// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Queues;

using Chaos.Mongo.Queues;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

public class MongoQueueBuilderTests
{
    [Test]
    public void Constructor_WhenServicesIsNull_ThrowsArgumentNullException()
    {
        // Arrange & Act
        var act = () => new MongoQueueBuilder<TestPayload>(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Constructor_WithValidServices_SuccessfullyCreatesInstance()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var builder = new MongoQueueBuilder<TestPayload>(services);

        // Assert
        builder.Should().NotBeNull();
    }

    [Test]
    public void RegisterQueue_CalledTwice_OnlyRegistersOnce()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoQueueBuilder<TestPayload>(services);
        builder.WithPayloadHandler<TestPayloadHandler>();

        // Act
        builder.RegisterQueue();
        var countAfterFirst = services.Count(s => s.ServiceType == typeof(IMongoQueue<TestPayload>));
        builder.RegisterQueue();
        var countAfterSecond = services.Count(s => s.ServiceType == typeof(IMongoQueue<TestPayload>));

        // Assert
        countAfterFirst.Should().Be(1);
        countAfterSecond.Should().Be(1);
    }

    [Test]
    public async Task RegisterQueue_WithClosedItemRetention_UsesConfiguredRetention()
    {
        // Arrange
        var services = CreateServices();
        var builder = new MongoQueueBuilder<TestPayload>(services);
        var retention = TimeSpan.FromMinutes(15);
        builder.WithCollectionName("test-queue");
        builder.WithPayloadHandler<TestPayloadHandler>();
        builder.WithClosedItemRetention(retention);

        // Act
        builder.RegisterQueue();
        await using var serviceProvider = services.BuildServiceProvider();
        var queue = serviceProvider.GetRequiredService<IMongoQueue<TestPayload>>();

        // Assert
        queue.QueueDefinition.ClosedItemRetention.Should().Be(retention);
    }

    [Test]
    public async Task RegisterQueue_WithImmediateDelete_UsesNullRetention()
    {
        // Arrange
        var services = CreateServices();
        var builder = new MongoQueueBuilder<TestPayload>(services);
        builder.WithCollectionName("test-queue");
        builder.WithPayloadHandler<TestPayloadHandler>();
        builder.WithImmediateDelete();

        // Act
        builder.RegisterQueue();
        await using var serviceProvider = services.BuildServiceProvider();
        var queue = serviceProvider.GetRequiredService<IMongoQueue<TestPayload>>();

        // Assert
        queue.QueueDefinition.ClosedItemRetention.Should().BeNull();
    }

    [Test]
    public async Task RegisterQueue_WithLockLeaseTime_UsesConfiguredLockLeaseTime()
    {
        // Arrange
        var services = CreateServices();
        var builder = new MongoQueueBuilder<TestPayload>(services);
        var lockLeaseTime = TimeSpan.FromSeconds(30);
        builder.WithCollectionName("test-queue");
        builder.WithPayloadHandler<TestPayloadHandler>();
        builder.WithLockLeaseTime(lockLeaseTime);

        // Act
        builder.RegisterQueue();
        await using var serviceProvider = services.BuildServiceProvider();
        var queue = serviceProvider.GetRequiredService<IMongoQueue<TestPayload>>();

        // Assert
        queue.QueueDefinition.LockLeaseTime.Should().Be(lockLeaseTime);
    }

    [Test]
    public async Task RegisterQueue_WithoutClosedItemRetention_UsesDefaultRetention()
    {
        // Arrange
        var services = CreateServices();
        var builder = new MongoQueueBuilder<TestPayload>(services);
        builder.WithCollectionName("test-queue");
        builder.WithPayloadHandler<TestPayloadHandler>();

        // Act
        builder.RegisterQueue();
        await using var serviceProvider = services.BuildServiceProvider();
        var queue = serviceProvider.GetRequiredService<IMongoQueue<TestPayload>>();

        // Assert
        queue.QueueDefinition.ClosedItemRetention.Should().Be(MongoDefaults.QueueClosedItemRetention);
    }

    [Test]
    public async Task RegisterQueue_WithoutLockLeaseTime_UsesDefaultLockLeaseTime()
    {
        // Arrange
        var services = CreateServices();
        var builder = new MongoQueueBuilder<TestPayload>(services);
        builder.WithCollectionName("test-queue");
        builder.WithPayloadHandler<TestPayloadHandler>();

        // Act
        builder.RegisterQueue();
        await using var serviceProvider = services.BuildServiceProvider();
        var queue = serviceProvider.GetRequiredService<IMongoQueue<TestPayload>>();

        // Assert
        queue.QueueDefinition.LockLeaseTime.Should().Be(MongoDefaults.QueueLockLeaseTime);
    }

