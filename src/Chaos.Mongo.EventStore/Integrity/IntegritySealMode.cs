// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Describes how an event was sealed into its stream's hash chain.
/// </summary>
internal enum IntegritySealMode
{
    /// <summary>
    /// The event was sealed while it was appended.
    /// </summary>
    Append,

    /// <summary>
    /// The event was sealed after it had been stored without integrity data.
    /// </summary>
    Retroactive
}
