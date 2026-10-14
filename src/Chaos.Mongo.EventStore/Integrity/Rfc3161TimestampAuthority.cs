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
/// for timestamping and permit signatures, and a <c>tsa</c> name in the token must correspond to one of its subject
/// names. A token is returned only together with the CRLs of every distribution point its chain names; a CRL that
/// cannot be downloaded fails the attempt, and the attempt's HTTP work as a whole is bounded by
/// <see cref="Rfc3161TimestampAuthorityOptions.AttemptTimeout"/> and every response body by
/// <see cref="Rfc3161TimestampAuthorityOptions.MaximumResponseSize"/>.
/// </remarks>
internal sealed class Rfc3161TimestampAuthority : ITimestampAuthority
{
    private const Int32 DigestLength = 32;
    private const Int32 NonceLength = 16;
    private const Int32 ReadChunkSize = 81920;
    private const String TimeStampingPurposeOid = "1.3.6.1.5.5.7.3.8";
    private const String TimestampQueryMediaType = "application/timestamp-query";
    private readonly HttpClient _httpClient;
    private readonly Rfc3161TimestampAuthorityOptions _options;
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
            var distributionPoints = elements.Take(elements.Count - 1)
                                             .SelectMany(GetDistributionPoints)
                                             .DistinctBy(locations => String.Join(" ", locations.Select(uri => uri.AbsoluteUri)))
                                             .ToList();
            return new ValidatedChain(elements.Select(certificate => certificate.RawData).ToList(), distributionPoints);
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

    private async Task<Byte[]> DownloadCrlAsync(IReadOnlyList<Uri> locations, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (var location in locations)
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

                if (crl.Length > 0)
                {
                    return crl;
                }

                lastError = new InvalidDataException($"The CRL at {location} is empty.");
            }
            catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
            {
                lastError = ex;
            }
        }

        throw Fail($"the CRL at {String.Join(", ", locations)} could not be downloaded.", lastError);
    }

    private TimestampAuthorityException Fail(String message, Exception? innerException = null)
        => new(_options.Name, message, innerException);

    /// <summary>
    /// Gets the HTTP locations of a certificate's distribution points. Points without one are skipped, but a
    /// certificate that names distribution points must yield at least one CRL that covers all revocation reasons
    /// (RFC 5280, section 4.2.1.13); points limited to some reasons are captured beside it.
    /// </summary>
    private IReadOnlyList<IReadOnlyList<Uri>> GetDistributionPoints(X509Certificate2 certificate)
    {
        var points = CrlDistributionPoints.GetHttpDistributionPoints(certificate);
        if (!points.Any(point => point.CoversAllReasons) && CrlDistributionPoints.NamesDistributionPoints(certificate))
        {
            throw Fail($"the certificate '{certificate.Subject}' names CRL distribution points, but none with an HTTP or HTTPS location " +
                       "covers all revocation reasons.");
        }

        return points.Select(point => point.Locations).ToList();
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

    private async Task<TimestampToken> TimestampCoreAsync(ReadOnlyMemory<Byte> digest, HashAlgorithmName algorithm, CancellationToken cancellationToken)
    {
        var request = Rfc3161TimestampRequest.CreateFromHash(digest,
                                                             algorithm,
                                                             nonce: RandomNumberGenerator.GetBytes(NonceLength),
                                                             requestSignerCertificates: true);
        var (token, rawToken) = ValidateResponse(request, await RequestAsync(request.Encode(), cancellationToken));
        if (!token.VerifySignatureForHash(digest.Span, algorithm, out var signer))
        {
            throw Fail("the token's signature or signing-certificate attribute is invalid.");
        }

        ValidatedChain chain;
        using (signer)
        {
            ValidateContent(token);
            ValidateTime(token);
            ValidateSigner(signer);
            ValidateAuthorityName(token, signer);
            chain = BuildChain(token, signer);
        }

        var crls = new List<Byte[]>();
        foreach (var locations in chain.DistributionPoints)
        {
            crls.Add(await DownloadCrlAsync(locations, cancellationToken));
        }

        return new TimestampToken(rawToken, chain.Certificates, crls, token.TokenInfo.Timestamp);
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
    /// Requires the signed content to be exactly one DER <c>TSTInfo</c> (RFC 3161, section 2.4.2). The Pkcs decoder
    /// does not reject data that follows it, although the signature covers that data.
    /// </summary>
    private void ValidateContent(Rfc3161TimestampToken token)
    {
        var content = token.AsSignedCms().ContentInfo.Content;
        if (!Rfc3161TimestampTokenInfo.TryDecode(content, out _, out var bytesConsumed) || bytesConsumed != content.Length)
        {
            throw Fail("the token's signed content is not exactly one TSTInfo.");
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

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_options.AttemptTimeout);
        try
        {
            return await TimestampCoreAsync(digest, algorithm, attempt.Token);
        }
        catch (OperationCanceledException ex) when (attempt.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw Fail($"the attempt exceeded the attempt timeout of {_options.AttemptTimeout}.", ex);
        }
    }

    private sealed record ValidatedChain(IReadOnlyList<Byte[]> Certificates, IReadOnlyList<IReadOnlyList<Uri>> DistributionPoints);
}
