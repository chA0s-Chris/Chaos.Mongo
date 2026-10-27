// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using Microsoft.Extensions.Logging;
using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// Confirms the revocation status of stored timestamp tokens with CRLs issued at least a grace period after the
/// token's time, which close the gap between the CRLs captured at stamping time and the token's time (ETSI EN 319 102-1;
/// RFC 5126).
/// </summary>
/// <remarks>
///     <para>
///     A token is due once its time plus <see cref="IntegrityAnchoringOptions.RevocationGracePeriod"/> has passed. The
///     due entries of one authority and certificate chain are confirmed together: the authority is asked once, with the
///     earliest time their CRLs must be issued at, and the CRLs it returns confirm every entry whose time they meet.
///     </para>
///     <para>
///     When a CRL available today was issued too early, the chain is not requested again before that CRL's next update
///     or <see cref="MaximumRetryDelay"/>, whichever comes first; this is not a failure. A CRL issued too early for the
///     chain's earliest due entry is too early for all later ones, so entries that become due meanwhile wait as well.
///     A failed attempt is counted and defers the chain by <see cref="MaximumRetryDelay"/>.
///     </para>
///     <para>
///     A run reads due entries from at most <see cref="MaximumAnchorsPerRun"/> anchors. Deferred entries are left out
///     before that bound applies, and entries of a waiting chain that become due are deferred to the same time, so a
///     chain that waits or keeps failing cannot keep the entries of other chains from being read.
///     </para>
/// </remarks>
internal sealed class TimestampRevocationConfirmer
{
    /// <summary>
    /// The default of <see cref="MaximumAnchorsPerRun"/>.
    /// </summary>
    internal const Int32 DefaultMaximumAnchorsPerRun = 1_000;

    /// <summary>
    /// The longest time a chain whose CRLs were issued too early waits for its next attempt, and the time a chain
    /// waits after a failed attempt. Many CAs issue CRLs daily but state a next update days ahead.
    /// </summary>
    internal static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromHours(1);

    private readonly IReadOnlyDictionary<String, ITimestampAuthority> _authorities;
    private readonly AnchorEvidenceStore _evidence;
    private readonly ILogger _logger;
    private readonly IntegrityAnchoringOptions _options;
    private readonly AnchorStore _store;
    private readonly TimeProvider _timeProvider;

    public TimestampRevocationConfirmer(AnchorStore store,
                                        AnchorEvidenceStore evidence,
                                        IReadOnlyList<ITimestampAuthority> authorities,
                                        IntegrityAnchoringOptions options,
                                        TimeProvider timeProvider,
                                        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(authorities);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _evidence = evidence;
        _authorities = authorities.ToDictionary(authority => authority.Name);
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Gets or sets the number of anchors whose due entries one run reads at most. Tests lower it.
    /// </summary>
    internal Int32 MaximumAnchorsPerRun { get; set; } = DefaultMaximumAnchorsPerRun;

    /// <summary>
    /// Confirms the due timestamp entries of the configured authorities. A failure for one chain is logged and counted
    /// on its entries and does not affect the others.
    /// </summary>
    /// <param name="ensureLock">Checks, and extends if needed, the run's lock before each authority is asked.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when every due chain was attempted or the lock was lost.</returns>
    public async Task ConfirmAsync(Func<CancellationToken, Task<Boolean>> ensureLock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ensureLock);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var due = await _store.FindUnconfirmedTimestampsAsync(now - _options.RevocationGracePeriod,
                                                              _authorities.Keys.ToList(),
                                                              now,
                                                              MaximumAnchorsPerRun,
                                                              cancellationToken);
        foreach (var chain in due.GroupBy(timestamp => (timestamp.Authority, ChainKey: GetChainKey(timestamp.CertificateChainHashes))))
        {
            var timestamps = chain.OrderBy(timestamp => timestamp.TimestampUtc).ToList();
            if (!await ensureLock(cancellationToken))
            {
                return;
            }

            // Entries that became due while their chain waits wait with it, which also keeps them out of the bounded
            // read until then.
            if (await _store.FindChainDeferralAsync(chain.Key.Authority, timestamps[0].CertificateChainHashes, now, cancellationToken) is { } deferredUntil)
            {
                await _store.DeferTimestampConfirmationAsync(timestamps, deferredUntil, cancellationToken);
                continue;
            }

            if (!await ConfirmChainAsync(_authorities[chain.Key.Authority], timestamps, ensureLock, cancellationToken))
            {
                return;
            }
        }
    }

