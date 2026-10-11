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
    /// <exception cref="InvalidOperationException">
    /// Thrown when a protected aggregate type with the same name is already registered.
    /// </exception>
    public void Add(ProtectedStreamType type)
    {
        ArgumentNullException.ThrowIfNull(type);

        // The name identifies a stream together with its aggregate ID in the genesis value and in anchor candidates.
        if (_types.FirstOrDefault(registered => registered.AggregateTypeName == type.AggregateTypeName) is { } existing)
        {
            throw new InvalidOperationException(
                $"The protected aggregate types stored in '{existing.EventsCollectionName}' and '{type.EventsCollectionName}' " +
                $"share the name '{type.AggregateTypeName}'. Protected aggregate types must have unique names, because the " +
                "name identifies their streams in the hash chain and in anchors.");
        }

        _types.Add(type);
    }
}
