// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The personal data paths erased from one event, as recorded by a <see cref="PersonalDataErased{TAggregate}"/> event.
/// </summary>
/// <remarks>A read-only output of erasure; the constructor and setters exist for deserialization.</remarks>
public sealed class PersonalDataErasureEntry
{
    /// <summary>
    /// The pinned element name of <see cref="Paths"/>.
    /// </summary>
    internal const String PathsElement = "Paths";

    /// <summary>
    /// The pinned element name of <see cref="Version"/>.
    /// </summary>
    internal const String VersionElement = "Version";

    /// <summary>
    /// Gets or sets the dotted element paths whose values were erased.
    /// </summary>
    public List<String> Paths { get; set; } = [];

    /// <summary>
    /// Gets or sets the version of the event the values were erased from.
    /// </summary>
    public Int64 Version { get; set; }
}