    private static String GetChainKey(IReadOnlyList<Byte[]> certificateChainHashes)
        => String.Join(":", certificateChainHashes.Select(Convert.ToHexString));

    /// <summary>
    /// Asks the authority for CRLs of one chain and records the outcome on its due entries, unless the run's lock was
    /// lost while the authority answered.
    /// </summary>
    /// <returns><c>false</c> when the lock was lost and nothing was recorded.</returns>
    private async Task<Boolean> ConfirmChainAsync(ITimestampAuthority authority,
                                                  IReadOnlyList<UnconfirmedTimestamp> timestamps,
                                                  Func<CancellationToken, Task<Boolean>> ensureLock,
                                                  CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var issuedNotBefore = timestamps[0].TimestampUtc + _options.RevocationGracePeriod;
        try
        {
            var certificateChain = await ReadChainAsync(timestamps[0].CertificateChainHashes, cancellationToken);
            var result = await authority.GetRevocationListsAsync(certificateChain,
                                                                 new DateTimeOffset(issuedNotBefore, TimeSpan.Zero),
                                                                 cancellationToken);

            // The authority may answer after the lease ran out; another instance may then hold the lock.
            if (!await ensureLock(cancellationToken))
            {
                return false;
            }

            if (result.NextUpdate is { } nextUpdate)
            {
                var retryUtc = nextUpdate.UtcDateTime < now + MaximumRetryDelay ? nextUpdate.UtcDateTime : now + MaximumRetryDelay;
                await _store.DeferTimestampConfirmationAsync(timestamps, retryUtc, cancellationToken);
                _logger.LogDebug("The CRLs of timestamp authority {AuthorityName} were issued before {IssuedNotBefore:O}; " +
                                 "confirming {TimestampCount} timestamps is attempted again from {RetryUtc:O}",
                                 authority.Name, issuedNotBefore, timestamps.Count, retryUtc);
                return true;
            }

            await RecordStatusesAsync(authority, timestamps, certificateChain, result.Lists, issuedNotBefore, now, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Confirming the revocation status of {TimestampCount} timestamps of authority {AuthorityName} failed; " +
                                   "it is retried from {RetryUtc:O}",
                               timestamps.Count, authority.Name, now + MaximumRetryDelay);
            if (!await ensureLock(cancellationToken))
            {
                return false;
            }

            await _store.RecordTimestampConfirmationFailureAsync(timestamps, ex.Message, now, now + MaximumRetryDelay, cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// Reads a chain's certificates from the evidence store.
    /// </summary>
    /// <exception cref="AnchorEvidenceException">Thrown when a certificate is missing or does not match its hash.</exception>
    private async Task<IReadOnlyList<Byte[]>> ReadChainAsync(IReadOnlyList<Byte[]> certificateChainHashes, CancellationToken cancellationToken)
    {
        var certificates = new List<Byte[]>(certificateChainHashes.Count);
        foreach (var hash in certificateChainHashes)
        {
            certificates.Add(await _evidence.ReadAsync(hash, cancellationToken));
        }

        return certificates;
    }

    /// <summary>
    /// Confirms every entry whose required issue time the CRLs meet, as revoked when an entry of a CRL that counts revokes
    /// a certificate of the chain at or before the token's time. CRLs count by the rule verification applies
    /// (<see cref="RevocationEvidence"/>), matched with the chain's distribution points by content, so a recorded status
    /// can always be recomputed; points of indirect CRL issuers, whose CRLs cannot be verified, do not block
    /// confirmation. The CRLs are stored in the evidence store before the entries reference them.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// Thrown when a returned CRL was issued too early or is malformed, or when no returned CRL counts for a distribution
    /// point and no counting CRL revokes a certificate of the chain.
    /// </exception>
    private async Task RecordStatusesAsync(ITimestampAuthority authority,
                                           IReadOnlyList<UnconfirmedTimestamp> timestamps,
                                           IReadOnlyList<Byte[]> certificateChain,
                                           IReadOnlyList<Byte[]> lists,
                                           DateTime issuedNotBefore,
                                           DateTime now,
                                           CancellationToken cancellationToken)
    {
        // The authority's time checks are repeated, so the acceptance rule also holds for custom authorities; size remains
        // the authority's check.
        var issuedAt = DateTime.MaxValue;
        foreach (var crl in lists)
        {
            CertificateRevocationListHeader header;
            try
            {
                header = CertificateRevocationListHeader.Decode(crl);
                RevokedCertificate.DecodeAll(crl);
            }
            catch (AsnContentException ex)
            {
                throw new InvalidDataException($"Timestamp authority '{authority.Name}' returned data that is not a single DER-encoded CRL.", ex);
            }

            if (header.ThisUpdate.UtcDateTime < issuedNotBefore)
            {
                throw new InvalidDataException($"Timestamp authority '{authority.Name}' returned a CRL issued at {header.ThisUpdate:O}, " +
                                               $"before the required {issuedNotBefore:O}.");
            }

            if (header.NextUpdate is not { } nextUpdate || (nextUpdate.UtcDateTime < issuedNotBefore))
            {
                throw new InvalidDataException($"Timestamp authority '{authority.Name}' returned a CRL that states no next update, " +
                                               $"or one before the required {issuedNotBefore:O}.");
            }

            issuedAt = header.ThisUpdate.UtcDateTime < issuedAt ? header.ThisUpdate.UtcDateTime : issuedAt;
        }

        var chain = new X509Certificate2Collection();
        List<(UnconfirmedTimestamp Timestamp, String? Revoked)> statuses;
        try
        {
            foreach (var certificate in certificateChain)
            {
                chain.Add(X509CertificateLoader.LoadCertificate(certificate));
            }

            var sources = TimestampTokenValidation.GetCrlSources(certificateChain);
            var evaluation = RevocationEvidence.Evaluate(sources, lists, chain, new DateTimeOffset(issuedNotBefore, TimeSpan.Zero));
            statuses = timestamps.Where(timestamp => timestamp.TimestampUtc + _options.RevocationGracePeriod <= issuedAt)
                                 .Select(timestamp => (Timestamp: timestamp,
                                                       Revoked: RevocationEvidence.FindRevoked(chain, evaluation.Entries, new DateTimeOffset(timestamp.TimestampUtc, TimeSpan.Zero))
                                                                                  ?.Subject))
                                 .ToList();
            var unconfirmed = sources.Where((source, index) => source.Issuer is not null && !evaluation.Covered[index]).ToList();
            if (unconfirmed.Count > 0 && statuses.Any(status => status.Revoked is null))
            {
                throw new InvalidDataException($"Timestamp authority '{authority.Name}' returned no CRL that is complete for the distribution point at " +
                                               $"{String.Join(", ", unconfirmed[0].Locations)} and verifies against its issuer.");
            }
        }
        finally
        {
            foreach (var certificate in chain)
            {
                certificate.Dispose();
            }
        }

        var listHashes = new List<Byte[]>(lists.Count);
        foreach (var crl in lists)
        {
            listHashes.Add(await _evidence.StoreAsync(crl, cancellationToken));
        }

        await _store.ConfirmTimestampsAsync(statuses.Select(status => (status.Timestamp,
                                                                       status.Revoked is null ? TimestampRevocationStatus.Confirmed : TimestampRevocationStatus.Revoked))
                                                    .ToList(),
                                            listHashes,
                                            now,
                                            cancellationToken);
        foreach (var (timestamp, revoked) in statuses.Where(status => status.Revoked is not null))
        {
            IntegrityAnchoringDiagnostics.RecordRevokedTimestamp();
            _logger.LogError("A CRL of timestamp authority {AuthorityName} lists the certificate '{Certificate}' as revoked at or before the time " +
                             "{TimestampUtc:O} of the token on anchor {AnchorSequence}; the token is no evidence for that time",
                             authority.Name, revoked, timestamp.TimestampUtc, timestamp.AnchorSequence);
        }

        _logger.LogDebug("Confirmed the revocation status of {TimestampCount} timestamps of authority {AuthorityName}", statuses.Count, authority.Name);
    }
}
