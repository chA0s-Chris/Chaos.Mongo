// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The state of one personal data value recorded in an event's integrity data.
/// </summary>
/// <param name="Path">The dotted element path of the value.</param>
/// <param name="Status">The value's state.</param>
internal sealed record PersonalDataPathStatus(String Path, PersonalDataStatus Status);
