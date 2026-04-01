// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

public class OutboxHostedServiceTests
{
    [Test]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var processor = Mock.Of<IOutboxProcessor>();
        var scopeFactory = Mock.Of<IServiceScopeFactory>();

        var act = () => new OutboxHostedService(processor, scopeFactory, null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Test]
    public void Constructor_NullOutboxProcessor_ThrowsArgumentNullException()
    {
        var scopeFactory = Mock.Of<IServiceScopeFactory>();
        var logger = Mock.Of<ILogger<OutboxHostedService>>();

        var act = () => new OutboxHostedService(null!, scopeFactory, logger);

        act.Should().Throw<ArgumentNullException>().WithParameterName("outboxProcessor");
    }

    [Test]
    public void Constructor_NullServiceScopeFactory_ThrowsArgumentNullException()
    {
        var processor = Mock.Of<IOutboxProcessor>();
        var logger = Mock.Of<ILogger<OutboxHostedService>>();

        var act = () => new OutboxHostedService(processor, null!, logger);

        act.Should().Throw<ArgumentNullException>().WithParameterName("serviceScopeFactory");
    }

    [Test]
    public async Task FullLifecycle_RunsInCorrectOrder()
    {
        var callOrder = new List<String>();
        var mockRunner = new Mock<IOutboxConfiguratorRunner>();
        mockRunner
            .Setup(r => r.RunAsync(It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("configurator"))
            .Returns(Task.CompletedTask);

        var sut = CreateService(out var processor, out _, mockRunner.Object);
        processor
            .Setup(p => p.StartAsync(It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("start"))
            .Returns(Task.CompletedTask);
        processor
            .Setup(p => p.StopAsync(It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("stop"))
            .Returns(Task.CompletedTask);

        await sut.StartingAsync(CancellationToken.None);
        await sut.StartAsync(CancellationToken.None);
        await sut.StartedAsync(CancellationToken.None);
        await sut.StoppingAsync(CancellationToken.None);
        await sut.StopAsync(CancellationToken.None);
        await sut.StoppedAsync(CancellationToken.None);

        callOrder.Should().Equal("configurator", "start", "stop");
    }

    [Test]
    public async Task StartAsync_IsNoOp()
    {
        var sut = CreateService(out _, out _);

        await sut.StartAsync(CancellationToken.None);
    }

    [Test]
    public async Task StartedAsync_PropagatesCancellationToken()
    {
        var sut = CreateService(out var processor, out _);
        using var cts = new CancellationTokenSource();

        await sut.StartedAsync(cts.Token);

        processor.Verify(p => p.StartAsync(cts.Token), Times.Once);
    }

    [Test]
    public async Task StartedAsync_StartsProcessor()
    {
        var sut = CreateService(out var processor, out _);

        await sut.StartedAsync(CancellationToken.None);

        processor.Verify(p => p.StartAsync(CancellationToken.None), Times.Once);
    }

    [Test]
    public async Task StartingAsync_PropagatesCancellationToken()
    {
        var mockRunner = new Mock<IOutboxConfiguratorRunner>();
        var sut = CreateService(out _, out _, mockRunner.Object);
        using var cts = new CancellationTokenSource();

        await sut.StartingAsync(cts.Token);

        mockRunner.Verify(r => r.RunAsync(cts.Token), Times.Once);
    }

    [Test]
    public async Task StartingAsync_RunsConfigurators()
    {
        var mockRunner = new Mock<IOutboxConfiguratorRunner>();
        var sut = CreateService(out _, out _, mockRunner.Object);

        await sut.StartingAsync(CancellationToken.None);

        mockRunner.Verify(r => r.RunAsync(CancellationToken.None), Times.Once);
    }

    [Test]
    public async Task StopAsync_IsNoOp()
    {
        var sut = CreateService(out _, out _);

        await sut.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task StoppedAsync_IsNoOp()
    {
        var sut = CreateService(out _, out _);

        await sut.StoppedAsync(CancellationToken.None);
    }

    [Test]
    public async Task StoppingAsync_PropagatesCancellationToken()
    {
        var sut = CreateService(out var processor, out _);
        using var cts = new CancellationTokenSource();

        await sut.StoppingAsync(cts.Token);

        processor.Verify(p => p.StopAsync(cts.Token), Times.Once);
    }

    [Test]
    public async Task StoppingAsync_StopsProcessor()
    {
        var sut = CreateService(out var processor, out _);

        await sut.StoppingAsync(CancellationToken.None);

        processor.Verify(p => p.StopAsync(CancellationToken.None), Times.Once);
    }

    private static OutboxHostedService CreateService(
        out Mock<IOutboxProcessor> processor,
        out Mock<IServiceScopeFactory> scopeFactory,
        IOutboxConfiguratorRunner? configuratorRunner = null)
    {
        processor = new();
        scopeFactory = new();

        var mockScope = new Mock<IServiceScope>();
        var scopeServices = new ServiceCollection();

        scopeServices.AddSingleton(configuratorRunner ?? Mock.Of<IOutboxConfiguratorRunner>());

        mockScope.Setup(s => s.ServiceProvider).Returns(scopeServices.BuildServiceProvider());
        scopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);

        var logger = Mock.Of<ILogger<OutboxHostedService>>();
        return new(processor.Object, scopeFactory.Object, logger);
    }
}
