// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Why a candidate document was skipped during anchoring or sealing instead of being read.
/// </summary>
internal enum MalformedCandidateReason
{
    /// <summary>
    /// The aggregate identifier could not be read as a standard-representation UUID.
    /// </summary>
    AggregateId,

    /// <summary>
    /// The version could not be read as an integer.
    /// </summary>
    Version,

    /// <summary>
    /// The integrity hash was not exactly <see cref="AnchorEncoding.HashLength"/> bytes.
    /// </summary>
    Hash
}
