// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using MongoDB.Bson;
using NUnit.Framework;

public class RawBsonElementsTests
{
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

    private static BsonDocument CreateDocument(params String[] elementNames)
    {
        var document = new BsonDocument();
        foreach (var name in elementNames)
        {
            document.Add(name, new BsonDocument("value", name));
        }

        return document;
    }
}
