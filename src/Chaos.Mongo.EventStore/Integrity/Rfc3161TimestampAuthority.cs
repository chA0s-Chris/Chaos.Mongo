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
/// certificate chain by <see cref="X509Chain"/> against the configured trust. A token is returned only together with
/// the CRLs of every distribution point its chain names; a CRL that cannot be downloaded fails the attempt.
/// </remarks>
internal sealed class Rfc3161TimestampAuthority : ITimestampAuthority
{
    private const Int32 DigestLength = 32;
    private const Int32 NonceLength = 16;
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
        if (!options.Uri.IsAbsoluteUri)
        {
            throw new ArgumentException("The timestamp authority URI must be absolute.", nameof(options));
        }

        _options = options;
        _timeProvider = serviceProvider.GetService(typeof(TimeProvider)) as TimeProvider ?? TimeProvider.System;
        _httpClient = options.HttpClientFactory(serviceProvider) ??
                      throw new InvalidOperationException($"The HTTP client factory of timestamp authority '{options.Name}' returned null.");
    }

    /// <inheritdoc/>
    public String Name => _options.Name;

    private static Boolean IsTransportFailure(Exception exception, CancellationToken cancellationToken)
        => exception is HttpRequestException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested);

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
                                             .DistinctBy(locations => String.Join(" ", locations))
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
                using var response = await _httpClient.GetAsync(location, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    lastError = new HttpRequestException($"HTTP status {(Int32)response.StatusCode}.", null, response.StatusCode);
                    continue;
                }

                var crl = await response.Content.ReadAsByteArrayAsync(cancellationToken);
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
    /// certificate that names distribution points must yield at least one CRL.
    /// </summary>
    private IReadOnlyList<IReadOnlyList<Uri>> GetDistributionPoints(X509Certificate2 certificate)
    {
        var points = CrlDistributionPoints.GetHttpUris(certificate);
        if (points.Count == 0 && CrlDistributionPoints.NamesDistributionPoints(certificate))
        {
            throw Fail($"the certificate '{certificate.Subject}' names CRL distribution points, but none has an HTTP or HTTPS location.");
        }

        return points;
    }

    private async Task<Byte[]> RequestAsync(Byte[] query, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(query);
        content.Headers.ContentType = new MediaTypeHeaderValue(TimestampQueryMediaType);
        try
        {
            using var response = await _httpClient.PostAsync(_options.Uri, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw Fail($"the timestamp request failed with HTTP status {(Int32)response.StatusCode}.");
            }

            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
        {
            throw Fail("the timestamp request failed.", ex);
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

        var request = Rfc3161TimestampRequest.CreateFromHash(digest,
                                                             algorithm,
                                                             nonce: RandomNumberGenerator.GetBytes(NonceLength),
                                                             requestSignerCertificates: true);
        var (token, rawToken) = ValidateResponse(request, await RequestAsync(request.Encode(), cancellationToken));
        if (!token.VerifySignatureForHash(digest.Span, algorithm, out var signer))
        {
            throw Fail("the token's signature or signing-certificate attribute is invalid.");
        }

        // The chain is validated at the token's time; bounding that time keeps a backdated or future-dated token
        // from a key whose certificate expired from passing.
        var skew = (token.TokenInfo.Timestamp - _timeProvider.GetUtcNow()).Duration();
        if (skew > _options.MaximumClockSkew)
        {
            signer.Dispose();
            throw Fail($"the token's time {token.TokenInfo.Timestamp:O} differs from the local time by {skew}, " +
                       $"more than the allowed {_options.MaximumClockSkew}.");
        }

        ValidatedChain chain;
        using (signer)
        {
            chain = BuildChain(token, signer);
        }

        var crls = new List<Byte[]>();
        foreach (var locations in chain.DistributionPoints)
        {
            crls.Add(await DownloadCrlAsync(locations, cancellationToken));
        }

        return new TimestampToken(rawToken, chain.Certificates, crls, token.TokenInfo.Timestamp);
    }

    private sealed record ValidatedChain(IReadOnlyList<Byte[]> Certificates, IReadOnlyList<IReadOnlyList<Uri>> DistributionPoints);
}
