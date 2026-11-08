// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The revocation status of a timestamp token's certificate chain at the token's time, as shown by CRLs issued at
/// least a grace period after it.
/// </summary>
public enum TimestampRevocationStatus
{
    /// <summary>
    /// No CRLs issued late enough have been obtained yet, or at least one non-root certificate of the chain names a
    /// CRL distribution point while at least one other does not, which can never reach <see cref="Confirmed"/> because
    /// the silent certificate's revocation is never checked.
    /// </summary>
    Unconfirmed,

    /// <summary>
    /// CRLs issued late enough list no certificate of the chain as revoked at or before the token's time, and every
    /// non-root certificate of the chain names at least one CRL distribution point, so every one of them was checked.
    /// </summary>
    Confirmed,

    /// <summary>
    /// A CRL issued late enough lists a certificate of the chain as revoked at or before the token's time, so the token
    /// is no evidence.
    /// </summary>
    /// <remarks>
    /// The status recorded during anchoring rests on CRLs whose signatures are not verified, so a recorded
    /// <see cref="Revoked"/> is an alert, not a final verdict. Verification recomputes the status from the stored CRLs
    /// and counts only CRLs whose signatures verify.
    /// </remarks>
    Revoked,

    /// <summary>
    /// No non-root certificate of the chain names a CRL distribution point, so there is nothing to confirm. Unlike
    /// <see cref="Confirmed"/>, this does not mean every certificate's revocation was checked and found clear; it means
    /// there was no revocation source to check in the first place. A chain where some certificates name a source and
    /// others do not is <see cref="Unconfirmed"/>, never this value or <see cref="Confirmed"/>, because the silent
    /// ones are never checked.
    /// </summary>
    NoRevocationSource
}
