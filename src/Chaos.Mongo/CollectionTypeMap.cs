// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo;

using Microsoft.Extensions.Options;
using System.Collections.Frozen;

/// <summary>
/// Default implementation of <see cref="ICollectionTypeMap"/> that maps CLR types to MongoDB collection names.
/// </summary>
internal class CollectionTypeMap : ICollectionTypeMap
{
    private readonly FrozenDictionary<Type, String> _typeMap;
    private readonly Boolean _useDefaultCollectionNames;

    /// <summary>
    /// Initializes a new instance of the <see cref="CollectionTypeMap"/> class.
    /// </summary>
    /// <param name="options">MongoDB configuration options containing the type-to-collection mappings.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
    public CollectionTypeMap(IOptions<MongoOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _useDefaultCollectionNames = options.Value.UseDefaultCollectionNames;
        _typeMap = options.Value.CollectionTypeMap.ToFrozenDictionary();
    }

    /// <inheritdoc/>
    public String GetCollectionName(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (_typeMap.TryGetValue(type, out var name))
        {
            return name;
        }

        if (_useDefaultCollectionNames)
        {
            return type.Name;
        }

        throw new KeyNotFoundException($"Type {type} is not mapped and default collection names are not used");
    }
}
