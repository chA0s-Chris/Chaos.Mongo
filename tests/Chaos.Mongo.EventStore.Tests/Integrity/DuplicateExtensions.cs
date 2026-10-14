// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

/// <summary>
/// Encodes certificates that carry an extension twice, which <c>CertificateRequest</c> refuses to create: the second
/// instance is added under <see cref="PlaceholderOid"/> and renamed in the encoded certificate afterwards.
/// </summary>
internal static class DuplicateExtensions
{
    /// <summary>
    /// A certificate extension OID below <c>2.5.29</c> that encodes with the same length as the standard extensions.
    /// </summary>
    public const String PlaceholderOid = "2.5.29.99";

    private const String ExtensionArc = "2.5.29.";
    private static readonly Byte[] PlaceholderEncoding = [0x06, 0x03, 0x55, 0x1D, 0x63];

    /// <summary>
    /// Renames the extension added under <see cref="PlaceholderOid"/> to <paramref name="oid"/>. The certificate's
    /// signature no longer verifies afterwards, but loading it does not check the signature.
    /// </summary>
    /// <param name="certificate">The encoded certificate.</param>
    /// <param name="oid">A standard certificate extension OID below <c>2.5.29</c>.</param>
    /// <returns>The encoded certificate with the renamed extension.</returns>
    public static Byte[] RenamePlaceholder(Byte[] certificate, String oid)
    {
        if (!oid.StartsWith(ExtensionArc, StringComparison.Ordinal) || !Byte.TryParse(oid[ExtensionArc.Length..], out var arc) || arc >= 0x80)
        {
            throw new ArgumentException($"'{oid}' is not a single-byte extension OID below {ExtensionArc}.", nameof(oid));
        }

        var renamed = certificate.ToArray();
        var index = renamed.AsSpan().IndexOf(PlaceholderEncoding);
        if (index < 0)
        {
            throw new ArgumentException("The certificate carries no placeholder extension.", nameof(certificate));
        }

        renamed[index + PlaceholderEncoding.Length - 1] = arc;
        return renamed;
    }
}
