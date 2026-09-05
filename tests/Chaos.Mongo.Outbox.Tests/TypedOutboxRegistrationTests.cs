// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests;

using Chaos.Mongo.Configuration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using NUnit.Framework;
using System.Reflection;

public class TypedOutboxRegistrationTests
{
    [TestCase(true)]
    [TestCase(false)]
    public void WithOutbox_DefaultAndTypedInEitherOrder_ResolveIndependentSingletons(Boolean defaultFirst)
    {
        var services = CreateServices();
        var builder = new MongoBuilder(services);
        if (defaultFirst)
            builder.WithOutbox(o => Configure(o));
        builder.WithOutbox<FirstDestination>(o => Configure(o).WithCollectionName("First"))
               .WithOutbox<SecondDestination>(o => Configure(o).WithCollectionName("Second"));
        if (!defaultFirst)
            builder.WithOutbox(o => Configure(o));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true
        });

        var first = provider.GetRequiredService<IOutbox<FirstDestination>>();
        var second = provider.GetRequiredService<IOutbox<SecondDestination>>();
        var original = provider.GetRequiredService<IOutbox>();
        first.Should().NotBeSameAs(second).And.NotBeSameAs(original);
        first.Should().BeSameAs(provider.GetRequiredService<IOutbox<FirstDestination>>());
        provider.GetRequiredService<IOutboxProcessor<FirstDestination>>().Should()
                .NotBeSameAs(provider.GetRequiredService<IOutboxProcessor<SecondDestination>>())
                .And.NotBeSameAs(provider.GetRequiredService<IOutboxProcessor>());
        provider.GetServices<IMongoConfigurator>().Should().HaveCount(3);
        provider.GetRequiredService<OutboxOptions>().CollectionName.Should().Be("Outbox");
    }

    [TestCase(true)]
    [TestCase(false)]
    public void WithOutbox_DefaultCollectionConflict_RejectsBeforeMutation(Boolean defaultFirst)
    {
        var services = CreateServices();
        var builder = new MongoBuilder(services);
        if (defaultFirst)
            builder.WithOutbox(o => Configure(o));
        else
            builder.WithOutbox<FirstDestination>(o => Configure(o));
        var count = services.Count;
        Action act = defaultFirst
            ? () => builder.WithOutbox<FirstDestination>(o => Configure(o))
            : () => builder.WithOutbox(o => Configure(o));
        act.Should().Throw<InvalidOperationException>().WithMessage("*Default*Outbox*")
           .Which.Message.Should().Contain("FirstDestination");
        services.Should().HaveCount(count);
    }

    [Test]
    public void WithOutbox_DuplicateMarkerAcrossBuilders_RejectsBeforeMutation()
    {
        var services = CreateServices();
        new MongoBuilder(services).WithOutbox<FirstDestination>(o => Configure(o));
        var count = services.Count;
        var act = () => new MongoBuilder(services).WithOutbox<FirstDestination>(o => o.WithPublisher<TestOutboxPublisher>()
                                                                                      .WithMessage<RejectedDuplicatePayload>().WithCollectionName("Different"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*FirstDestination*already registered*");
        services.Should().HaveCount(count);
        BsonClassMap.IsClassMapRegistered(typeof(RejectedDuplicatePayload)).Should().BeFalse();
    }

    [Test]
    public void WithOutbox_MultipleAutomaticRegistrations_RegistersOneSharedHostedService()
    {
        var services = CreateServices();
        new MongoBuilder(services)
            .WithOutbox<FirstDestination>(o => Configure(o).WithCollectionName("First").WithAutoStartProcessor())
            .WithOutbox<SecondDestination>(o => Configure(o).WithCollectionName("Second").WithAutoStartProcessor())
            .WithOutbox(o => Configure(o).WithAutoStartProcessor());
        using var provider = services.BuildServiceProvider();
        provider.GetServices<IHostedService>().Should().ContainSingle().Which.Should().BeOfType<OutboxHostedService>();
    }

    [Test]
    public void WithOutbox_NullTypedArguments_RejectsArguments()
    {
        var builder = new MongoBuilder(new ServiceCollection());
        var nullBuilder = () => MongoBuilderExtensions.WithOutbox<FirstDestination>(null!, o => Configure(o));
        var nullConfigure = () => builder.WithOutbox<FirstDestination>(null!);
        nullBuilder.Should().Throw<ArgumentNullException>().WithParameterName("builder");
        nullConfigure.Should().Throw<ArgumentNullException>().WithParameterName("configure");
    }

    [TestCase(ServiceLifetime.Transient)]
    [TestCase(ServiceLifetime.Scoped)]
    [TestCase(ServiceLifetime.Singleton)]
    public void WithOutbox_SharedPublisherType_HonorsLifetimeAndDestination(ServiceLifetime lifetime)
    {
        var services = CreateServices();
        new MongoBuilder(services)
            .WithOutbox<FirstDestination>(o => Configure(o).WithCollectionName("First").WithPublisher<TestOutboxPublisher>(lifetime))
            .WithOutbox<SecondDestination>(o => Configure(o).WithCollectionName("Second").WithPublisher<TestOutboxPublisher>(ServiceLifetime.Singleton))
            .WithOutbox(o => Configure(o).WithPublisher<TestOutboxPublisher>(ServiceLifetime.Singleton));
        var keys = services.Where(d => d.IsKeyedService && d.ServiceType == typeof(IOutboxPublisher)).Select(d => d.ServiceKey).ToArray();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true
        });
        using var scope = provider.CreateScope();
        using var otherScope = provider.CreateScope();
        var first = scope.ServiceProvider.GetRequiredKeyedService<IOutboxPublisher>(keys[0]);
        var again = scope.ServiceProvider.GetRequiredKeyedService<IOutboxPublisher>(keys[0]);
        var another = otherScope.ServiceProvider.GetRequiredKeyedService<IOutboxPublisher>(keys[0]);
        ReferenceEquals(first, again).Should().Be(lifetime != ServiceLifetime.Transient);
        ReferenceEquals(first, another).Should().Be(lifetime == ServiceLifetime.Singleton);
        first.Should().NotBeSameAs(provider.GetRequiredKeyedService<IOutboxPublisher>(keys[1]))
             .And.NotBeSameAs(provider.GetRequiredService<IOutboxPublisher>());
    }

    [Test]
    public void WithOutbox_TypedCollectionConflict_RejectsBeforeSerialization()
    {
        var services = CreateServices();
        var builder = new MongoBuilder(services).WithOutbox<FirstDestination>(o => Configure(o).WithCollectionName("Shared"));
        var count = services.Count;
        var act = () => builder.WithOutbox<SecondDestination>(o => o.WithPublisher<TestOutboxPublisher>()
                                                                    .WithMessage<RejectedConflictPayload>().WithCollectionName("Shared"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*SecondDestination*FirstDestination*Shared*");
        services.Should().HaveCount(count);
        BsonClassMap.IsClassMapRegistered(typeof(RejectedConflictPayload)).Should().BeFalse();
        builder.WithOutbox<SecondDestination>(o => Configure(o).WithCollectionName("shared"));
    }

    [Test]
    public void WithOutbox_TypedOnly_DoesNotPopulateDefaultServices()
    {
        var services = CreateServices();
        new MongoBuilder(services).WithOutbox<FirstDestination>(o => Configure(o));
        using var provider = services.BuildServiceProvider();
        provider.GetService<IOutbox>().Should().BeNull();
        provider.GetService<IOutboxProcessor>().Should().BeNull();
        provider.GetService<IOutboxPublisher>().Should().BeNull();
        provider.GetService<OutboxOptions>().Should().BeNull();
        provider.GetService<OutboxConfigurator>().Should().BeNull();
        provider.GetRequiredService<IOutbox<FirstDestination>>().Should().NotBeNull();
    }

    private static OutboxBuilder Configure(OutboxBuilder builder)
        => builder.WithPublisher<TestOutboxPublisher>().WithMessage<TestPayload>();

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IMongoHelper>(new ProcessingFilterMongoHelper(DispatchProxy.Create<IMongoDatabase, LifecycleDatabase>()));
        return services;
    }
}

internal sealed class FirstDestination
{
    private FirstDestination() => throw new InvalidOperationException("Marker types must not be instantiated.");
}

internal sealed class SecondDestination
{
    private SecondDestination() => throw new InvalidOperationException("Marker types must not be instantiated.");
}

internal sealed class RejectedDuplicatePayload;

internal sealed class RejectedConflictPayload;
