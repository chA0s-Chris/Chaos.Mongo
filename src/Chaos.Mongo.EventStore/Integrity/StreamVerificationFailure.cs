// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Describes why the verification of an event in a stream failed.
/// </summary>
public enum StreamVerificationFailure
{
    /// <summary>
    /// The expected version is missing, so an event was deleted or versions were changed.
    /// </summary>
    VersionGap,

    /// <summary>
    /// The event carries no integrity data.
    /// </summary>
    NotSealed,

    /// <summary>
    /// The version-1 event stores no aggregate type as a string, so the stream's genesis value cannot be derived.
    /// </summary>
    MissingAggregateType,

    /// <summary>
    /// The event's integrity data cannot be read, for example because elements have unexpected types.
    /// </summary>
    MalformedIntegrity,

    /// <summary>
    /// The event's integrity data uses an unsupported format version or algorithm.
    /// </summary>
    UnsupportedFormat,

    /// <summary>
    /// The event's stored previous hash does not match its predecessor's hash.
    /// </summary>
    PreviousHashMismatch,

    /// <summary>
    /// The hash recomputed from the stored event does not match its stored hash.
    /// </summary>
    HashMismatch,

    /// <summary>
    /// A personal data value whose salt is present does not match its commitment.
    /// </summary>
    PersonalDataMismatch,

    /// <summary>
    /// A personal data value whose salt is absent is not <c>null</c>, so the value was kept while its salt was removed.
    /// </summary>
    ErasedPersonalDataPresent,

    /// <summary>
    /// A version that a completed anchor covers no longer exists, so the stream was truncated or deleted.
    /// </summary>
    AnchoredVersionMissing,

    /// <summary>
    /// The stored hash of a version that a completed anchor covers differs from the anchor's leaf, so the stream was
    /// rewritten or rolled back.
    /// </summary>
    AnchoredHashMismatch,

    /// <summary>
    /// A leaf of the stream in a completed anchor does not prove its inclusion in the anchor's Merkle root, so the
    /// leaf was changed, or a leaf of the stream, or the committed anchor it belongs to, cannot be read.
    /// </summary>
    AnchoredLeafInvalid,

    /// <summary>
    /// The stream has no events, so the library cannot tell "this aggregate never existed" apart from "every event of
    /// this stream, and its anchored leaves, were deleted."
    /// </summary>
    EmptyStream,

    /// <summary>
    /// The stream has events, but no completed anchor's leaf was found for the configured aggregate type name, while
    /// a leaf exists for the same aggregate identifier under a different aggregate type name, so the stream was
    /// anchored under a name that no longer matches the verifier's configured type and coverage could not be
    /// established.
    /// </summary>
    AggregateTypeMismatch
}
