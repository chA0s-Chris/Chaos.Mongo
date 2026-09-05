// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo;

using Microsoft.Extensions.Options;

/// <summary>
/// Validates <see cref="MongoOptions"/> configuration at application startup.
/// </summary>
public sealed class MongoOptionsValidation : IValidateOptions<MongoOptions>
{
    private static ValidateOptionsResult ValidateCollectionTypeMap(IReadOnlyDictionary<Type, String>? collectionTypeMap)
    {
        if (collectionTypeMap is null)
        {
            return ValidateOptionsResult.Fail("MongoOptions.CollectionTypeMap must not be null");
        }

        foreach (var kvp in collectionTypeMap)
        {
            if (kvp.Key is null)
            {
                return ValidateOptionsResult.Fail("CollectionTypeMap contains a null Type key");
            }

            if (String.IsNullOrWhiteSpace(kvp.Value))
            {
                return ValidateOptionsResult.Fail($"CollectionTypeMap for type '{kvp.Key}' has an invalid (null/empty) collection name");
            }
        }

        return ValidateOptionsResult.Success;
    }

    /// <inheritdoc/>
    public ValidateOptionsResult Validate(String? name, MongoOptions? options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail("Options instance is null");
        }

        if (options.Url is null)
        {
            return ValidateOptionsResult.Fail("MongoOptions.Url must be configured");
        }

        return ValidateCollectionTypeMap(options.CollectionTypeMap);
    }
}
