// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests;

using Chaos.Mongo.Configuration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

public class MongoBuilderExtensionsTests
{
    [Test]
    public void WithEventStore_DuplicateRegistration_ThrowsInvalidOperationException()
    {
        var services = new ServiceCollection();
        var builder = new MongoBuilder(services);

        builder.WithEventStore<TestAggregate>(es => es.WithEvent<TestCreatedEvent>());

        var act = () => builder.WithEventStore<TestAggregate>(es => es.WithEvent<TestCreatedEvent>());

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*TestAggregate*already registered*");
    }

    [Test]
    public void WithEventStore_NullBuilder_ThrowsArgumentNullException()
    {
        MongoBuilder builder = null!;

        var act = () => builder.WithEventStore<TestAggregate>(_ => { });

        act.Should().Throw<ArgumentNullException>().WithParameterName("builder");
    }

    [Test]
    public void WithEventStore_NullConfigure_ThrowsArgumentNullException()
    {
        var services = new ServiceCollection();
        var builder = new MongoBuilder(services);

        var act = () => builder.WithEventStore<TestAggregate>(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("configure");
    }

    [Test]
    public void WithEventStore_RegistersAggregateRepository()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMongoHelper>(new FakeMongoHelper());
        var builder = new MongoBuilder(services);

        builder.WithEventStore<TestAggregate>(es => es.WithEvent<TestCreatedEvent>());

        var sp = services.BuildServiceProvider();
        var repository = sp.GetService<IAggregateRepository<TestAggregate>>();

        repository.Should().NotBeNull();
        repository.Should().BeOfType<MongoAggregateRepository<TestAggregate>>();
    }

    [Test]
    public void WithEventStore_RegistersConfigurator()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMongoHelper>(new FakeMongoHelper());
        var builder = new MongoBuilder(services);

        builder.WithEventStore<TestAggregate>(es => es.WithEvent<TestCreatedEvent>());

        var sp = services.BuildServiceProvider();
        var configurators = sp.GetServices<IMongoConfigurator>().ToList();

        configurators.Should().ContainSingle(c => c is MongoEventStoreConfigurator<TestAggregate>);
    }

    [Test]
    public void WithEventStore_RegistersEventStore()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMongoHelper>(new FakeMongoHelper());
        var builder = new MongoBuilder(services);

        builder.WithEventStore<TestAggregate>(es => es.WithEvent<TestCreatedEvent>());

        var sp = services.BuildServiceProvider();
        var eventStore = sp.GetService<IEventStore<TestAggregate>>();

        eventStore.Should().NotBeNull();
        eventStore.Should().BeOfType<MongoEventStore<TestAggregate>>();
    }

    [Test]
    public void WithEventStore_RegistersOptions()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMongoHelper>(new FakeMongoHelper());
        var builder = new MongoBuilder(services);

        builder.WithEventStore<TestAggregate>(es => es
                                                    .WithCollectionPrefix("MyPrefix")
                                                    .WithEvent<TestCreatedEvent>("Created"));

        var sp = services.BuildServiceProvider();
        var options = sp.GetService<MongoEventStoreOptions<TestAggregate>>();

        options.Should().NotBeNull();
        options.CollectionPrefix.Should().Be("MyPrefix");
        options.EventTypes.Should().ContainKey(typeof(TestCreatedEvent));
    }

    [Test]
    public void WithEventStore_ReturnsSameBuilder()
    {
        var services = new ServiceCollection();
        var builder = new MongoBuilder(services);

        var result = builder.WithEventStore<TestAggregate>(es => es.WithEvent<TestCreatedEvent>());

        result.Should().BeSameAs(builder);
    }

    private sealed class FakeMongoHelper : IMongoHelper
    {
        public MongoDB.Driver.IMongoClient Client => throw new NotImplementedException();
        public MongoDB.Driver.IMongoDatabase Database => throw new NotImplementedException();

        public MongoDB.Driver.IMongoCollection<TDocument> GetCollection<TDocument>(MongoDB.Driver.MongoCollectionSettings? settings = null)
            => throw new NotImplementedException();

        public Task<IMongoLock?> TryAcquireLockAsync(String lockName, TimeSpan? leaseTime = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
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
