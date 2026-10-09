// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Integrity data stored in the reserved <c>_integrity</c> element of a sealed event.
/// The format is a draft until the tamper-evidence feature is released.
/// </summary>
internal sealed class EventIntegrity
{
    /// <summary>
    /// Gets or sets the name of the hash algorithm used for <see cref="Hash"/> and <see cref="PreviousHash"/>.
    /// </summary>
    public String Algorithm { get; set; } = String.Empty;

    /// <summary>
    /// Gets or sets the version of the integrity format.
    /// </summary>
    public Int32 FormatVersion { get; set; }

    /// <summary>
    /// Gets or sets the chain hash of this event.
    /// </summary>
    public Byte[] Hash { get; set; } = [];

    /// <summary>
    /// Gets or sets the chain hash of the predecessor event, or the stream's genesis value for version 1.
    /// </summary>
    public Byte[] PreviousHash { get; set; } = [];

    /// <summary>
    /// Gets or sets how the event was sealed.
    /// </summary>
    public IntegritySealMode SealMode { get; set; }
}
