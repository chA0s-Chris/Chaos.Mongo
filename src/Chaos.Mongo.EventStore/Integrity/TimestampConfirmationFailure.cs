// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The failed attempts to confirm the revocation status of a timestamp token, kept on its entry so gaps stay visible.
/// CRLs that were only issued too early do not count as failures.
/// </summary>
/// <param name="FailureCount">The number of failed attempts.</param>
/// <param name="LastFailure">The message of the last failure.</param>
/// <param name="LastFailureUtc">The time of the last failure.</param>
public sealed record TimestampConfirmationFailure(Int32 FailureCount, String LastFailure, DateTime LastFailureUtc);
