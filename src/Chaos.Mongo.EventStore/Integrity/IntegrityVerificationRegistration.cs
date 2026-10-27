// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// Records the module-level verification configuration in the service collection, so a second configuration and an
/// anchoring configuration with other anchor collections are detected.
/// </summary>
internal sealed class IntegrityVerificationRegistration
{
    public IntegrityVerificationRegistration(IntegrityVerificationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        Builder = builder;
    }

    /// <summary>
    /// Gets the verification builder.
    /// </summary>
    public IntegrityVerificationBuilder Builder { get; }
}
