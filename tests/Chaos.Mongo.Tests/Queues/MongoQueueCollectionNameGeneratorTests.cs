// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Queues;

using Chaos.Mongo.Queues;
using FluentAssertions;
using NUnit.Framework;
using System.IO.Hashing;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

public class MongoQueueCollectionNameGeneratorTests
{
    [Test]
    public void GenerateQueueCollectionName_CalledMultipleTimes_ReturnsSameResult()
    {
        // Arrange
        var generator = new MongoQueueCollectionNameGenerator();
        var payloadType = typeof(TestPayload);

        // Act
        var result1 = generator.GenerateQueueCollectionName(payloadType);
        var result2 = generator.GenerateQueueCollectionName(payloadType);

        // Assert
        result1.Should().Be(result2);
    }

    [Test]
    public void GenerateQueueCollectionName_GenericPayloadAcrossAssemblyVersions_ReturnsIdenticalName()
    {
        // Arrange
        var generator = new MongoQueueCollectionNameGenerator();
        var versionedTypeV1 = EmitVersionedPayloadType(new Version(1, 0, 0, 0));
        var versionedTypeV2 = EmitVersionedPayloadType(new Version(2, 0, 0, 0));

        // The array wrapping is required: Type.FullName for an array of a closed generic type embeds the element
        // type's assembly-qualified name, even though the array type itself is not a generic type.
        var closedPayloadV1 = typeof(GenericPayload<>).MakeGenericType(typeof(List<>).MakeGenericType(versionedTypeV1).MakeArrayType());
        var closedPayloadV2 = typeof(GenericPayload<>).MakeGenericType(typeof(List<>).MakeGenericType(versionedTypeV2).MakeArrayType());

        // Act
        var nameV1 = generator.GenerateQueueCollectionName(closedPayloadV1);
        var nameV2 = generator.GenerateQueueCollectionName(closedPayloadV2);

        // Assert
        nameV1.Should().Be(nameV2);
    }

    [Test]
    public void GenerateQueueCollectionName_WhenPayloadTypeIsNull_ThrowsArgumentNullException()
    {
        // Arrange
        var generator = new MongoQueueCollectionNameGenerator();

        // Act
        var act = () => generator.GenerateQueueCollectionName(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void GenerateQueueCollectionName_WithClosedGenericPayloadType_IncludesHashOfTypeArguments()
    {
        // Arrange
        var generator = new MongoQueueCollectionNameGenerator();
        var payloadType = typeof(GenericPayload<PayloadArgument>);

        // Act
        var result = generator.GenerateQueueCollectionName(payloadType);

        // Assert
        // This name is persisted as a MongoDB collection name; pinned as a literal so moving or renaming
        // GenericPayload<T>/PayloadArgument, or changing the hashing/formatting algorithm, fails this test instead
        // of silently stranding existing queue data under the compatibility policy's frozen-name guarantee.
        result.Should().Be("_Queue.CA8729864FC82514.GenericPayload`1");
    }

    [Test]
    public void GenerateQueueCollectionName_WithDifferentTypes_ReturnsDifferentResults()
    {
        // Arrange
        var generator = new MongoQueueCollectionNameGenerator();
        var payloadType1 = typeof(TestPayload);
        var payloadType2 = typeof(AnotherTestPayload);

        // Act
        var result1 = generator.GenerateQueueCollectionName(payloadType1);
        var result2 = generator.GenerateQueueCollectionName(payloadType2);

        // Assert
        result1.Should().NotBe(result2);
        result1.Should().EndWith(payloadType1.Name);
        result2.Should().EndWith(payloadType2.Name);
    }

    [Test]
    public void GenerateQueueCollectionName_WithNestedType_IncludesCorrectName()
    {
        // Arrange
        var generator = new MongoQueueCollectionNameGenerator();
        var payloadType = typeof(NestedContainer.NestedPayload);

        // Act
        var result = generator.GenerateQueueCollectionName(payloadType);

        // Assert
        result.Should().StartWith("_Queue.");
        result.Should().EndWith("NestedPayload");
    }

    [Test]
    public void GenerateQueueCollectionName_WithValidType_IncludesHashOfFullName()
    {
        // Arrange
        var generator = new MongoQueueCollectionNameGenerator();
        var payloadType = typeof(TestPayload);
        var expectedHash = Convert.ToHexString(XxHash3.Hash(Encoding.UTF8.GetBytes(payloadType.FullName!)));

        // Act
        var result = generator.GenerateQueueCollectionName(payloadType);

        // Assert
        result.Should().Contain(expectedHash);
        result.Should().Be($"_Queue.{expectedHash}.{payloadType.Name}");
    }

    [Test]
    public void GenerateQueueCollectionName_WithValidType_ReturnsCorrectFormat()
    {
        // Arrange
        var generator = new MongoQueueCollectionNameGenerator();
        var payloadType = typeof(TestPayload);

        // Act
        var result = generator.GenerateQueueCollectionName(payloadType);

        // Assert
        result.Should().StartWith("_Queue.");
        result.Should().Contain(payloadType.Name);
        result.Should().MatchRegex(@"^_Queue\.[A-F0-9]{16}\.TestPayload$");
    }

    /// <summary>
    /// Emits a throwaway in-memory assembly declaring a public type with a fixed namespace/name but the given
    /// version, so a test can close a generic payload definition over types that only differ by assembly version.
    /// </summary>
    private static Type EmitVersionedPayloadType(Version version)
    {
        var assemblyName = new AssemblyName("Chaos.Mongo.Tests.Queues.VersionedPayloadAssembly")
        {
            Version = version
        };
        var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
        var moduleBuilder = assemblyBuilder.DefineDynamicModule("MainModule");
        var typeBuilder = moduleBuilder.DefineType("Chaos.Mongo.Tests.Queues.VersionedPayload", TypeAttributes.Public);
        return typeBuilder.CreateType();
    }

    private static class NestedContainer
    {
        public class NestedPayload;
    }

    private class AnotherTestPayload;

    private sealed class GenericPayload<T>
    {
        public T? Value { get; set; }
    }

    private sealed class PayloadArgument;

    private class TestPayload;
}
