// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox;

/// <summary>
/// Represents the processing state of an outbox message.
/// </summary>
public enum OutboxMessageState
{
    /// <summary>
    /// The message is pending and has not yet been processed.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// The message has been successfully published.
    /// </summary>
    Processed = 1,

    /// <summary>
    /// The message has permanently failed after exhausting all retry attempts.
    /// </summary>
    Failed = 2
}
