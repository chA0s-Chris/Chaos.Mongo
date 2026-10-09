// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using MongoDB.Bson;
using MongoDB.Bson.IO;
using System.Buffers.Binary;

/// <summary>
/// Byte-level operations on serialized BSON documents.
/// </summary>
internal static class RawBsonElements
{
    /// <summary>
    /// Removes the first top-level element with the given name by cutting its bytes and correcting
    /// the document's length prefix. The remaining bytes are never decoded and re-encoded, so their
    /// representation is preserved exactly.
    /// </summary>
    /// <param name="document">The serialized BSON document.</param>
    /// <param name="elementName">The name of the top-level element to remove.</param>
    /// <returns>
    /// The document without the element, or <paramref name="document"/> itself when no such element exists.
    /// </returns>
    public static Byte[] RemoveTopLevelElement(Byte[] document, String elementName)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(elementName);

        using var stream = new MemoryStream(document, false);
        using var reader = new BsonBinaryReader(stream);
        reader.ReadStartDocument();

        while (true)
        {
            var elementStart = (Int32)reader.BsonStream.Position;
            if (reader.ReadBsonType() == BsonType.EndOfDocument)
            {
                return document;
            }

            var name = reader.ReadName();
            reader.SkipValue();

            if (String.Equals(name, elementName, StringComparison.Ordinal))
            {
                var elementEnd = (Int32)reader.BsonStream.Position;
                return Cut(document, elementStart, elementEnd);
            }
        }
    }

    private static Byte[] Cut(Byte[] document, Int32 start, Int32 end)
    {
        var result = new Byte[document.Length - (end - start)];
        document.AsSpan(0, start).CopyTo(result);
        document.AsSpan(end).CopyTo(result.AsSpan(start));
        BinaryPrimitives.WriteInt32LittleEndian(result, result.Length);
        return result;
    }
}
