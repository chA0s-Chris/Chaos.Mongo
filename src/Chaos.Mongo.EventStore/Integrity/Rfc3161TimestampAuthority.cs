// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using System.Formats.Asn1;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// Obtains timestamp tokens from an RFC 3161 authority over HTTP and validates them on receipt.
/// </summary>
/// <remarks>
/// Imprint, nonce, signature and ESSCertID are validated by the <c>System.Security.Cryptography.Pkcs</c> types, the
/// certificate chain by <see cref="X509Chain"/> against the configured trust. The signing certificate must be reserved
/// for timestamping and permit signatures, a <c>tsa</c> name in the token must correspond to one of its subject names,
/// and the token must carry no signature besides the authority's, be signed with a collision-resistant digest and a key
/// of adequate size, and be issued under the configured policy. A token is
/// returned only together with the CRLs of every distribution point its chain names; a CRL that cannot be downloaded
/// or is not a current CRL of the expected issuer fails the attempt, and the attempt's HTTP work as a whole is bounded by
/// <see cref="Rfc3161TimestampAuthorityOptions.AttemptTimeout"/> and every response body by
/// <see cref="Rfc3161TimestampAuthorityOptions.MaximumResponseSize"/>.
/// </remarks>
internal sealed class Rfc3161TimestampAuthority : ITimestampAuthority
{
    private const Int32 DigestLength = 32;
    private const Int32 NonceLength = 16;
    private const Int32 ReadChunkSize = 81920;
    private const String TimestampQueryMediaType = "application/timestamp-query";

    private readonly HttpClient _httpClient;
    private readonly Rfc3161TimestampAuthorityOptions _options;
    private readonly Oid? _policyId;
    private readonly TimeProvider _timeProvider;

    public Rfc3161TimestampAuthority(Rfc3161TimestampAuthorityOptions options, IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Name, nameof(options));
        ArgumentNullException.ThrowIfNull(options.Uri, nameof(options));
        ArgumentNullException.ThrowIfNull(options.HttpClientFactory, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumClockSkew, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.RevocationCheckTimeout, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.AttemptTimeout, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumResponseSize, nameof(options));
        if (!options.Uri.IsAbsoluteUri || ((options.Uri.Scheme != Uri.UriSchemeHttp) && (options.Uri.Scheme != Uri.UriSchemeHttps)))
        {
            throw new ArgumentException("The timestamp authority URI must be an absolute HTTP or HTTPS URI.", nameof(options));
        }

        if (options.PolicyId is not null)
        {
            try
            {
                new AsnWriter(AsnEncodingRules.DER).WriteObjectIdentifier(options.PolicyId);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException("The timestamp authority policy ID must be an object identifier.", nameof(options), ex);
            }

            _policyId = new Oid(options.PolicyId);
        }

