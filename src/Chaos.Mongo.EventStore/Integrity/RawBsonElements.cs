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
    private const Char PathSeparator = '.';

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

    /// <summary>
    /// Replaces the value of every element addressed by a path with a binary value of subtype <c>0x00</c>
    /// holding the given bytes. The element keeps its name and position; the length prefixes of all enclosing
    /// documents are corrected. The other bytes are copied verbatim, so their representation is preserved exactly.
    /// </summary>
    /// <param name="document">The serialized BSON document.</param>
    /// <param name="values">The binary payload for each dotted element path.</param>
    /// <returns>The document with the replaced values.</returns>
    /// <exception cref="ArgumentException">Thrown when a path does not resolve to an element.</exception>
    public static Byte[] ReplaceValuesWithBinary(Byte[] document, IReadOnlyDictionary<String, Byte[]> values)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(values);

        using var stream = new MemoryStream(document, false);
        using var reader = new BsonBinaryReader(stream);
        using var output = new MemoryStream(document.Length);
        var remaining = new HashSet<String>(values.Keys, StringComparer.Ordinal);

        CopyDocument(reader, document, String.Empty, values, remaining, output);

        if (remaining.Count > 0)
        {
            throw new ArgumentException(
                $"The path '{remaining.First()}' does not resolve to an element of the document.",
                nameof(values));
        }

        return output.ToArray();
    }

    /// <summary>
    /// Locates the element addressed by a dotted path. Every segment but the last must resolve to an
    /// embedded document; arrays are not traversed.
    /// </summary>
    /// <param name="document">The serialized BSON document.</param>
    /// <param name="path">The dotted element path.</param>
    /// <param name="type">The type of the located element.</param>
    /// <param name="value">The range of the located element's value bytes within <paramref name="document"/>.</param>
    /// <returns><c>true</c> when the path resolves to an element; otherwise <c>false</c>.</returns>
    public static Boolean TryLocate(Byte[] document, String path, out BsonType type, out Range value)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(path);

        type = BsonType.EndOfDocument;
        value = default;

        var segments = path.Split(PathSeparator);
        using var stream = new MemoryStream(document, false);
        using var reader = new BsonBinaryReader(stream);
        reader.ReadStartDocument();

        for (var depth = 0; depth < segments.Length; depth++)
        {
            if (!TryFindElement(reader, segments[depth], out var elementType))
            {
                return false;
            }

            if (depth == segments.Length - 1)
            {
                var valueStart = (Int32)reader.BsonStream.Position;
                reader.SkipValue();
                type = elementType;
                value = valueStart..(Int32)reader.BsonStream.Position;
                return true;
            }

            if (elementType != BsonType.Document)
            {
                return false;
            }

            reader.ReadStartDocument();
        }

        return false;
    }

    /// <summary>
    /// Copies the document at the reader's position to the output, replacing the addressed values.
    /// </summary>
    private static void CopyDocument(BsonBinaryReader reader,
                                     Byte[] source,
                                     String pathPrefix,
                                     IReadOnlyDictionary<String, Byte[]> values,
                                     HashSet<String> remaining,
                                     MemoryStream output)
    {
        var lengthPosition = output.Position;
        output.Write(stackalloc Byte[4]);
        reader.ReadStartDocument();

        while (true)
        {
            var elementStart = (Int32)reader.BsonStream.Position;
            var type = reader.ReadBsonType();
            if (type == BsonType.EndOfDocument)
            {
                break;
            }

            var path = pathPrefix + reader.ReadName();
            var valueStart = (Int32)reader.BsonStream.Position;

            if (values.TryGetValue(path, out var replacement))
            {
                reader.SkipValue();
                remaining.Remove(path);
                output.WriteByte((Byte)BsonType.Binary);
                output.Write(source, elementStart + 1, valueStart - elementStart - 1);
                WriteBinary(output, replacement);
            }
            else if (type == BsonType.Document && HasPathBelow(remaining, path))
            {
                output.Write(source, elementStart, valueStart - elementStart);
                CopyDocument(reader, source, path + PathSeparator, values, remaining, output);
            }
            else
            {
                reader.SkipValue();
                output.Write(source, elementStart, (Int32)reader.BsonStream.Position - elementStart);
            }
        }

        reader.ReadEndDocument();
        output.WriteByte(0);

        var length = (Int32)(output.Position - lengthPosition);
        Span<Byte> lengthBytes = stackalloc Byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, length);
        var end = output.Position;
        output.Position = lengthPosition;
        output.Write(lengthBytes);
        output.Position = end;
    }

    private static Byte[] Cut(Byte[] document, Int32 start, Int32 end)
    {
        var result = new Byte[document.Length - (end - start)];
        document.AsSpan(0, start).CopyTo(result);
        document.AsSpan(end).CopyTo(result.AsSpan(start));
        BinaryPrimitives.WriteInt32LittleEndian(result, result.Length);
        return result;
    }

    private static Boolean HasPathBelow(HashSet<String> paths, String parentPath)
    {
        foreach (var path in paths)
        {
            if (path.Length > parentPath.Length &&
                path[parentPath.Length] == PathSeparator &&
                path.StartsWith(parentPath, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads the elements of the current document until one with the given name is found, leaving the reader
    /// positioned at that element's value.
    /// </summary>
    private static Boolean TryFindElement(BsonBinaryReader reader, String name, out BsonType type)
    {
        while (true)
        {
            type = reader.ReadBsonType();
            if (type == BsonType.EndOfDocument)
            {
                return false;
            }

            if (String.Equals(reader.ReadName(), name, StringComparison.Ordinal))
            {
                return true;
            }

            reader.SkipValue();
        }
    }

    private static void WriteBinary(MemoryStream output, Byte[] payload)
    {
        Span<Byte> lengthBytes = stackalloc Byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, payload.Length);
        output.Write(lengthBytes);
        output.WriteByte((Byte)BsonBinarySubType.Binary);
        output.Write(payload);
    }
}
