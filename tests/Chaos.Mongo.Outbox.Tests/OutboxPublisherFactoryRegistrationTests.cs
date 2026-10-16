// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

public class OutboxPublisherFactoryRegistrationTests
{
    [TestCase(ServiceLifetime.Transient)]
    [TestCase(ServiceLifetime.Scoped)]
    [TestCase(ServiceLifetime.Singleton)]
    public void WithOutbox_DefaultPublisherFactory_HonorsLifetime(ServiceLifetime lifetime)
    {
        var services = new ServiceCollection();
        var invocations = 0;
        new MongoBuilder(services).WithOutbox(o => o.WithMessage<TestPayload>().WithPublisher(_ =>
        {
            invocations++;
            return new TestOutboxPublisher();
        }, lifetime));

        AssertLifetime(services, null, lifetime, () => invocations);
    }

    [Test]
    public void WithOutbox_DefaultPublisherFactory_UsesTransientLifetimeByDefault()
    {
        var services = new ServiceCollection();
        new MongoBuilder(services).WithOutbox(o => o.WithMessage<TestPayload>().WithPublisher(_ => new TestOutboxPublisher()));

        var descriptor = services.Single(d => d.ServiceType == typeof(IOutboxPublisher));
        descriptor.IsKeyedService.Should().BeFalse();
        descriptor.Lifetime.Should().Be(ServiceLifetime.Transient);
        descriptor.ImplementationFactory.Should().NotBeNull();
    }

