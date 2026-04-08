// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests;

using FluentAssertions;
using MongoDB.Driver;
using Moq;
using NUnit.Framework;

public class OutboxConfiguratorTests
{
    [Test]
    public async Task ConfigureAsync_WithoutRetentionPeriod_CreatesPollingIndexAndDropsManagedTtlIndexes()
    {
        var options = new OutboxOptions
        {
            CollectionName = "TestOutbox"
        };
        var sut = new OutboxConfigurator(options);

        var indexManagerMock = new Mock<IMongoIndexManager<OutboxMessage>>();
        var collectionMock = new Mock<IMongoCollection<OutboxMessage>>();
        collectionMock.Setup(c => c.Indexes).Returns(indexManagerMock.Object);

        var databaseMock = new Mock<IMongoDatabase>();
        databaseMock
            .Setup(d => d.GetCollection<OutboxMessage>(options.CollectionName, null))
            .Returns(collectionMock.Object);

        var mongoHelperMock = new Mock<IMongoHelper>();
        mongoHelperMock.Setup(h => h.Database).Returns(databaseMock.Object);

        await sut.ConfigureAsync(mongoHelperMock.Object);

        indexManagerMock.Verify(
            m => m.CreateOneAsync(
                It.IsAny<CreateIndexModel<OutboxMessage>>(),
                It.IsAny<CreateOneIndexOptions>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        indexManagerMock.Verify(
            m => m.DropOneAsync("IX_Outbox_ProcessedUtc_TTL", It.IsAny<CancellationToken>()),
            Times.Once);

        indexManagerMock.Verify(
            m => m.DropOneAsync("IX_Outbox_FailedUtc_TTL", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task ConfigureAsync_WithRetentionPeriod_CreatesPollingAndTtlIndexes()
    {
        var options = new OutboxOptions
        {
            CollectionName = "TestOutbox",
            RetentionPeriod = TimeSpan.FromDays(7)
        };
        var sut = new OutboxConfigurator(options);

        var indexManagerMock = new Mock<IMongoIndexManager<OutboxMessage>>();
        var collectionMock = new Mock<IMongoCollection<OutboxMessage>>();
        collectionMock.Setup(c => c.Indexes).Returns(indexManagerMock.Object);

        var databaseMock = new Mock<IMongoDatabase>();
        databaseMock
            .Setup(d => d.GetCollection<OutboxMessage>(options.CollectionName, null))
            .Returns(collectionMock.Object);

        var mongoHelperMock = new Mock<IMongoHelper>();
        mongoHelperMock.Setup(h => h.Database).Returns(databaseMock.Object);

        await sut.ConfigureAsync(mongoHelperMock.Object);

        // 1 polling index + 2 TTL indexes (ProcessedUtc + FailedUtc)
        indexManagerMock.Verify(
            m => m.CreateOneAsync(
                It.IsAny<CreateIndexModel<OutboxMessage>>(),
                It.IsAny<CreateOneIndexOptions>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Test]
    public void Constructor_NullOptions_ThrowsArgumentNullException()
    {
        var act = () => new OutboxConfigurator(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("options");
    }
}
