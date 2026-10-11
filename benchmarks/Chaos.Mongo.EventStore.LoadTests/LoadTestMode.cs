// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.LoadTests;

/// <summary>
/// Whether a load test collects anchor candidates; every mode seals appends into the hash chain.
/// </summary>
internal enum LoadTestMode
{
    /// <summary>
    /// The hash chain without an anchor candidate source, as a reference.
    /// </summary>
    Chain,

    /// <summary>
    /// The hash chain with an active change-stream consumer collecting anchor candidates.
    /// </summary>
    ChainWithChangeStream
}
