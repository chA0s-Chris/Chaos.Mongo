// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// A timestamp entry as stored on an anchor: the token and the evidence captured when it was obtained are referenced
/// by their SHA-256 in the <see cref="AnchorEvidenceStore"/>, so the entry's size does not depend on theirs. RFC 4998
/// renewal can later append re-timestamping entries to an anchor's list.
/// </summary>
/// <param name="Authority">The name of the authority that issued the token.</param>
/// <param name="TokenHash">The SHA-256 of the raw RFC 3161 token.</param>
/// <param name="CertificateChainHashes">The SHA-256 of each certificate, from the signing certificate to the root.</param>
/// <param name="CertificateRevocationListHashes">The SHA-256 of each CRL captured for the chain, in capture order.</param>
/// <param name="TimestampUtc">The time the authority asserts in the token.</param>
/// <param name="ObtainedUtc">The time the token was obtained.</param>
internal sealed record AnchorTimestampReference(
    String Authority,
    Byte[] TokenHash,
    IReadOnlyList<Byte[]> CertificateChainHashes,
    IReadOnlyList<Byte[]> CertificateRevocationListHashes,
    DateTime TimestampUtc,
    DateTime ObtainedUtc);
