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
    private const String EcPublicKeyOid = "1.2.840.10045.2.1";
    private const Int32 MinimumEcKeySize = 256;
    private const Int32 MinimumRsaKeySize = 2048;
    private const Int32 NonceLength = 16;
    private const Int32 ReadChunkSize = 81920;
    private const String RsaPssPublicKeyOid = "1.2.840.113549.1.1.10";
    private const String RsaPublicKeyOid = "1.2.840.113549.1.1.1";
    private const String TimeStampingPurposeOid = "1.3.6.1.5.5.7.3.8";
    private const String TimestampQueryMediaType = "application/timestamp-query";

    /// <summary>
    /// MD2, MD4, MD5 and SHA-1, which are not collision resistant.
    /// </summary>
    private static readonly HashSet<String> WeakDigestAlgorithmOids =
    [
        "1.2.840.113549.2.2",
        "1.2.840.113549.2.4",
        "1.2.840.113549.2.5",
        "1.3.14.3.2.26"
    ];

    /// <summary>
    /// Signature algorithms that name one of <see cref="WeakDigestAlgorithmOids"/>: RSA with MD2, MD4, MD5 and SHA-1,
    /// ECDSA with SHA-1 and DSA with SHA-1.
    /// </summary>
    private static readonly HashSet<String> WeakSignatureAlgorithmOids =
    [
        "1.2.840.113549.1.1.2",
        "1.2.840.113549.1.1.3",
        "1.2.840.113549.1.1.4",
        "1.2.840.113549.1.1.5",
        "1.3.14.3.2.29",
        "1.2.840.10045.4.1",
        "1.2.840.10040.4.3"
    ];

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
    /// Gets the size of an RSA or ECDSA key and the minimum this authority requires for it; other key types are not
    /// size-checked. The size is <c>null</c> when the platform cannot read the key, as for RSASSA-PSS keys today.
    /// </summary>
    private static (Int32? KeySize, Int32 Minimum) GetKeySize(X509Certificate2 certificate)
    {
        switch (certificate.PublicKey.Oid.Value)
        {
            case RsaPublicKeyOid:
            case RsaPssPublicKeyOid:
                using (var key = certificate.GetRSAPublicKey())
                {
                    return (key?.KeySize, MinimumRsaKeySize);
                }
            case EcPublicKeyOid:
                using (var key = certificate.GetECDsaPublicKey())
                {
                    return (key?.KeySize, MinimumEcKeySize);
                }
            default:
                return (null, 0);
        }
    }

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

    private ValidatedChain BuildChain(Rfc3161TimestampToken token, X509Certificate2 signer)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = _options.RevocationMode;
        chain.ChainPolicy.UrlRetrievalTimeout = _options.RevocationCheckTimeout;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(TimeStampingPurposeOid));
        chain.ChainPolicy.VerificationTime = token.TokenInfo.Timestamp.UtcDateTime;
        var tokenCertificates = token.AsSignedCms().Certificates;
        chain.ChainPolicy.ExtraStore.AddRange(tokenCertificates);
        if (_options.TrustedRoots.Count > 0)
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(_options.TrustedRoots);
        }

        var elements = new List<X509Certificate2>();
        try
        {
            if (!chain.Build(signer))
            {
                var statuses = String.Join(", ", chain.ChainStatus.Select(status => status.Status));
                throw Fail($"the token's certificate chain is not trusted ({statuses}).");
            }

            // Everything needed later is copied out, so the certificates can be disposed here.
            elements.AddRange(chain.ChainElements.Select(element => element.Certificate));
            var crlSources = GetCrlSources(elements.Take(elements.Count - 1));
            return new ValidatedChain(elements.Select(certificate => certificate.RawData).ToList(), crlSources);
        }
        catch (AsnContentException ex)
        {
            throw Fail("a certificate of the token's chain has a malformed CRL distribution point.", ex);
        }
        finally
        {
            foreach (var certificate in elements.Concat(tokenCertificates))
            {
                certificate.Dispose();
            }
        }
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

    /// <summary>
    /// Gets the CRLs a certificate's distribution points name, with their HTTP locations and, unless a point names
    /// another CRL issuer, the issuer they must come from. Points without an HTTP location are skipped, but a
    /// certificate that names distribution points must yield at least one CRL that covers all revocation reasons
    /// (RFC 5280, section 4.2.1.13); points limited to some reasons are captured beside it.
    /// </summary>
    private IReadOnlyList<CrlSource> GetCrlSources(X509Certificate2 certificate)
    {
        var points = CrlDistributionPoints.GetHttpDistributionPoints(certificate);
        if (!points.Any(point => point.CoversAllReasons) && CrlDistributionPoints.NamesDistributionPoints(certificate))
        {
            throw Fail($"the certificate '{certificate.Subject}' names CRL distribution points, but none with an HTTP or HTTPS location " +
                       "covers all revocation reasons.");
        }

        return points.Select(point => new CrlSource(point.Locations, point.NamesCrlIssuer ? null : certificate.IssuerName.RawData)).ToList();
    }

    /// <summary>
    /// Gets the distinct CRLs the distribution points of certificates name.
    /// </summary>
    private IReadOnlyList<CrlSource> GetCrlSources(IEnumerable<X509Certificate2> certificates)
        => certificates.SelectMany(GetCrlSources)
                       .DistinctBy(source => (String.Join(" ", source.Locations.Select(uri => uri.AbsoluteUri)),
                                              Convert.ToHexString(source.Issuer ?? [])))
                       .ToList();

    /// <summary>
    /// Gets the distinct CRLs the distribution points of a stored chain's non-root certificates name.
    /// </summary>
    private IReadOnlyList<CrlSource> GetCrlSources(IReadOnlyList<Byte[]> certificateChain)
    {
        var certificates = new List<X509Certificate2>();
        try
        {
            foreach (var certificate in certificateChain.Take(certificateChain.Count - 1))
            {
                certificates.Add(X509CertificateLoader.LoadCertificate(certificate));
            }

            return GetCrlSources(certificates);
        }
        catch (CryptographicException ex)
        {
            throw Fail("a certificate of the stored chain is malformed.", ex);
        }
        catch (AsnContentException ex)
        {
            throw Fail("a certificate of the stored chain has a malformed CRL distribution point.", ex);
        }
        finally
        {
            foreach (var certificate in certificates)
            {
                certificate.Dispose();
            }
        }
    }

    private async Task<RevocationLists> GetRevocationListsCoreAsync(IReadOnlyList<Byte[]> certificateChain,
                                                                    DateTimeOffset issuedNotBefore,
                                                                    CancellationToken cancellationToken)
    {
        var crls = new List<Byte[]>();
        DateTimeOffset? tooEarlyNextUpdate = null;
        foreach (var source in GetCrlSources(certificateChain))
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
            ValidateContent(token);
            ValidatePolicy(token);
            ValidateTime(token);
            ValidateSigner(signer);
            ValidateAlgorithms(token, signer);
            ValidateAuthorityName(token, signer);
            chain = BuildChain(token, signer);
        }

        var crls = new List<Byte[]>();
        foreach (var source in chain.CrlSources)
        {
            var downloaded = await DownloadCrlAsync(source, token.TokenInfo.Timestamp, "the token's time", null, cancellationToken);
            crls.Add(downloaded.Crl ?? throw new InvalidOperationException("A CRL download without a minimum issue time returned no CRL."));
        }

        return new TimestampToken(rawToken, chain.Certificates, crls, token.TokenInfo.Timestamp);
    }

    /// <summary>
    /// Requires the token to be signed with a collision-resistant digest and the signing key to have at least 2048 bits
    /// for RSA or 256 bits for ECDSA, so a token is not weak evidence from the day it is issued (ETSI TS 119 312).
    /// The Pkcs types accept any algorithm. The signing-certificate identifier may still use SHA-1 (ESSCertID), which
    /// only names the certificate.
    /// </summary>
    private void ValidateAlgorithms(Rfc3161TimestampToken token, X509Certificate2 signer)
    {
        var signerInfo = token.AsSignedCms().SignerInfos[0];
        if (WeakDigestAlgorithmOids.Contains(signerInfo.DigestAlgorithm.Value!) ||
            WeakSignatureAlgorithmOids.Contains(signerInfo.SignatureAlgorithm.Value!))
        {
            throw Fail($"the token is signed with a weak algorithm (digest {signerInfo.DigestAlgorithm.Value}, signature " +
                       $"{signerInfo.SignatureAlgorithm.Value}); MD5 and SHA-1 are not accepted.");
        }

        var (keySize, minimum) = GetKeySize(signer);
        if (keySize is null && minimum > 0)
        {
            throw Fail("the signing key's size could not be determined.");
        }

        if (keySize < minimum)
        {
            throw Fail($"the signing key has {keySize} bits, fewer than the required {minimum}.");
        }
    }

    /// <summary>
    /// Requires a <c>tsa</c> name in the token to correspond to a subject name of the signing certificate
    /// (RFC 3161, section 2.4.2); a token without one is accepted.
    /// </summary>
    private void ValidateAuthorityName(Rfc3161TimestampToken token, X509Certificate2 signer)
    {
        if (token.TokenInfo.GetTimestampAuthorityName() is not { } authorityName)
        {
            return;
        }

        try
        {
            if (!TimestampAuthorityName.Matches(authorityName, signer))
            {
                throw Fail("the token's TSA name does not correspond to a subject name of the signing certificate.");
            }
        }
        catch (AsnContentException ex)
        {
            throw Fail("the token's TSA name is malformed.", ex);
        }
    }

    /// <summary>
    /// Requires the signed content to be exactly one DER <c>TSTInfo</c> and the token to carry no signature besides
    /// the authority's (RFC 3161, section 2.4.2). The Pkcs decoder does not reject data that follows the
    /// <c>TSTInfo</c>, although the signature covers that data, and it requires one signer but ignores
    /// countersignatures.
    /// </summary>
    private void ValidateContent(Rfc3161TimestampToken token)
    {
        var signedCms = token.AsSignedCms();
        var content = signedCms.ContentInfo.Content;
        if (!Rfc3161TimestampTokenInfo.TryDecode(content, out _, out var bytesConsumed) || bytesConsumed != content.Length)
        {
            throw Fail("the token's signed content is not exactly one TSTInfo.");
        }

        if (signedCms.SignerInfos[0].CounterSignerInfos.Count > 0)
        {
            throw Fail("the token carries a countersignature besides the authority's signature.");
        }
    }

    /// <summary>
    /// Requires the token to be issued under the configured policy (RFC 3161, section 2.2). The Pkcs types do not
    /// compare the token's policy with the requested one.
    /// </summary>
    private void ValidatePolicy(Rfc3161TimestampToken token)
    {
        if (_policyId is not null && token.TokenInfo.PolicyId.Value != _policyId.Value)
        {
            throw Fail($"the token is issued under policy {token.TokenInfo.PolicyId.Value}, not the required policy {_policyId.Value}.");
        }
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
    /// Requires the signing certificate to be reserved for timestamping (RFC 3161, section 2.3) and, when it carries a
    /// key usage extension, to permit signatures (RFC 5280, section 4.2.1.3). Both extensions are decoded on first
    /// access, so an undecodable one fails the attempt here. A certificate must not carry an extension more than once
    /// (RFC 5280, section 4.2); the extension accessors would read only one instance.
    /// </summary>
    private void ValidateSigner(X509Certificate2 signer)
    {
        var duplicate = signer.Extensions.GroupBy(extension => extension.Oid?.Value)
                              .FirstOrDefault(extensions => extensions.Count() > 1);
        if (duplicate is not null)
        {
            throw Fail($"the signing certificate carries the extension {duplicate.Key} more than once.");
        }

        List<String?> purposes;
        X509KeyUsageFlags? keyUsages;
        try
        {
            purposes = signer.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                             .SelectMany(extension => extension.EnhancedKeyUsages.Cast<Oid>())
                             .Select(purpose => purpose.Value)
                             .ToList();
            keyUsages = signer.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault()?.KeyUsages;
        }
        catch (CryptographicException ex)
        {
            throw Fail("the signing certificate's usage extensions are malformed.", ex);
        }

        if (purposes.Count == 0 || purposes.Any(purpose => purpose != TimeStampingPurposeOid))
        {
            throw Fail("the signing certificate's extended key usage must contain only id-kp-timeStamping.");
        }

        if (keyUsages is { } usages && (usages & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) == 0)
        {
            throw Fail("the signing certificate's key usage permits neither digitalSignature nor nonRepudiation.");
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
    /// A CRL to capture: the alternative locations of a distribution point and the issuer its CRL must come from, or
    /// <c>null</c> when the point names another CRL issuer.
    /// </summary>
    private sealed record CrlSource(IReadOnlyList<Uri> Locations, Byte[]? Issuer);

    /// <summary>
    /// The issue and next update of a usable CRL.
    /// </summary>
    private sealed record CrlTimes(DateTimeOffset ThisUpdate, DateTimeOffset NextUpdate);

    /// <summary>
    /// A downloaded CRL, or the earliest next update of the usable CRLs that were issued too early.
    /// </summary>
    private sealed record DownloadedCrl(Byte[]? Crl, DateTimeOffset? NextUpdate);

    private sealed record ValidatedChain(IReadOnlyList<Byte[]> Certificates, IReadOnlyList<CrlSource> CrlSources);
}
