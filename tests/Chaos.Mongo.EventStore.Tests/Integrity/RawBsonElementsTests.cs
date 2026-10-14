// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using NUnit.Framework;

public class RawBsonElementsTests
{
    private static readonly Byte[] Payload = Enumerable.Range(1, 32).Select(value => (Byte)value).ToArray();

    [Test]
    public void RemoveTopLevelElement_ElementAbsent_ReturnsSameBytes()
    {
        var bytes = CreateDocument("first", "second").ToBson();

        var result = RawBsonElements.RemoveTopLevelElement(bytes, "missing");

        result.Should().BeSameAs(bytes);
    }

    [TestCase("first", new[]
    {
        "first",
        "second",
        "third"
    })]
    [TestCase("second", new[]
    {
        "first",
        "second",
        "third"
    })]
    [TestCase("third", new[]
    {
        "first",
        "second",
        "third"
    })]
    [TestCase("only", new[] { "only" })]
    public void RemoveTopLevelElement_ElementPresent_ReturnsBytesWithoutElement(String elementName, String[] elementNames)
    {
        var document = CreateDocument(elementNames);
        var expected = CreateDocument(elementNames.Where(name => name != elementName).ToArray()).ToBson();

        var result = RawBsonElements.RemoveTopLevelElement(document.ToBson(), elementName);

        result.Should().Equal(expected);
    }

    [Test]
    public void RemoveTopLevelElement_NestedElementWithSameName_KeepsNestedElement()
    {
        var document = new BsonDocument
        {
            { "outer", new BsonDocument("_integrity", 1) },
            { "_integrity", 2 }
        };
        var expected = new BsonDocument("outer", new BsonDocument("_integrity", 1)).ToBson();

        var result = RawBsonElements.RemoveTopLevelElement(document.ToBson(), "_integrity");

        result.Should().Equal(expected);
    }

    [Test]
    public void RemoveTopLevelElement_NullDocument_ThrowsArgumentNullException()
    {
        var act = () => RawBsonElements.RemoveTopLevelElement(null!, "name");

        act.Should().Throw<ArgumentNullException>().WithParameterName("document");
    }

