// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using System.Buffers.Binary;
using System.Text;

/// <summary>
/// Checks documents of the anchor and evidence collections. They are read as raw documents, which keep a repeated
/// element name instead of failing the driver's deserialization, so the strict readers can reject such a document as
/// malformed.
/// </summary>
internal static class StoredDocuments
{
    /// <summary>
    /// The deprecated DBPointer type, which the driver cannot read but MongoDB still stores: a string and an ObjectId.
    /// </summary>
    private const BsonType DbPointer = (BsonType)0x0C;

    private static readonly BsonBinaryReaderSettings StoredSubtypeSettings = new()
    {
        FixOldBinarySubTypeOnInput = false
    };

    /// <summary>
    /// Finds a top-level element by comparing names as bytes, so names elsewhere in the document that are not valid
    /// UTF-8 do not matter.
    /// </summary>
    /// <param name="document">The document's bytes.</param>
    /// <param name="name">The element name.</param>
    /// <returns>The element's type and value bytes, or <c>null</c> when it is absent.</returns>
    /// <exception cref="FormatException">Thrown when the bytes are not a well-formed document.</exception>
    public static (BsonType Type, Byte[] Value)? FindTopLevelElement(Byte[] document, String name)
    {
        ArgumentNullException.ThrowIfNull(document);
        var target = Encoding.UTF8.GetBytes(name);
        if (document.Length < 5 || BinaryPrimitives.ReadInt32LittleEndian(document) != document.Length || document[^1] != 0)
        {
            throw new FormatException("A stored document's length does not match its bytes.");
        }

        var position = 4;
        while (document[position] != 0)
        {
            var type = (BsonType)document[position];
            var nameEnd = Array.IndexOf(document, (Byte)0, position + 1);
            if (nameEnd < 0)
            {
                throw new FormatException("A stored element name is not terminated.");
            }

            var valueStart = nameEnd + 1;
            var valueEnd = valueStart + ValueSize(document, type, valueStart);
            if (valueEnd < valueStart || valueEnd > document.Length - 1)
            {
                throw new FormatException("A stored element exceeds its document.");
            }

            if (document.AsSpan(position + 1, nameEnd - position - 1).SequenceEqual(target))
            {
                return (type, document[valueStart..valueEnd]);
            }

            position = valueEnd;
        }

        return null;
    }

    /// <summary>
    /// Returns a stored document ready for the strict readers: a raw document is checked for repeated element names
    /// and read again without the driver's conversion of old binary data (subtype 0x02) to generic binary, so the
    /// readers see every subtype as stored; other documents are returned unchanged.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <returns>The document to read.</returns>
    /// <exception cref="FormatException">Thrown when an element name repeats or a value is malformed.</exception>
    public static BsonDocument Prepare(BsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document is not RawBsonDocument raw)
        {
            return document;
        }

        WithDistinctNames(raw);
        using var stream = new ByteBufferStream(raw.Slice);
        using var reader = new BsonBinaryReader(stream, StoredSubtypeSettings);
        return BsonDocumentSerializer.Instance.Deserialize(BsonDeserializationContext.CreateRoot(reader));
    }

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

    private static Int32 RegularExpressionSize(Byte[] document, Int32 start)
    {
        var patternEnd = Array.IndexOf(document, (Byte)0, start);
        var optionsEnd = patternEnd < 0 ? -1 : Array.IndexOf(document, (Byte)0, patternEnd + 1);
        return optionsEnd < 0 ? throw new FormatException("A stored regular expression is not terminated.") : optionsEnd + 1 - start;
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

    private static Int32 ValueSize(Byte[] document, BsonType type, Int32 start)
        => type switch
        {
            BsonType.Double or BsonType.DateTime or BsonType.Int64 or BsonType.Timestamp => 8,
            BsonType.String or BsonType.JavaScript or BsonType.Symbol => 4 + BinaryPrimitives.ReadInt32LittleEndian(document.AsSpan(start)),
            BsonType.Document or BsonType.Array or BsonType.JavaScriptWithScope => BinaryPrimitives.ReadInt32LittleEndian(document.AsSpan(start)),
            BsonType.Binary => 5 + BinaryPrimitives.ReadInt32LittleEndian(document.AsSpan(start)),
            BsonType.ObjectId => 12,
            BsonType.Boolean => 1,
            BsonType.Int32 => 4,
            BsonType.Decimal128 => 16,
            BsonType.Undefined or BsonType.Null or BsonType.MinKey or BsonType.MaxKey => 0,
            BsonType.RegularExpression => RegularExpressionSize(document, start),
            DbPointer => 4 + BinaryPrimitives.ReadInt32LittleEndian(document.AsSpan(start)) + 12,
            _ => throw new FormatException($"A stored element has the unknown type 0x{(Int32)type:x2}.")
        };
}