        _options = options;
        _timeProvider = serviceProvider.GetService(typeof(TimeProvider)) as TimeProvider ?? TimeProvider.System;
        _httpClient = options.HttpClientFactory(serviceProvider) ??
                      throw new InvalidOperationException($"The HTTP client factory of timestamp authority '{options.Name}' returned null.");
    }

    /// <inheritdoc/>
    public String Name => _options.Name;

    /// <summary>
    /// Gets a value indicating whether an exception is a failed or timed-out HTTP exchange. Bodies are read after the
    /// headers arrive, so a connection lost while reading one surfaces as an <see cref="IOException"/>.
    /// </summary>
    private static Boolean IsTransportFailure(Exception exception, CancellationToken cancellationToken)
        => exception is HttpRequestException or IOException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    private static Byte[] ReadToken(Byte[] response)
    {
        // TimeStampResp ::= SEQUENCE { status PKIStatusInfo, timeStampToken TimeStampToken OPTIONAL }
        var sequence = new AsnReader(response, AsnEncodingRules.BER).ReadSequence();
        sequence.ReadEncodedValue();
        return sequence.ReadEncodedValue().ToArray();
    }

    /// <summary>
    /// Checks that a download is usable evidence: an HTTP location must point to a single DER-encoded CRL (RFC 5280,
    /// section 4.2.1.13), issued by the certificate's issuer unless the point names another CRL issuer, whose CRLs
    /// are not checked here. It must state its next update (RFC 5280, section 5.1.2.5), which must not lie before
    /// <paramref name="validAt"/>, and must not claim to be issued later than the local time allows. A CRL issued after
    /// the token's time is accepted: it lists every revocation up to its issue, so it is stronger evidence for the
    /// token's time.
    /// </summary>
    /// <returns>The CRL's issue and next update when it is usable; otherwise the reason it is not.</returns>
    private (CrlTimes? Times, InvalidDataException? Error) CheckCrl(Byte[] crl, Uri location, Byte[]? issuer, DateTimeOffset validAt, String validAtName)
    {
        CertificateRevocationListHeader header;
        try
        {
            header = CertificateRevocationListHeader.Decode(crl);
        }
        catch (AsnContentException ex)
        {
            return (null, new InvalidDataException($"The data at {location} is not a single DER-encoded CRL.", ex));
        }

        if (issuer is not null && !header.Issuer.Span.SequenceEqual(issuer))
        {
            return (null, new InvalidDataException($"The CRL at {location} is not issued by the issuer of the certificate that names it."));
        }

        if (header.NextUpdate is not { } nextUpdate)
        {
            return (null, new InvalidDataException($"The CRL at {location} states no next update."));
        }

        var latestIssue = _timeProvider.GetUtcNow() + _options.MaximumClockSkew;
        if (header.ThisUpdate > latestIssue)
        {
            return (null, new InvalidDataException($"The CRL at {location} was issued at {header.ThisUpdate:O}, after the local time allows."));
        }

        return nextUpdate < validAt
            ? (null, new InvalidDataException($"The CRL at {location} expired at {nextUpdate:O}, before {validAtName} {validAt:O}."))
            : (new CrlTimes(header.ThisUpdate, nextUpdate), null);
    }

    /// <summary>
    /// Downloads the CRL of a distribution point from the first of its locations that serves a usable one, issued at or
    /// after <paramref name="issuedNotBefore"/> when given. A usable CRL issued earlier is skipped, so another location
    /// may still serve a later one.
    /// </summary>
    /// <returns>The CRL, or the earliest next update of the usable CRLs that were issued too early.</returns>
    /// <exception cref="TimestampAuthorityException">Thrown when no location serves a usable CRL.</exception>
    private async Task<DownloadedCrl> DownloadCrlAsync(CrlSource source,
                                                       DateTimeOffset validAt,
                                                       String validAtName,
                                                       DateTimeOffset? issuedNotBefore,
                                                       CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        DateTimeOffset? tooEarlyNextUpdate = null;
        foreach (var location in source.Locations)
        {
            try
            {
                using var response = await _httpClient.GetAsync(location, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    lastError = new HttpRequestException($"HTTP status {(Int32)response.StatusCode}.", null, response.StatusCode);
                    continue;
                }

                var crl = await ReadContentAsync(response.Content, cancellationToken);
                if (crl is null)
                {
                    lastError = new InvalidDataException($"The CRL at {location} exceeds the maximum response size of {_options.MaximumResponseSize} bytes.");
                    continue;
                }

                var (times, error) = CheckCrl(crl, location, source.Issuer, validAt, validAtName);
                if (times is null)
                {
                    lastError = error;
                    continue;
                }

                if (times.ThisUpdate < issuedNotBefore)
                {
                    tooEarlyNextUpdate = tooEarlyNextUpdate < times.NextUpdate ? tooEarlyNextUpdate : times.NextUpdate;
                    continue;
                }

                return new DownloadedCrl(crl, null);
            }
            catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
            {
                lastError = ex;
            }
        }

        return tooEarlyNextUpdate is { } nextUpdate
            ? new DownloadedCrl(null, nextUpdate)
            : throw Fail($"the CRL at {String.Join(", ", source.Locations)} could not be downloaded.", lastError);
    }

    private TimestampAuthorityException Fail(String message, Exception? innerException = null)
        => new(_options.Name, message, innerException);

    private async Task<RevocationLists> GetRevocationListsCoreAsync(IReadOnlyList<Byte[]> certificateChain,
                                                                    DateTimeOffset issuedNotBefore,
                                                                    CancellationToken cancellationToken)
    {
        var crls = new List<Byte[]>();
        DateTimeOffset? tooEarlyNextUpdate = null;
        IReadOnlyList<CrlSource> sources;
        try
        {
            sources = TimestampTokenValidation.GetCrlSources(certificateChain);
        }
        catch (TimestampTokenValidationException ex)
        {
            throw Fail(ex.Message, ex.InnerException);
        }

        foreach (var source in sources)
        {
            var downloaded = await DownloadCrlAsync(source, issuedNotBefore, "the required issue time", issuedNotBefore, cancellationToken);
            if (downloaded.Crl is { } crl)
            {
                crls.Add(crl);
            }
            else
            {
                tooEarlyNextUpdate = tooEarlyNextUpdate < downloaded.NextUpdate ? tooEarlyNextUpdate : downloaded.NextUpdate;
            }
        }

        return tooEarlyNextUpdate is { } nextUpdate ? RevocationLists.IssuedTooEarly(nextUpdate) : RevocationLists.Issued(crls);
    }

    /// <summary>
    /// Reads a response body of at most <see cref="Rfc3161TimestampAuthorityOptions.MaximumResponseSize"/> bytes, so
    /// the bound holds whatever buffer limit the HTTP client has.
    /// </summary>
    /// <returns>The body, or <c>null</c> when it is larger.</returns>
    private async Task<Byte[]?> ReadContentAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var limit = _options.MaximumResponseSize;
        if (content.Headers.ContentLength > limit)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var body = new MemoryStream();
        var chunk = new Byte[ReadChunkSize];
        Int32 read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (body.Length + read > limit)
            {
                return null;
            }

            body.Write(chunk, 0, read);
        }

        return body.ToArray();
    }

    private async Task<Byte[]> RequestAsync(Byte[] query, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Uri);
        request.Content = new ByteArrayContent(query);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(TimestampQueryMediaType);
        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw Fail($"the timestamp request failed with HTTP status {(Int32)response.StatusCode}.");
            }

            return await ReadContentAsync(response.Content, cancellationToken) ??
                   throw Fail($"the timestamp response exceeds the maximum response size of {_options.MaximumResponseSize} bytes.");
        }
        catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
        {
            throw Fail("the timestamp request failed.", ex);
        }
    }

    /// <summary>
    /// Runs one attempt bounded by <see cref="Rfc3161TimestampAuthorityOptions.AttemptTimeout"/>.
    /// </summary>
    private async Task<T> RunAttemptAsync<T>(Func<CancellationToken, Task<T>> attempt, CancellationToken cancellationToken)
    {
        using var attemptSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptSource.CancelAfter(_options.AttemptTimeout);
        try
        {
            return await attempt(attemptSource.Token);
        }
        catch (OperationCanceledException ex) when (attemptSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw Fail($"the attempt exceeded the attempt timeout of {_options.AttemptTimeout}.", ex);
        }
    }

    private async Task<TimestampToken> TimestampCoreAsync(ReadOnlyMemory<Byte> digest, HashAlgorithmName algorithm, CancellationToken cancellationToken)
    {
        var request = Rfc3161TimestampRequest.CreateFromHash(digest,
                                                             algorithm,
                                                             _policyId,
                                                             RandomNumberGenerator.GetBytes(NonceLength),
                                                             true);
        var (token, rawToken) = ValidateResponse(request, await RequestAsync(request.Encode(), cancellationToken));
        if (!token.VerifySignatureForHash(digest.Span, algorithm, out var signer))
        {
            throw Fail("the token's signature or signing-certificate attribute is invalid.");
        }

        ValidatedChain chain;
        using (signer)
        {
            try
            {
                TimestampTokenValidation.ValidateContent(token);
                TimestampTokenValidation.RequireDer(rawToken);
                TimestampTokenValidation.ValidatePolicy(token, _policyId);
                ValidateTime(token);
                TimestampTokenValidation.ValidateSigner(signer);
                TimestampTokenValidation.ValidateAlgorithms(token, signer);
                TimestampTokenValidation.ValidateAuthorityName(token, signer);
                chain = TimestampTokenValidation.BuildChain(token,
                                                            signer,
                                                            new ChainSettings(_options.TrustedRoots,
                                                                              _options.RevocationMode,
                                                                              _options.RevocationCheckTimeout,
                                                                              []));
            }
            catch (TimestampTokenValidationException ex)
            {
                throw Fail(ex.Message, ex.InnerException);
            }
        }

        var crls = new List<Byte[]>();
        foreach (var source in chain.CrlSources)
        {
            var downloaded = await DownloadCrlAsync(source, token.TokenInfo.Timestamp, "the token's time", null, cancellationToken);
            crls.Add(downloaded.Crl ?? throw new InvalidOperationException("A CRL download without a minimum issue time returned no CRL."));
        }

        return new TimestampToken(rawToken, chain.Certificates, crls, token.TokenInfo.Timestamp);
    }

    private (Rfc3161TimestampToken Token, Byte[] RawToken) ValidateResponse(Rfc3161TimestampRequest request, Byte[] response)
    {
        try
        {
            var token = request.ProcessResponse(response, out var bytesConsumed);
            if (bytesConsumed != response.Length)
            {
                throw Fail("the response contains data after the timestamp response.");
            }

            return (token, ReadToken(response));
        }
        catch (CryptographicException ex)
        {
            throw Fail("the response does not match the request.", ex);
        }
    }

    /// <summary>
    /// Bounds the token's time against the local time: the chain is validated at the token's time, so a backdated or
    /// future-dated token from a key whose certificate expired must not pass.
    /// </summary>
    private void ValidateTime(Rfc3161TimestampToken token)
    {
        var skew = (token.TokenInfo.Timestamp - _timeProvider.GetUtcNow()).Duration();
        if (skew > _options.MaximumClockSkew)
        {
            throw Fail($"the token's time {token.TokenInfo.Timestamp:O} differs from the local time by {skew}, " +
                       $"more than the allowed {_options.MaximumClockSkew}.");
        }
    }

    /// <inheritdoc/>
    public Task<RevocationLists> GetRevocationListsAsync(IReadOnlyList<Byte[]> certificateChain,
                                                         DateTimeOffset issuedNotBefore,
                                                         CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(certificateChain);
        return RunAttemptAsync(attemptToken => GetRevocationListsCoreAsync(certificateChain, issuedNotBefore, attemptToken), cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<TimestampToken> TimestampAsync(ReadOnlyMemory<Byte> digest, HashAlgorithmName algorithm, CancellationToken cancellationToken)
    {
        if (algorithm != HashAlgorithmName.SHA256)
        {
            throw new ArgumentException("Only SHA-256 digests can be timestamped.", nameof(algorithm));
        }

        if (digest.Length != DigestLength)
        {
            throw new ArgumentException($"A SHA-256 digest has {DigestLength} bytes.", nameof(digest));
        }

        return await RunAttemptAsync(attemptToken => TimestampCoreAsync(digest, algorithm, attemptToken), cancellationToken);
    }

    /// <summary>
    /// The issue and next update of a usable CRL.
    /// </summary>
    private sealed record CrlTimes(DateTimeOffset ThisUpdate, DateTimeOffset NextUpdate);

    /// <summary>
    /// A downloaded CRL, or the earliest next update of the usable CRLs that were issued too early.
    /// </summary>
    private sealed record DownloadedCrl(Byte[]? Crl, DateTimeOffset? NextUpdate);
}
