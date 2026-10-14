// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Describes why the verification of an event in a stream failed.
/// </summary>
internal enum StreamVerificationFailure
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
    ErasedPersonalDataPresent
}