    [Test]
    public void RemoveTopLevelElement_NullElementName_ThrowsArgumentNullException()
    {
        var act = () => RawBsonElements.RemoveTopLevelElement(new BsonDocument().ToBson(), null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("elementName");
    }

    [Test]
    public void ReplaceValuesWithBinary_MultiplePaths_ReplacesEveryValue()
    {
        var document = CreateNestedDocument();
        var expected = document.DeepClone().AsBsonDocument;
        SetPath(expected, "first", new BsonBinaryData(Payload));
        SetPath(expected, "middle.innerMiddle", new BsonBinaryData(Enumerable.Reverse(Payload).ToArray()));
        SetPath(expected, "last", new BsonBinaryData([7]));

        var result = RawBsonElements.ReplaceValuesWithBinary(document.ToBson(), new Dictionary<String, Byte[]>
        {
            { "first", Payload },
            { "middle.innerMiddle", Enumerable.Reverse(Payload).ToArray() },
            { "last", [7] }
        });

        result.Should().Equal(expected.ToBson());
    }

    [TestCase("middle.innerFirst")]
    [TestCase("middle.innerMiddle")]
    [TestCase("middle.innerLast")]
    public void ReplaceValuesWithBinary_NestedPath_ReplacesValueAndCorrectsEnclosingLengths(String path)
    {
        var document = CreateNestedDocument();
        var expected = document.DeepClone().AsBsonDocument;
        SetPath(expected, path, new BsonBinaryData(Payload));

        var result = RawBsonElements.ReplaceValuesWithBinary(document.ToBson(), new Dictionary<String, Byte[]>
        {
            { path, Payload }
        });

        result.Should().Equal(expected.ToBson());
        BsonSerializer.Deserialize<BsonDocument>(result).Equals(expected).Should().BeTrue();
    }

    [Test]
    public void ReplaceValuesWithBinary_NoPaths_ReturnsEqualBytes()
    {
        var bytes = CreateNestedDocument().ToBson();

        var result = RawBsonElements.ReplaceValuesWithBinary(bytes, new Dictionary<String, Byte[]>());

        result.Should().Equal(bytes);
    }

    [Test]
    public void ReplaceValuesWithBinary_NullArguments_ThrowArgumentNullException()
    {
        var actDocument = () => RawBsonElements.ReplaceValuesWithBinary(null!, new Dictionary<String, Byte[]>());
        var actValues = () => RawBsonElements.ReplaceValuesWithBinary(new BsonDocument().ToBson(), null!);

        actDocument.Should().Throw<ArgumentNullException>().WithParameterName("document");
        actValues.Should().Throw<ArgumentNullException>().WithParameterName("values");
    }

    [TestCase("first")]
    [TestCase("middle")]
    [TestCase("last")]
    public void ReplaceValuesWithBinary_TopLevelPath_ReplacesValueInPlace(String path)
    {
        var document = CreateNestedDocument();
        var expected = document.DeepClone().AsBsonDocument;
        SetPath(expected, path, new BsonBinaryData(Payload));

        var result = RawBsonElements.ReplaceValuesWithBinary(document.ToBson(), new Dictionary<String, Byte[]>
        {
            { path, Payload }
        });

        result.Should().Equal(expected.ToBson());
    }

    [TestCase("missing")]
    [TestCase("first.inner")]
    [TestCase("middle.missing")]
    [TestCase("middle.innerLast.0")]
    public void ReplaceValuesWithBinary_UnresolvedPath_ThrowsArgumentException(String path)
    {
        var bytes = CreateNestedDocument().ToBson();

        var act = () => RawBsonElements.ReplaceValuesWithBinary(bytes, new Dictionary<String, Byte[]>
        {
            { path, Payload }
        });

        act.Should().Throw<ArgumentException>().WithParameterName("values").WithMessage($"*'{path}'*");
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(200)]
    public void ReplaceValuesWithBinary_ValueSizeChanges_ProducesWellFormedDocument(Int32 payloadLength)
    {
        var payload = new Byte[payloadLength];
        var document = CreateNestedDocument();
        var expected = document.DeepClone().AsBsonDocument;
        SetPath(expected, "middle.innerMiddle", new BsonBinaryData(payload));

        var result = RawBsonElements.ReplaceValuesWithBinary(document.ToBson(), new Dictionary<String, Byte[]>
        {
            { "middle.innerMiddle", payload }
        });

        result.Should().Equal(expected.ToBson());
        result.Length.Should().Be(document.ToBson().Length - 8 + 5 + payloadLength);
    }

    [TestCase("missing")]
    [TestCase("first.inner")]
    [TestCase("middle.missing")]
    [TestCase("middle.innerLast.0")]
    public void TryLocate_MissingPath_ReturnsFalse(String path)
    {
        var result = RawBsonElements.TryLocate(CreateNestedDocument().ToBson(), path, out _, out _);

        result.Should().BeFalse();
    }

    [Test]
    public void TryLocate_NestedElement_ReturnsTypeAndValueBytes()
    {
        var bytes = CreateNestedDocument().ToBson();

        var result = RawBsonElements.TryLocate(bytes, "middle.innerMiddle", out var type, out var value);

        result.Should().BeTrue();
        type.Should().Be(BsonType.String);
        bytes[value].Should().Equal(ValueBytes("two"));
    }

    [Test]
    public void TryLocate_NullArguments_ThrowArgumentNullException()
    {
        var actDocument = () => RawBsonElements.TryLocate(null!, "path", out _, out _);
        var actPath = () => RawBsonElements.TryLocate(new BsonDocument().ToBson(), null!, out _, out _);

        actDocument.Should().Throw<ArgumentNullException>().WithParameterName("document");
        actPath.Should().Throw<ArgumentNullException>().WithParameterName("path");
    }

    [Test]
    public void TryLocate_NullValue_ReturnsNullTypeAndEmptyValue()
    {
        var bytes = CreateNestedDocument().ToBson();

        var result = RawBsonElements.TryLocate(bytes, "last", out var type, out var value);

        result.Should().BeTrue();
        type.Should().Be(BsonType.Null);
        bytes[value].Should().BeEmpty();
    }

    [TestCase("middle", BsonType.Document)]
    [TestCase("middle.innerLast", BsonType.Array)]
    public void TryLocate_StructuredLastSegment_ReturnsWholeValue(String path, BsonType expectedType)
    {
        var document = CreateNestedDocument();
        var bytes = document.ToBson();

        var result = RawBsonElements.TryLocate(bytes, path, out var type, out var value);

        result.Should().BeTrue();
        type.Should().Be(expectedType);
        bytes[value].Should().Equal(ValueBytes(GetPath(document, path)));
    }

    [Test]
    public void TryLocate_TopLevelElement_ReturnsTypeAndValueBytes()
    {
        var bytes = CreateNestedDocument().ToBson();

        var result = RawBsonElements.TryLocate(bytes, "first", out var type, out var value);

        result.Should().BeTrue();
        type.Should().Be(BsonType.String);
        bytes[value].Should().Equal(ValueBytes("one"));
    }

    private static BsonDocument CreateDocument(params String[] elementNames)
    {
        var document = new BsonDocument();
        foreach (var name in elementNames)
        {
            document.Add(name, new BsonDocument("value", name));
        }

        return document;
    }

    private static BsonDocument CreateNestedDocument()
        => new()
        {
            { "first", "one" },
            {
                "middle", new BsonDocument
                {
                    { "innerFirst", 1 },
                    { "innerMiddle", "two" },
                    {
                        "innerLast", new BsonArray
                        {
                            1,
                            2
                        }
                    }
                }
            },
            { "last", BsonNull.Value }
        };

    private static BsonValue GetPath(BsonDocument document, String path)
    {
        BsonValue current = document;
        foreach (var segment in path.Split('.'))
        {
            current = current[segment];
        }

        return current;
    }

    private static void SetPath(BsonDocument document, String path, BsonValue value)
    {
        var segments = path.Split('.');
        var current = document;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            current = current[segments[index]].AsBsonDocument;
        }

        current[segments[^1]] = value;
    }

    /// <summary>
    /// The stored bytes of a value: a single-element document without its length prefix, type byte, name and terminator.
    /// </summary>
    private static Byte[] ValueBytes(BsonValue value)
        => new BsonDocument("x", value).ToBson()[7..^1];
}
