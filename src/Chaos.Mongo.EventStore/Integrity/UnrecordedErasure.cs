// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// A personal data value that is <c>null</c> without its salt, but that no later erasure event of the stream records.
/// </summary>
public sealed record UnrecordedErasure
{
    /// <summary>
    /// Gets the dotted element path of the value.
    /// </summary>
    public required String Path { get; init; }

    /// <summary>
    /// Gets the version of the event holding the value.
    /// </summary>
    public required Int64 Version { get; init; }
}
