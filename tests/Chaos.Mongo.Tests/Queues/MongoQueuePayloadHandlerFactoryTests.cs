// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Queues;

using Chaos.Mongo.Queues;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

public class MongoQueuePayloadHandlerFactoryTests
{
    [Test]
    public void CreateHandler_CalledMultipleTimes_ReturnsNewInstanceEachTime()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddTransient<IMongoQueuePayloadHandler<TestPayload>, TestPayloadHandler>();
        var serviceProvider = services.BuildServiceProvider();
        var factory = new MongoQueuePayloadHandlerFactory();

        // Act
        var handler1 = factory.CreateHandler<TestPayload>(serviceProvider);
        var handler2 = factory.CreateHandler<TestPayload>(serviceProvider);

        // Assert
        handler1.Should().NotBeNull();
        handler2.Should().NotBeNull();
        handler1.Should().NotBeSameAs(handler2);
    }

    [Test]
    public void CreateHandler_WhenHandlerIsNotRegistered_ThrowsInvalidOperationException()
    {
        // Arrange
        var services = new ServiceCollection();
        var serviceProvider = services.BuildServiceProvider();
        var factory = new MongoQueuePayloadHandlerFactory();

        // Act
        var act = () => factory.CreateHandler<TestPayload>(serviceProvider);

        // Assert
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("No handler for queue payload * registered.");
    }

    [Test]
    public void CreateHandler_WhenHandlerIsRegistered_ReturnsHandler()
    {
        // Arrange
        var mockHandler = new Mock<IMongoQueuePayloadHandler<TestPayload>>();
        var services = new ServiceCollection();
        services.AddSingleton(mockHandler.Object);
        var serviceProvider = services.BuildServiceProvider();
        var factory = new MongoQueuePayloadHandlerFactory();

        // Act
        var result = factory.CreateHandler<TestPayload>(serviceProvider);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeSameAs(mockHandler.Object);
    }

    [Test]
    public void CreateHandler_WhenServiceProviderIsNull_ThrowsArgumentNullException()
    {
        // Arrange
        var factory = new MongoQueuePayloadHandlerFactory();

        // Act
        var act = () => factory.CreateHandler<TestPayload>(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    public class TestPayload;

    public class TestPayloadHandler : IMongoQueuePayloadHandler<TestPayload>
    {
        public Task HandlePayloadAsync(TestPayload payload, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
