// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Resolves the evidence a completed anchor references, so an anchor handed out carries its tokens, certificate chains
/// and CRLs and is self-contained.
/// </summary>
/// <remarks>
/// Anchors are resolved only when they are handed out, by the completion hook and the verification API; reading and
/// looking up anchors does not read their evidence.
/// </remarks>
internal sealed class AnchorEvidenceResolver
{
    private readonly AnchorEvidenceStore _evidence;

    public AnchorEvidenceResolver(IMongoHelper mongoHelper, IntegrityAnchoringOptions options)
    {
        _evidence = new AnchorEvidenceStore(mongoHelper, options);
    }

    /// <summary>
    /// Resolves a completed anchor with its timestamps and their evidence.
    /// </summary>
    /// <param name="anchor">The completed anchor.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The self-contained anchor.</returns>
    /// <exception cref="ArgumentException">Thrown when the anchor is not completed.</exception>
    /// <exception cref="AnchorEvidenceException">
    /// Thrown when referenced evidence is missing or does not match its hash.
    /// </exception>
    public async Task<CompletedAnchor> ResolveAsync(AnchorRecord anchor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if (anchor.Status != AnchorStatus.Completed)
        {
            throw new ArgumentException("The anchor is not completed.", nameof(anchor));
        }

        // Entries of several authorities, or several distribution points of one chain, often share evidence.
        var resolved = new Dictionary<String, Byte[]>();
        var timestamps = new List<AnchorTimestamp>(anchor.Timestamps.Count);
        foreach (var timestamp in anchor.Timestamps)
        {
            timestamps.Add(new AnchorTimestamp(timestamp.Authority,
                                               await ReadAsync(timestamp.TokenHash, resolved, cancellationToken),
                                               await ReadAllAsync(timestamp.CertificateChainHashes, resolved, cancellationToken),
                                               await ReadAllAsync(timestamp.CertificateRevocationListHashes, resolved, cancellationToken),
                                               timestamp.TimestampUtc,
                                               timestamp.ObtainedUtc)
            {
                RevocationStatus = timestamp.RevocationStatus,
                RevocationStatusUtc = timestamp.RevocationStatusUtc,
                ConfirmingRevocationLists = await ReadAllAsync(timestamp.ConfirmingRevocationListHashes, resolved, cancellationToken),
                ConfirmationNotBeforeUtc = timestamp.ConfirmationNotBeforeUtc,
                ConfirmationFailure = timestamp.ConfirmationFailure
            });
        }

        return CompletedAnchor.From(anchor, timestamps);
    }

    private async Task<IReadOnlyList<Byte[]>> ReadAllAsync(IReadOnlyList<Byte[]> hashes, Dictionary<String, Byte[]> resolved, CancellationToken cancellationToken)
    {
        var items = new List<Byte[]>(hashes.Count);
        foreach (var hash in hashes)
        {
            items.Add(await ReadAsync(hash, resolved, cancellationToken));
        }

        return items;
    }

    private async Task<Byte[]> ReadAsync(Byte[] hash, Dictionary<String, Byte[]> resolved, CancellationToken cancellationToken)
    {
        var key = Convert.ToHexString(hash);
        if (!resolved.TryGetValue(key, out var data))
        {
            data = await _evidence.ReadAsync(hash, cancellationToken);
            resolved[key] = data;
        }

        return data;
    }
}
