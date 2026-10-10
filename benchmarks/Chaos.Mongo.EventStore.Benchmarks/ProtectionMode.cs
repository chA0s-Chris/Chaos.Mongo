// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Benchmarks;

/// <summary>
/// The integrity protection and anchor candidate source a benchmark runs with.
/// </summary>
public enum ProtectionMode
{
    /// <summary>
    /// No integrity protection.
    /// </summary>
    Off,

    /// <summary>
    /// The hash chain without an anchor candidate source.
    /// </summary>
    Chain,

    /// <summary>
    /// The hash chain with pending-anchor entries (option E) and their consumer running.
    /// </summary>
    ChainWithPendingCollection,

    /// <summary>
    /// The hash chain with an active change-stream consumer (option D).
    /// </summary>
    ChainWithChangeStream
}
