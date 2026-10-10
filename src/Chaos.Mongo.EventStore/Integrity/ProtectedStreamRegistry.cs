// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// All aggregate types registered with integrity protection, in registration order.
/// </summary>
internal sealed class ProtectedStreamRegistry
{
    private readonly List<ProtectedStreamType> _types = [];

    /// <summary>
    /// Gets the protected aggregate types.
    /// </summary>
    public IReadOnlyList<ProtectedStreamType> Types => _types;

    /// <summary>
    /// Adds a protected aggregate type.
    /// </summary>
    /// <param name="type">The protected aggregate type.</param>
    public void Add(ProtectedStreamType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        _types.Add(type);
    }
}
