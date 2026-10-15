// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The revocation status of a timestamp token's certificate chain at the token's time, as shown by CRLs issued at
/// least a grace period after it.
/// </summary>
internal enum TimestampRevocationStatus
{
    /// <summary>
    /// No CRLs issued late enough have been obtained yet.
    /// </summary>
    Unconfirmed,

    /// <summary>
    /// CRLs issued late enough list no certificate of the chain as revoked at or before the token's time.
    /// </summary>
    Confirmed,

    /// <summary>
    /// A CRL issued late enough lists a certificate of the chain as revoked at or before the token's time, so the token
    /// is no evidence. CRL signatures are not verified, so this is an alert, not a final verdict.
    /// </summary>
    Revoked
}
