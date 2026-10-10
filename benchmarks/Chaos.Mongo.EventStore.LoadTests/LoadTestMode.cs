// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.LoadTests;

/// <summary>
/// The anchor candidate source a load test runs with; every mode seals appends into the hash chain.
/// </summary>
internal enum LoadTestMode
{
    /// <summary>
    /// The hash chain without an anchor candidate source, as a reference.
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
