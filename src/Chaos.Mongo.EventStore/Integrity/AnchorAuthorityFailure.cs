// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The failed attempts of one authority to stamp an anchor, kept on the anchor so gaps stay visible.
/// </summary>
/// <param name="Authority">The name of the authority.</param>
/// <param name="FailureCount">The number of failed attempts.</param>
/// <param name="LastFailure">The message of the last failure.</param>
/// <param name="LastFailureUtc">The time of the last failure.</param>
internal sealed record AnchorAuthorityFailure(String Authority, Int32 FailureCount, String LastFailure, DateTime LastFailureUtc);