    [Test]
    public void RegisterQueue_WithoutPayloadHandler_ThrowsInvalidOperationException()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoQueueBuilder<TestPayload>(services);

        // Act
        var act = () => builder.RegisterQueue();

        // Assert
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("Payload handler type or factory must be specified.");
    }

    [Test]
    public void Validate_WhenAlreadyRegistered_ThrowsInvalidOperationException()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IMongoQueue<TestPayload>>(_ => null!);
        var builder = new MongoQueueBuilder<TestPayload>(services);
        builder.WithPayloadHandler<TestPayloadHandler>();

        // Act
        var act = () => builder.RegisterQueue();

        // Assert
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("A registration for a MongoDB queue with payload TestPayload already exists.");
    }

    [Test]
    public void Validate_WithBothHandlerTypeAndFactory_ThrowsInvalidOperationException()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoQueueBuilder<TestPayload>(services);
        builder.WithPayloadHandler<TestPayloadHandler>();
        builder.WithPayloadHandler(_ => new TestPayloadHandler());

        // Act
        var act = () => builder.RegisterQueue();

        // Assert
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("Payload handler type and factory cannot be specified together.");
    }

    [Test]
    public void WithAutoStartSubscription_ReturnsBuilderForChaining()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoQueueBuilder<TestPayload>(services);

        // Act
        var result = builder.WithAutoStartSubscription();

        // Assert
        result.Should().BeSameAs(builder);
    }

    [Test]
    public void WithClosedItemRetention_WithNegativeValue_ThrowsArgumentException()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoQueueBuilder<TestPayload>(services);

        // Act
        var act = () => builder.WithClosedItemRetention(TimeSpan.FromSeconds(-1));

        // Assert
        act.Should().Throw<ArgumentException>()
           .WithMessage("Closed item retention must be greater than 0.*");
    }

    [Test]
    public void WithClosedItemRetention_WithPositiveValue_ReturnsBuilderForChaining()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoQueueBuilder<TestPayload>(services);

        // Act
        var result = builder.WithClosedItemRetention(TimeSpan.FromMinutes(5));

        // Assert
        result.Should().BeSameAs(builder);
    }

    [Test]
    public void WithClosedItemRetention_WithZeroValue_ThrowsArgumentException()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoQueueBuilder<TestPayload>(services);

        // Act
        var act = () => builder.WithClosedItemRetention(TimeSpan.Zero);

        // Assert
        act.Should().Throw<ArgumentException>()
           .WithMessage("Closed item retention must be greater than 0.*");
    }

    [Test]
    public void WithCollectionName_WhenCollectionNameIsEmpty_ThrowsArgumentException()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoQueueBuilder<TestPayload>(services);

        // Act
        var act = () => builder.WithCollectionName(String.Empty);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void WithCollectionName_WhenCollectionNameIsNull_ThrowsArgumentException()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoQueueBuilder<TestPayload>(services);

        // Act
        var act = () => builder.WithCollectionName(null!);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void WithCollectionName_WhenCollectionNameIsWhitespace_ThrowsArgumentException()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoQueueBuilder<TestPayload>(services);

        // Act
        var act = () => builder.WithCollectionName("   ");

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void WithCollectionName_WithValidName_ReturnsBuilderForChaining()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoQueueBuilder<TestPayload>(services);

        // Act
        var result = builder.WithCollectionName("test-queue");

        // Assert
        result.Should().BeSameAs(builder);
    }

    [Test]
    public void WithImmediateDelete_ReturnsBuilderForChaining()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoQueueBuilder<TestPayload>(services);

        // Act
        var result = builder.WithImmediateDelete();

        // Assert
        result.Should().BeSameAs(builder);
    }

    [Test]
    public void WithLockLeaseTime_WithNegativeValue_ThrowsArgumentException()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoQueueBuilder<TestPayload>(services);

        // Act
        var act = () => builder.WithLockLeaseTime(TimeSpan.FromSeconds(-1));

        // Assert
        act.Should().Throw<ArgumentException>()
           .WithMessage("Lock lease time must be greater than 0.*");
    }

    [Test]
    public void WithLockLeaseTime_WithPositiveValue_ReturnsBuilderForChaining()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoQueueBuilder<TestPayload>(services);

        // Act
        var result = builder.WithLockLeaseTime(TimeSpan.FromSeconds(30));

        // Assert
        result.Should().BeSameAs(builder);
    }

    [Test]
    public void WithLockLeaseTime_WithZeroValue_ThrowsArgumentException()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoQueueBuilder<TestPayload>(services);

        // Act
        var act = () => builder.WithLockLeaseTime(TimeSpan.Zero);

        // Assert
        act.Should().Throw<ArgumentException>()
           .WithMessage("Lock lease time must be greater than 0.*");
    }

    [Test]
    public void WithoutAutoStartSubscription_ReturnsBuilderForChaining()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoQueueBuilder<TestPayload>(services);

        // Act
        var result = builder.WithoutAutoStartSubscription();

        // Assert
        result.Should().BeSameAs(builder);
    }

    [Test]
    public void WithPayloadHandler_GenericVersion_ReturnsBuilderForChaining()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoQueueBuilder<TestPayload>(services);

        // Act
        var result = builder.WithPayloadHandler<TestPayloadHandler>();

        // Assert
        result.Should().BeSameAs(builder);
    }

    [Test]
    public void WithPayloadHandler_WithAbstractType_ThrowsInvalidOperationException()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoQueueBuilder<TestPayload>(services);

        // Act
        var act = () => builder.WithPayloadHandler(typeof(AbstractPayloadHandler));

        // Assert
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("Payload handler type must be a non-abstract class implementing *");
    }

    [Test]
    public void WithPayloadHandler_WithFactory_ReturnsBuilderForChaining()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoQueueBuilder<TestPayload>(services);

        // Act
        var result = builder.WithPayloadHandler(_ => new TestPayloadHandler());

        // Assert
        result.Should().BeSameAs(builder);
    }

    [Test]
    public void WithPayloadHandler_WithFactoryNull_ThrowsArgumentNullException()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoQueueBuilder<TestPayload>(services);

        // Act
        var act = () => builder.WithPayloadHandler((Func<IServiceProvider, IMongoQueuePayloadHandler<TestPayload>>)null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void WithPayloadHandler_WithInterface_ThrowsInvalidOperationException()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoQueueBuilder<TestPayload>(services);

        // Act
        var act = () => builder.WithPayloadHandler(typeof(IMongoQueuePayloadHandler<TestPayload>));

        // Assert
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("Payload handler type must be a non-abstract class implementing *");
    }

    [Test]
    public void WithPayloadHandler_WithNullType_ThrowsArgumentNullException()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoQueueBuilder<TestPayload>(services);

        // Act
        var act = () => builder.WithPayloadHandler((Type)null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void WithPayloadHandler_WithType_ReturnsBuilderForChaining()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoQueueBuilder<TestPayload>(services);

        // Act
        var result = builder.WithPayloadHandler(typeof(TestPayloadHandler));

        // Assert
        result.Should().BeSameAs(builder);
    }

    [Test]
    public void WithPayloadHandler_WithWrongInterfaceType_ThrowsInvalidOperationException()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoQueueBuilder<TestPayload>(services);

        // Act
        var act = () => builder.WithPayloadHandler(typeof(WrongPayloadHandler));

        // Assert
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("Payload handler type must be a non-abstract class implementing *");
    }

    [Test]
    public void WithQueueLimit_WithNegativeValue_ThrowsArgumentException()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoQueueBuilder<TestPayload>(services);

        // Act
        var act = () => builder.WithQueryLimit(-1);

        // Assert
        act.Should().Throw<ArgumentException>()
           .WithMessage("Query limit must be greater than 0.*");
    }

    [Test]
    public void WithQueueLimit_WithPositiveValue_ReturnsBuilderForChaining()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoQueueBuilder<TestPayload>(services);

        // Act
        var result = builder.WithQueryLimit(10);

        // Assert
        result.Should().BeSameAs(builder);
    }

    [Test]
    public void WithQueueLimit_WithZeroValue_ThrowsArgumentException()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoQueueBuilder<TestPayload>(services);

        // Act
        var act = () => builder.WithQueryLimit(0);

        // Assert
        act.Should().Throw<ArgumentException>()
           .WithMessage("Query limit must be greater than 0.*");
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IMongoQueueCollectionNameGenerator>());
        services.AddSingleton(Mock.Of<IMongoQueueSubscriptionFactory>());
        services.AddSingleton(Mock.Of<IMongoQueuePublisher>());
        return services;
    }

    public abstract class AbstractPayloadHandler : IMongoQueuePayloadHandler<TestPayload>
    {
        public abstract Task HandlePayloadAsync(TestPayload payload, CancellationToken cancellationToken = default);
    }

    public class AnotherPayload;

    public class TestPayload;

    public class TestPayloadHandler : IMongoQueuePayloadHandler<TestPayload>
    {
        public Task HandlePayloadAsync(TestPayload payload, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    public class WrongPayloadHandler : IMongoQueuePayloadHandler<AnotherPayload>
    {
        public Task HandlePayloadAsync(AnotherPayload payload, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
