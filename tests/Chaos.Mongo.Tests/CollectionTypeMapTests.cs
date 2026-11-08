// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests;

using FluentAssertions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

public class CollectionTypeMapTests
{
    [Test]
    public void Ctor_WhenOptionsIsNull_ThrowsArgumentNull()
    {
        // Act
        var act = () => new CollectionTypeMap(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void GetCollectionName_WhenNotMappedAndDefaultNamesDisabled_ThrowsKeyNotFound()
    {
        // Arrange
        var options = new MongoOptions
        {
            UseDefaultCollectionNames = false,
            CollectionTypeMap = new Dictionary<Type, String>()
        };

        var sut = new CollectionTypeMap(Options.Create(options));

        // Act
        var act = () => sut.GetCollectionName(typeof(UnmappedType));

        // Assert
        act.Should().Throw<KeyNotFoundException>()
           .WithMessage("*is not mapped*default collection names are not used*");
    }

    [Test]
    public void GetCollectionName_WhenNotMappedAndDefaultNamesEnabled_ReturnsTypeName()
    {
        // Arrange
        var options = new MongoOptions
        {
            UseDefaultCollectionNames = true,
            CollectionTypeMap = new Dictionary<Type, String>()
        };

        var sut = new CollectionTypeMap(Options.Create(options));

        // Act
        var name = sut.GetCollectionName(typeof(UnmappedType));

        // Assert
        name.Should().Be(nameof(UnmappedType));
    }

    [Test]
    public void GetCollectionName_WhenNotMappedClosedGenericWithDifferentTypeArguments_ReturnsDifferentStableNames()
    {
        // Arrange
        var options = new MongoOptions
        {
            UseDefaultCollectionNames = true,
            CollectionTypeMap = new Dictionary<Type, String>()
        };

        var sut = new CollectionTypeMap(Options.Create(options));

        // Act
        var nameA = sut.GetCollectionName(typeof(Envelope<EnvelopePayloadA>));
        var nameB = sut.GetCollectionName(typeof(Envelope<EnvelopePayloadB>));

        // Assert
        nameA.Should().Be("Envelope<EnvelopePayloadA>");
        nameB.Should().Be("Envelope<EnvelopePayloadB>");
        nameA.Should().NotBe(nameB);
    }

    [Test]
    public void GetCollectionName_WhenNotMappedTypeNestedInClosedGeneric_ReturnsNameDistinctFromSameNamedGeneric()
    {
        // Arrange
        var options = new MongoOptions
        {
            UseDefaultCollectionNames = true,
            CollectionTypeMap = new Dictionary<Type, String>()
        };

        var sut = new CollectionTypeMap(Options.Create(options));

        // Act
        var nestedName = sut.GetCollectionName(typeof(EnvelopeContainer<EnvelopePayloadA>.Envelope));
        var genericName = sut.GetCollectionName(typeof(Envelope<EnvelopePayloadA>));

        // Assert
        nestedName.Should().Be("EnvelopeContainer<EnvelopePayloadA>+Envelope");
        genericName.Should().Be("Envelope<EnvelopePayloadA>");
    }

    [Test]
    public void GetCollectionName_WhenTypeIsNull_ThrowsArgumentNull()
    {
        // Arrange
        var options = new MongoOptions();
        var sut = new CollectionTypeMap(Options.Create(options));

        // Act
        var act = () => sut.GetCollectionName(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void GetCollectionName_WithTypeConfigured_ReturnsMappedName()
    {
        // Arrange
        var options = new MongoOptions
        {
            UseDefaultCollectionNames = false,
            CollectionTypeMap = new Dictionary<Type, String>
            {
                { typeof(MappedType), "mapped_collection" }
            }
        };

        var sut = new CollectionTypeMap(Options.Create(options));

        // Act
        var name = sut.GetCollectionName(typeof(MappedType));

        // Assert
        name.Should().Be("mapped_collection");
    }

    // Only the type name and its generic arity matter for the collection names under test.
    // ReSharper disable once UnusedTypeParameter
    private static class EnvelopeContainer<T>
    {
        public sealed class Envelope;
    }

    private sealed class Envelope<T>
    {
        // Only the type name and its generic arity matter for the collection names under test.
        // ReSharper disable once UnusedMember.Local
        public T? Payload { get; set; }
    }

    private sealed class EnvelopePayloadA { }

    private sealed class EnvelopePayloadB { }

    private sealed class MappedType { }

    private sealed class UnmappedType { }
}