    [TestCase(ServiceLifetime.Transient)]
    [TestCase(ServiceLifetime.Scoped)]
    public void WithOutbox_FactoryResolvingScopedDependency_ReceivesScopeProvider(ServiceLifetime lifetime)
    {
        var services = new ServiceCollection();
        services.AddScoped<ScopedPublisherDependency>();
        new MongoBuilder(services)
            .WithOutbox(o => o.WithMessage<TestPayload>()
                              .WithPublisher(sp => new DependentOutboxPublisher(sp.GetRequiredService<ScopedPublisherDependency>()), lifetime))
            .WithOutbox<FirstDestination>(o => o.WithMessage<TestPayload>().WithCollectionName("First")
                                                .WithPublisher(sp => new DependentOutboxPublisher(sp.GetRequiredService<ScopedPublisherDependency>()),
                                                               lifetime));
        var key = services.Single(d => d.IsKeyedService && d.ServiceType == typeof(IOutboxPublisher)).ServiceKey;
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true
        });
        using var scope = provider.CreateScope();
        var dependency = scope.ServiceProvider.GetRequiredService<ScopedPublisherDependency>();

        var defaultPublisher = scope.ServiceProvider.GetRequiredService<IOutboxPublisher>();
        var typedPublisher = scope.ServiceProvider.GetRequiredKeyedService<IOutboxPublisher>(key);

        defaultPublisher.Should().BeOfType<DependentOutboxPublisher>().Which.Dependency.Should().BeSameAs(dependency);
        typedPublisher.Should().BeOfType<DependentOutboxPublisher>().Which.Dependency.Should().BeSameAs(dependency);
    }

    [Test]
    public void WithOutbox_FactoryReturnsNull_ThrowsWithOutboxIdentity()
    {
        var services = new ServiceCollection();
        new MongoBuilder(services)
            .WithOutbox(o => o.WithMessage<TestPayload>().WithPublisher(_ => null!))
            .WithOutbox<FirstDestination>(o => o.WithMessage<TestPayload>().WithCollectionName("First").WithPublisher(_ => null!));
        var key = services.Single(d => d.IsKeyedService && d.ServiceType == typeof(IOutboxPublisher)).ServiceKey;
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.Invoking(sp => sp.GetRequiredService<IOutboxPublisher>())
             .Should().Throw<InvalidOperationException>().WithMessage("*factory*'Default'*null*");
        scope.ServiceProvider.Invoking(sp => sp.GetRequiredKeyedService<IOutboxPublisher>(key))
             .Should().Throw<InvalidOperationException>().WithMessage($"*factory*'{nameof(FirstDestination)}'*null*");
    }

    [Test]
    public void WithOutbox_FactoryThenType_RegistersType()
    {
        var services = new ServiceCollection();
        new MongoBuilder(services).WithOutbox(o => o.WithMessage<TestPayload>()
                                                    .WithPublisher(_ => new TestOutboxPublisher(), ServiceLifetime.Singleton)
                                                    .WithPublisher<TestOutboxPublisher>(ServiceLifetime.Scoped));

        var descriptor = services.Single(d => d.ServiceType == typeof(IOutboxPublisher));
        descriptor.ImplementationType.Should().Be(typeof(TestOutboxPublisher));
        descriptor.ImplementationFactory.Should().BeNull();
        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Test]
    public void WithOutbox_TypeThenFactory_RegistersFactory()
    {
        var services = new ServiceCollection();
        var publisher = new TestOutboxPublisher();
        new MongoBuilder(services).WithOutbox<FirstDestination>(o => o.WithMessage<TestPayload>()
                                                                      .WithPublisher<TestOutboxPublisher>(ServiceLifetime.Scoped)
                                                                      .WithPublisher(_ => publisher, ServiceLifetime.Singleton));

        var descriptor = services.Single(d => d.ServiceType == typeof(IOutboxPublisher));
        descriptor.IsKeyedService.Should().BeTrue();
        descriptor.KeyedImplementationType.Should().BeNull();
        descriptor.KeyedImplementationFactory.Should().NotBeNull();
        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredKeyedService<IOutboxPublisher>(descriptor.ServiceKey).Should().BeSameAs(publisher);
    }

    [TestCase(ServiceLifetime.Transient)]
    [TestCase(ServiceLifetime.Scoped)]
    [TestCase(ServiceLifetime.Singleton)]
    public void WithOutbox_TypedPublisherFactory_HonorsLifetime(ServiceLifetime lifetime)
    {
        var services = new ServiceCollection();
        var invocations = 0;
        new MongoBuilder(services).WithOutbox<FirstDestination>(o => o.WithMessage<TestPayload>().WithPublisher(_ =>
        {
            invocations++;
            return new TestOutboxPublisher();
        }, lifetime));
        var key = services.Single(d => d.IsKeyedService && d.ServiceType == typeof(IOutboxPublisher)).ServiceKey;

        services.Should().NotContain(d => !d.IsKeyedService && d.ServiceType == typeof(IOutboxPublisher));
        AssertLifetime(services, key, lifetime, () => invocations);
    }

    private static void AssertLifetime(ServiceCollection services, Object? key, ServiceLifetime lifetime, Func<Int32> invocations)
    {
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true
        });
        using var scope = provider.CreateScope();
        using var otherScope = provider.CreateScope();

        var first = Resolve(scope.ServiceProvider, key);
        var again = Resolve(scope.ServiceProvider, key);
        var another = Resolve(otherScope.ServiceProvider, key);

        first.Should().BeOfType<TestOutboxPublisher>();
        ReferenceEquals(first, again).Should().Be(lifetime != ServiceLifetime.Transient);
        ReferenceEquals(first, another).Should().Be(lifetime == ServiceLifetime.Singleton);
        invocations.Invoke().Should().Be(lifetime switch
        {
            ServiceLifetime.Transient => 3,
            ServiceLifetime.Scoped => 2,
            _ => 1
        });
    }

    private static IOutboxPublisher Resolve(IServiceProvider serviceProvider, Object? key)
        => key is null
            ? serviceProvider.GetRequiredService<IOutboxPublisher>()
            : serviceProvider.GetRequiredKeyedService<IOutboxPublisher>(key);
}

internal sealed class ScopedPublisherDependency;

internal sealed class DependentOutboxPublisher : IOutboxPublisher
{
    public DependentOutboxPublisher(ScopedPublisherDependency dependency)
    {
        Dependency = dependency;
    }

    public ScopedPublisherDependency Dependency { get; }

    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
