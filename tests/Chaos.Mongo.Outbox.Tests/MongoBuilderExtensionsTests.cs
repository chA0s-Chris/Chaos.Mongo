// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests;

using Chaos.Mongo.Configuration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using NUnit.Framework;

public class MongoBuilderExtensionsTests
{
    [Test]
    public void WithOutbox_BuildsMessageTypeLookup()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMongoHelper>(new FakeMongoHelper());
        services.AddSingleton(TimeProvider.System);
        var builder = new MongoBuilder(services);

        builder.WithOutbox(o => o
                                .WithPublisher<TestOutboxPublisher>()
                                .WithMessage<TestPayload>("Test")
                                .WithMessage<AnotherTestPayload>("Another"));

        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<OutboxOptions>();

        options.MessageTypeLookup.Should().ContainKey(typeof(TestPayload));
        options.MessageTypeLookup[typeof(TestPayload)].Should().Be("Test");
        options.MessageTypeLookup.Should().ContainKey(typeof(AnotherTestPayload));
        options.MessageTypeLookup[typeof(AnotherTestPayload)].Should().Be("Another");
    }

    [Test]
    public void WithOutbox_DuplicateRegistration_ThrowsInvalidOperationException()
    {
        var services = new ServiceCollection();
        var builder = new MongoBuilder(services);

        builder.WithOutbox(o => o
                                .WithPublisher<TestOutboxPublisher>()
                                .WithMessage<TestPayload>());

        var act = () => builder.WithOutbox(o => o
                                                .WithPublisher<TestOutboxPublisher>()
                                                .WithMessage<TestPayload>());

        act.Should().Throw<InvalidOperationException>().WithMessage("*already registered*");
    }

    [Test]
    public void WithOutbox_NoMessageTypes_ThrowsInvalidOperationException()
    {
        var services = new ServiceCollection();
        var builder = new MongoBuilder(services);

        var act = () => builder.WithOutbox(o => o.WithPublisher<TestOutboxPublisher>());

        act.Should().Throw<InvalidOperationException>().WithMessage("*message type*");
    }

    [Test]
    public void WithOutbox_NoPublisher_ThrowsInvalidOperationException()
    {
        var services = new ServiceCollection();
        var builder = new MongoBuilder(services);

        var act = () => builder.WithOutbox(o => o.WithMessage<TestPayload>());

        act.Should().Throw<InvalidOperationException>().WithMessage("*IOutboxPublisher*");
    }

    [Test]
    public void WithOutbox_NullBuilder_ThrowsArgumentNullException()
    {
        MongoBuilder builder = null!;

        var act = () => builder.WithOutbox(_ => { });

        act.Should().Throw<ArgumentNullException>().WithParameterName("builder");
    }

    [Test]
    public void WithOutbox_NullConfigure_ThrowsArgumentNullException()
    {
        var services = new ServiceCollection();
        var builder = new MongoBuilder(services);

        var act = () => builder.WithOutbox(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("configure");
    }

    [Test]
    public void WithOutbox_RegistersConfigurator()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMongoHelper>(new FakeMongoHelper());
        services.AddSingleton(TimeProvider.System);
        var builder = new MongoBuilder(services);

        builder.WithOutbox(o => o
                                .WithPublisher<TestOutboxPublisher>()
                                .WithMessage<TestPayload>());

        var sp = services.BuildServiceProvider();
        var configurators = sp.GetServices<IMongoConfigurator>().ToList();

        configurators.Should().ContainSingle(c => c is OutboxConfigurator);
    }

    [Test]
    public void WithOutbox_RegistersIOutbox()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMongoHelper>(new FakeMongoHelper());
        services.AddSingleton(TimeProvider.System);
        var builder = new MongoBuilder(services);

        builder.WithOutbox(o => o
                                .WithPublisher<TestOutboxPublisher>()
                                .WithMessage<TestPayload>());

        var sp = services.BuildServiceProvider();
        var outbox = sp.GetService<IOutbox>();

        outbox.Should().NotBeNull();
        outbox.Should().BeOfType<MongoOutbox>();
    }

    [Test]
    public void WithOutbox_RegistersIOutboxProcessor()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMongoHelper>(new FakeMongoHelper());
        services.AddSingleton(TimeProvider.System);
        services.AddLogging();
        var builder = new MongoBuilder(services);

        builder.WithOutbox(o => o
                                .WithPublisher<TestOutboxPublisher>()
                                .WithMessage<TestPayload>());

        var sp = services.BuildServiceProvider();
        var processor = sp.GetService<IOutboxProcessor>();

        processor.Should().NotBeNull();
        processor.Should().BeOfType<OutboxProcessor>();
    }

    [Test]
    public void WithOutbox_RegistersOutboxOptions()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMongoHelper>(new FakeMongoHelper());
        services.AddSingleton(TimeProvider.System);
        var builder = new MongoBuilder(services);

        builder.WithOutbox(o => o
                                .WithPublisher<TestOutboxPublisher>()
                                .WithMessage<TestPayload>("Test")
                                .WithCollectionName("MyOutbox"));

        var sp = services.BuildServiceProvider();
        var options = sp.GetService<OutboxOptions>();

        options.Should().NotBeNull();
        options.CollectionName.Should().Be("MyOutbox");
        options.MessageTypeLookup.Should().ContainKey(typeof(TestPayload));
    }

    [Test]
    public void WithOutbox_RegistersPublisher()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMongoHelper>(new FakeMongoHelper());
        services.AddSingleton(TimeProvider.System);
        var builder = new MongoBuilder(services);

        builder.WithOutbox(o => o
                                .WithPublisher<TestOutboxPublisher>()
                                .WithMessage<TestPayload>());

        var sp = services.BuildServiceProvider();
        var publisher = sp.GetService<IOutboxPublisher>();

        publisher.Should().NotBeNull();
        publisher.Should().BeOfType<TestOutboxPublisher>();
    }

    [Test]
    public void WithOutbox_ReturnsSameBuilder()
    {
        var services = new ServiceCollection();
        var builder = new MongoBuilder(services);

        var result = builder.WithOutbox(o => o
                                             .WithPublisher<TestOutboxPublisher>()
                                             .WithMessage<TestPayload>());

        result.Should().BeSameAs(builder);
    }

    [Test]
    public void WithOutbox_WithAutoStart_RegistersHostedService()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMongoHelper>(new FakeMongoHelper());
        services.AddSingleton(TimeProvider.System);
        var builder = new MongoBuilder(services);

        builder.WithOutbox(o => o
                                .WithPublisher<TestOutboxPublisher>()
                                .WithMessage<TestPayload>()
                                .WithAutoStartProcessor());

        services.Should().Contain(d => d.ImplementationType == typeof(OutboxHostedService));
    }

    [Test]
    public void WithOutbox_WithoutAutoStart_DoesNotRegisterHostedService()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMongoHelper>(new FakeMongoHelper());
        services.AddSingleton(TimeProvider.System);
        var builder = new MongoBuilder(services);

        builder.WithOutbox(o => o
                                .WithPublisher<TestOutboxPublisher>()
                                .WithMessage<TestPayload>());

        services.Should().NotContain(d => d.ImplementationType == typeof(OutboxHostedService));
    }

    private sealed class FakeMongoHelper : IMongoHelper
    {
        public IMongoClient Client => throw new NotImplementedException();
        public IMongoDatabase Database => throw new NotImplementedException();

        public IMongoCollection<TDocument> GetCollection<TDocument>(MongoCollectionSettings? settings = null)
            => throw new NotImplementedException();

        public Task<IMongoLock?> TryAcquireLockAsync(String lockName, TimeSpan? leaseTime = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }
}
