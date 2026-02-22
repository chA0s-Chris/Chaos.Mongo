// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests;

using FluentAssertions;
using NUnit.Framework;

public class MongoEventStoreBuilderTests
{
    [Test]
    public void FluentChaining_AllMethodsReturnBuilder()
    {
        var builder = new MongoEventStoreBuilder<TestAggregate>();

        var result = builder
                     .WithCollectionPrefix("Test")
                     .WithCheckpoints(50)
                     .WithCheckpointCollectionSuffix("_Snap")
                     .WithEventsCollectionSuffix("_Evt")
                     .WithEvent<TestCreatedEvent>("Created");

        result.Should().BeSameAs(builder);
    }

    [Test]
    public void Options_DefaultValues_AreCorrect()
    {
        var builder = new MongoEventStoreBuilder<TestAggregate>();

        builder.Options.CollectionPrefix.Should().Be("TestAggregate");
        builder.Options.EventsCollectionSuffix.Should().Be("_Events");
        builder.Options.CheckpointCollectionSuffix.Should().Be("_Checkpoints");
        builder.Options.CheckpointInterval.Should().Be(0);
        builder.Options.CheckpointsEnabled.Should().BeFalse();
        builder.Options.EventTypes.Should().BeEmpty();
    }

    [Test]
    public void WithCheckpointCollectionSuffix_NullOrWhitespace_ThrowsArgumentException()
    {
        var builder = new MongoEventStoreBuilder<TestAggregate>();

        var actNull = () => builder.WithCheckpointCollectionSuffix(null!);
        var actEmpty = () => builder.WithCheckpointCollectionSuffix("");
        var actWhitespace = () => builder.WithCheckpointCollectionSuffix("   ");

        actNull.Should().Throw<ArgumentException>();
        actEmpty.Should().Throw<ArgumentException>();
        actWhitespace.Should().Throw<ArgumentException>();
    }

    [Test]
    public void WithCheckpointCollectionSuffix_ValidValue_SetsOption()
    {
        var builder = new MongoEventStoreBuilder<TestAggregate>();

        builder.WithCheckpointCollectionSuffix("_Snapshots");

        builder.Options.CheckpointCollectionSuffix.Should().Be("_Snapshots");
    }

    [Test]
    public void WithCheckpoints_ValidInterval_SetsOption()
    {
        var builder = new MongoEventStoreBuilder<TestAggregate>();

        builder.WithCheckpoints(100);

        builder.Options.CheckpointInterval.Should().Be(100);
        builder.Options.CheckpointsEnabled.Should().BeTrue();
    }

    [Test]
    public void WithCheckpoints_ZeroOrNegative_ThrowsArgumentOutOfRangeException()
    {
        var builder = new MongoEventStoreBuilder<TestAggregate>();

        var actZero = () => builder.WithCheckpoints(0);
        var actNegative = () => builder.WithCheckpoints(-5);

        actZero.Should().Throw<ArgumentOutOfRangeException>();
        actNegative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void WithCollectionPrefix_NullOrWhitespace_ThrowsArgumentException()
    {
        var builder = new MongoEventStoreBuilder<TestAggregate>();

        var actNull = () => builder.WithCollectionPrefix(null!);
        var actEmpty = () => builder.WithCollectionPrefix("");
        var actWhitespace = () => builder.WithCollectionPrefix("   ");

        actNull.Should().Throw<ArgumentException>();
        actEmpty.Should().Throw<ArgumentException>();
        actWhitespace.Should().Throw<ArgumentException>();
    }

    [Test]
    public void WithCollectionPrefix_ValidValue_SetsOption()
    {
        var builder = new MongoEventStoreBuilder<TestAggregate>();

        builder.WithCollectionPrefix("MyAggregate");

        builder.Options.CollectionPrefix.Should().Be("MyAggregate");
        builder.Options.EventsCollectionName.Should().Be("MyAggregate_Events");
        builder.Options.CheckpointCollectionName.Should().Be("MyAggregate_Checkpoints");
        builder.Options.ReadModelCollectionName.Should().Be("MyAggregate");
    }

    [Test]
    public void WithEvent_NoDiscriminator_UsesClassName()
    {
        var builder = new MongoEventStoreBuilder<TestAggregate>();

        builder.WithEvent<TestCreatedEvent>();

        builder.Options.EventTypes.Should().ContainKey(typeof(TestCreatedEvent));
        builder.Options.EventTypes[typeof(TestCreatedEvent)].Should().Be("TestCreatedEvent");
    }

    [Test]
    public void WithEvent_WithDiscriminator_UsesProvidedDiscriminator()
    {
        var builder = new MongoEventStoreBuilder<TestAggregate>();

        builder.WithEvent<TestCreatedEvent>("Created");

        builder.Options.EventTypes[typeof(TestCreatedEvent)].Should().Be("Created");
    }

    [Test]
    public void WithEventsCollectionSuffix_NullOrWhitespace_ThrowsArgumentException()
    {
        var builder = new MongoEventStoreBuilder<TestAggregate>();

        var actNull = () => builder.WithEventsCollectionSuffix(null!);
        var actEmpty = () => builder.WithEventsCollectionSuffix("");
        var actWhitespace = () => builder.WithEventsCollectionSuffix("   ");

        actNull.Should().Throw<ArgumentException>();
        actEmpty.Should().Throw<ArgumentException>();
        actWhitespace.Should().Throw<ArgumentException>();
    }

    [Test]
    public void WithEventsCollectionSuffix_ValidValue_SetsOption()
    {
        var builder = new MongoEventStoreBuilder<TestAggregate>();

        builder.WithEventsCollectionSuffix("_DomainEvents");

        builder.Options.EventsCollectionSuffix.Should().Be("_DomainEvents");
    }

    private sealed class TestAggregate : IAggregate
    {
        public DateTime CreatedUtc { get; set; }
        public Guid Id { get; set; }
        public Int64 Version { get; set; }
    }

    private sealed class TestCreatedEvent : Event<TestAggregate>
    {
        public override void Execute(TestAggregate aggregate) { }
    }
}
