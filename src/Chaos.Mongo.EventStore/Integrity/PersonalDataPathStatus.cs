// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The state of one personal data value recorded in an event's integrity data.
/// </summary>
public sealed record PersonalDataPathStatus
{
    /// <summary>
    /// Gets the dotted element path of the value.
    /// </summary>
    public required String Path { get; init; }

    /// <summary>
    /// Gets the value's state.
    /// </summary>
    public required PersonalDataStatus Status { get; init; }
}
