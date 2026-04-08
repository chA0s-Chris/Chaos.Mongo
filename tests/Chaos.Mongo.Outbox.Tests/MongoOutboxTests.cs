// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests;

using FluentAssertions;
using MongoDB.Driver;
using Moq;
using NUnit.Framework;

public class MongoOutboxTests
{
    [Test]
    public async Task AddMessageAsync_NullPayload_ThrowsArgumentNullException()
    {
        var mongoHelper = Mock.Of<IMongoHelper>();
        var options = new OutboxOptions();
        var sut = new MongoOutbox(mongoHelper, options, TimeProvider.System);

        var session = Mock.Of<IClientSessionHandle>();

        var act = () => sut.AddMessageAsync<TestPayload>(session, null!);

        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("payload");
    }

    [Test]
    public void Constructor_NullMongoHelper_ThrowsArgumentNullException()
    {
        var options = new OutboxOptions();
        var timeProvider = TimeProvider.System;

        var act = () => new MongoOutbox(null!, options, timeProvider);

        act.Should().Throw<ArgumentNullException>().WithParameterName("mongoHelper");
    }

    [Test]
    public void Constructor_NullOptions_ThrowsArgumentNullException()
    {
        var mongoHelper = Mock.Of<IMongoHelper>();
        var timeProvider = TimeProvider.System;

        var act = () => new MongoOutbox(mongoHelper, null!, timeProvider);

        act.Should().Throw<ArgumentNullException>().WithParameterName("options");
    }

    [Test]
    public void Constructor_NullTimeProvider_ThrowsArgumentNullException()
    {
        var mongoHelper = Mock.Of<IMongoHelper>();
        var options = new OutboxOptions();

        var act = () => new MongoOutbox(mongoHelper, options, null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("timeProvider");
    }
}
