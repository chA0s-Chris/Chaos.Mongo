// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using MongoDB.Bson;

/// <summary>
/// Checks documents of the anchor and evidence collections. They are read as raw documents, which keep a repeated
/// element name instead of failing the driver's deserialization, so the strict readers can reject such a document as
/// malformed.
/// </summary>
internal static class StoredDocuments
{
    /// <summary>
    /// Reads a 32-bit integer stored as an int32, or as an int64 or an integral double within the int32 range.
    /// </summary>
    /// <param name="value">The stored value.</param>
    /// <returns>The integer.</returns>
    /// <exception cref="FormatException">Thrown when the value is of another type or out of range.</exception>
    public static Int32 ReadInt32(BsonValue value)
        => value switch
        {
            BsonInt32 number => number.Value,
            BsonInt64 { Value: >= Int32.MinValue and <= Int32.MaxValue } number => (Int32)number.Value,
            BsonDouble { Value: var number } when Double.IsInteger(number) && number is >= Int32.MinValue and <= Int32.MaxValue => (Int32)number,
            _ => throw new FormatException($"A stored element is a {value.BsonType}, not a 32-bit integer.")
        };

    /// <summary>
    /// Returns the document when no element name repeats within it or within any embedded document or array.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <typeparam name="TDocument">The document type.</typeparam>
    /// <returns>The document.</returns>
    /// <exception cref="FormatException">Thrown when an element name repeats.</exception>
    public static TDocument WithDistinctNames<TDocument>(TDocument document) where TDocument : BsonDocument
    {
        ArgumentNullException.ThrowIfNull(document);
        RequireDistinctNames(document);
        return document;
    }

    private static void RequireDistinctNames(BsonValue value)
    {
        switch (value)
        {
            case BsonDocument document:
                var names = new HashSet<String>(StringComparer.Ordinal);
                foreach (var element in document)
                {
                    if (!names.Add(element.Name))
                    {
                        throw new FormatException($"A stored document repeats the element name '{element.Name}'.");
                    }

                    RequireDistinctNames(element.Value);
                }

                break;
            case BsonArray array:
                foreach (var item in array)
                {
                    RequireDistinctNames(item);
                }

                break;
        }
    }
}
