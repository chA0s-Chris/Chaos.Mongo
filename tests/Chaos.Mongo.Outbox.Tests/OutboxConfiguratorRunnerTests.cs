// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests;

using FluentAssertions;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Moq;
using NUnit.Framework;

public class OutboxConfiguratorRunnerTests
{
    [Test]
    public void Constructor_NullConfigurator_ThrowsArgumentNullException()
    {
        var mongoHelper = Mock.Of<IMongoHelper>();
        var logger = Mock.Of<ILogger<OutboxConfiguratorRunner>>();

        var act = () => new OutboxConfiguratorRunner(mongoHelper, null!, logger);

        act.Should().Throw<ArgumentNullException>().WithParameterName("configurator");
    }

    [Test]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var mongoHelper = Mock.Of<IMongoHelper>();
        var configurator = new OutboxConfigurator(new());

        var act = () => new OutboxConfiguratorRunner(mongoHelper, configurator, null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Test]
    public void Constructor_NullMongoHelper_ThrowsArgumentNullException()
    {
        var configurator = new OutboxConfigurator(new());
        var logger = Mock.Of<ILogger<OutboxConfiguratorRunner>>();

        var act = () => new OutboxConfiguratorRunner(null!, configurator, logger);

        act.Should().Throw<ArgumentNullException>().WithParameterName("mongoHelper");
    }

    [Test]
    public async Task RunAsync_DelegatesToConfigurator()
    {
        var options = new OutboxOptions
        {
            CollectionName = "TestOutbox"
        };
        var mockDatabase = new Mock<IMongoDatabase>();
        var mockIndexManager = new Mock<IMongoIndexManager<OutboxMessage>>();
        var mockCollection = new Mock<IMongoCollection<OutboxMessage>>();
        mockCollection.Setup(c => c.Indexes).Returns(mockIndexManager.Object);
        mockDatabase
            .Setup(d => d.GetCollection<OutboxMessage>(options.CollectionName, null))
            .Returns(mockCollection.Object);

        var mockHelper = new Mock<IMongoHelper>();
        mockHelper.Setup(h => h.Database).Returns(mockDatabase.Object);

        var configurator = new OutboxConfigurator(options);
        var logger = Mock.Of<ILogger<OutboxConfiguratorRunner>>();
        var runner = new OutboxConfiguratorRunner(mockHelper.Object, configurator, logger);

        await runner.RunAsync();

        mockDatabase.Verify(
            d => d.GetCollection<OutboxMessage>(options.CollectionName, null),
            Times.Once);
    }
}
