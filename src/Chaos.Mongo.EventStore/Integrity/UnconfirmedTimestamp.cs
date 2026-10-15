// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// A timestamp entry whose revocation status is unconfirmed, with what confirming it needs; its CRLs are not read.
/// </summary>
/// <param name="AnchorId">The identifier of the anchor that holds the entry.</param>
/// <param name="AnchorSequence">The anchor's sequence number.</param>
/// <param name="Authority">The name of the authority that issued the token; with the token, it identifies the entry.</param>
/// <param name="Token">The raw token.</param>
/// <param name="CertificateChain">The DER-encoded certificates from the signing certificate to the root.</param>
/// <param name="TimestampUtc">The time the authority asserts in the token.</param>
/// <param name="ConfirmationNotBeforeUtc">The time before which confirmation is not attempted again, if any.</param>
internal sealed record UnconfirmedTimestamp(
    Guid AnchorId,
    Int64 AnchorSequence,
    String Authority,
    Byte[] Token,
    IReadOnlyList<Byte[]> CertificateChain,
    DateTime TimestampUtc,
    DateTime? ConfirmationNotBeforeUtc);
