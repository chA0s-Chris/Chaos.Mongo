// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The commitment to one personal data value of a sealed event, stored in the <c>PersonalData</c> array
/// of the event's <c>_integrity</c> element, in the stable format version 1 that docs/tamper-evidence-specification.md
/// defines.
/// </summary>
internal sealed class PersonalDataCommitment
{
    /// <summary>
    /// Gets or sets the salted hash the chain hash covers in place of the value.
    /// </summary>
    public Byte[] Commitment { get; set; } = [];

    /// <summary>
    /// Gets or sets the dotted element path of the value within the stored event.
    /// </summary>
    public String Path { get; set; } = String.Empty;

    /// <summary>
    /// Gets or sets the salt of the commitment, or <c>null</c> once the value was erased.
    /// </summary>
    public Byte[]? Salt { get; set; }
}
