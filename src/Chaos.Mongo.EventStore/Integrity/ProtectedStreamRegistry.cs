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
    /// Thrown when a protected aggregate type with the same name or the same events collection is already registered.
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

        // Candidates and recovery scans attribute every document of an events collection to a single type.
        if (_types.FirstOrDefault(registered => registered.EventsCollectionName == type.EventsCollectionName) is { } shared)
        {
            throw new InvalidOperationException(
                $"The protected aggregate types '{shared.AggregateTypeName}' and '{type.AggregateTypeName}' share the events " +
                $"collection '{type.EventsCollectionName}'. Every protected aggregate type needs its own events collection, " +
                "because anchor candidates identify the type of a stream by its collection.");
        }

        _types.Add(type);
    }
}
