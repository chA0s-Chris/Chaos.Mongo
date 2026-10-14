// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The outcome of verifying one stored personal data value against its commitment.
/// </summary>
internal enum PersonalDataVerification
{
    /// <summary>
    /// The salt is present and the value matches its commitment.
    /// </summary>
    Committed,

    /// <summary>
    /// The salt is absent and the value is <c>null</c>.
    /// </summary>
    Erased,

    /// <summary>
    /// The recorded path does not resolve to an element of the stored event.
    /// </summary>
    Unresolved,

    /// <summary>
    /// The salt is present, but the value does not match its commitment.
    /// </summary>
    Mismatch,

    /// <summary>
    /// The salt is absent, but the value is not <c>null</c>.
    /// </summary>
    NotErased
}
