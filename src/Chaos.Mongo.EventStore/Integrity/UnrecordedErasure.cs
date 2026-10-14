// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// A personal data value that is <c>null</c> without its salt, but that no later erasure event of the stream records.
/// </summary>
/// <param name="Version">The version of the event holding the value.</param>
/// <param name="Path">The dotted element path of the value.</param>
internal sealed record UnrecordedErasure(Int64 Version, String Path);
