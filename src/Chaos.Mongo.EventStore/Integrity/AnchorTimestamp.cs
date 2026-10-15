// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// A timestamp token of an anchor with the evidence captured when it was obtained, resolved from its
/// <see cref="AnchorTimestampReference"/> so a handed-out anchor is self-contained.
/// </summary>
/// <param name="Authority">The name of the authority that issued the token.</param>
/// <param name="Token">The raw RFC 3161 token.</param>
/// <param name="CertificateChain">The DER-encoded certificates from the signing certificate to the root.</param>
/// <param name="CertificateRevocationLists">The CRLs captured for the chain.</param>
/// <param name="TimestampUtc">The time the authority asserts in the token.</param>
/// <param name="ObtainedUtc">The time the token was obtained.</param>
internal sealed record AnchorTimestamp(
    String Authority,
    Byte[] Token,
    IReadOnlyList<Byte[]> CertificateChain,
    IReadOnlyList<Byte[]> CertificateRevocationLists,
    DateTime TimestampUtc,
    DateTime ObtainedUtc);
