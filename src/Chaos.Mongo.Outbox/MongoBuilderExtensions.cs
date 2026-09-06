// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox;

using Chaos.Mongo.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Extension methods for <see cref="MongoBuilder"/> to register outbox services.
/// </summary>
public static class MongoBuilderExtensions
{
    /// <summary>
    /// Registers the default transactional outbox with the specified configuration.
    /// </summary>
    /// <param name="builder">The Mongo builder.</param>
    /// <param name="configure">Action to configure the outbox builder.</param>
    /// <returns>The Mongo builder for method chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">The configuration is invalid or conflicts with an existing outbox.</exception>
    public static MongoBuilder WithOutbox(this MongoBuilder builder, Action<OutboxBuilder> configure)
    {
        var registration = Register(builder, configure, null);
        builder.Services.AddSingleton(registration.Options);
        builder.Services.AddSingleton<OutboxConfigurator>();
        builder.Services.AddSingleton<IOutbox, MongoOutbox>();
        builder.Services.AddSingleton<IOutboxProcessor, OutboxProcessor>();
        return builder;
    }

    /// <summary>
    /// Registers an independent transactional outbox. Each outbox must use a distinct collection name.
    /// </summary>
    /// <typeparam name="TOutbox">The destination marker type, which is never instantiated.</typeparam>
    /// <param name="builder">The Mongo builder.</param>
    /// <param name="configure">Action configuring this destination's messages, publisher, policies, and lifecycle.</param>
    /// <returns>The Mongo builder for method chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The configuration is invalid, the marker is already registered, or the
    /// collection is shared.
    /// </exception>
    /// <remarks>
    /// Resolve IOutbox&lt;TOutbox&gt; and IOutboxProcessor&lt;TOutbox&gt; to use this destination.
    /// Publisher lifetimes are independent per registration; scoped and transient publishers resolve once per batch.
    /// Without automatic startup, initialize indexes using IOutboxConfiguratorRunner before starting processors manually.
    /// Writes require an active transaction on a session compatible with the configured MongoDB database.
    /// </remarks>
    public static MongoBuilder WithOutbox<TOutbox>(this MongoBuilder builder, Action<OutboxBuilder> configure)
    {
        var registration = Register(builder, configure, typeof(TOutbox));
        var services = builder.Services;
        services.AddKeyedSingleton<OutboxConfigurator>(registration, (_, _) => new OutboxConfigurator(registration.Options));
        services.AddKeyedSingleton<MongoOutbox>(registration, (sp, _) => new MongoOutbox(
                                                    sp.GetRequiredService<IMongoHelper>(), registration.Options, sp.GetRequiredService<TimeProvider>()));
        services.AddKeyedSingleton<OutboxProcessor>(registration, (sp, _) => new OutboxProcessor(
                                                        sp.GetRequiredService<IMongoHelper>(), registration.Options, sp.GetRequiredService<IServiceScopeFactory>(),
                                                        sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<OutboxProcessor>>(), registration));
        services.AddSingleton<IOutbox<TOutbox>>(sp => new TypedOutbox<TOutbox>(sp.GetRequiredKeyedService<MongoOutbox>(registration)));
        services.AddSingleton<IOutboxProcessor<TOutbox>>(sp => new TypedOutboxProcessor<TOutbox>(sp.GetRequiredKeyedService<OutboxProcessor>(registration)));
        return builder;
    }

    private static OutboxRegistration Register(MongoBuilder builder, Action<OutboxBuilder> configure, Type? markerType)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var identity = markerType?.Name ?? "Default";
        var registrations = builder.Services.Where(s => s.ServiceType == typeof(OutboxRegistration))
                                   .Select(s => s.ImplementationInstance).OfType<OutboxRegistration>().ToArray();
        if (registrations.Any(r => r.MarkerType == markerType))
        {
            throw new InvalidOperationException($"Outbox '{identity}' is already registered.");
        }

        var outboxBuilder = new OutboxBuilder();
        configure(outboxBuilder);
        outboxBuilder.Validate();
        var options = outboxBuilder.Build(identity);
        var conflict = registrations.FirstOrDefault(r => String.Equals(r.Options.CollectionName, options.CollectionName, StringComparison.Ordinal));
        if (conflict is not null)
        {
            throw new InvalidOperationException(
                $"Outbox '{identity}' and outbox '{conflict.Options.Identity}' both use collection '{options.CollectionName}'. Configure distinct collection names.");
        }

        var publisherType = outboxBuilder.PublisherType ?? throw new InvalidOperationException("An IOutboxPublisher implementation is required.");
        OutboxSerializationSetup.RegisterClassMaps(outboxBuilder.MessageTypes);
        var registration = new OutboxRegistration(markerType, options);
        builder.Services.AddSingleton(registration);
        builder.Services.Add(markerType is null
                                 ? new ServiceDescriptor(typeof(IOutboxPublisher), publisherType, outboxBuilder.PublisherLifetime)
                                 : new ServiceDescriptor(typeof(IOutboxPublisher), registration, publisherType, outboxBuilder.PublisherLifetime));
        builder.Services.AddTransient<IMongoConfigurator>(registration.GetConfigurator);
        builder.Services.TryAddTransient<IOutboxConfiguratorRunner>(sp => new OutboxConfiguratorRunner(
                                                                        sp.GetRequiredService<IMongoHelper>(),
                                                                        sp.GetServices<OutboxRegistration>().Select(r => r.GetConfigurator(sp)),
                                                                        sp.GetRequiredService<ILogger<OutboxConfiguratorRunner>>()));
        if (options.AutoStartProcessor)
        {
            builder.Services.AddHostedService(sp => new OutboxHostedService(
                                                  sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<ILogger<OutboxHostedService>>(),
                                                  sp.GetServices<OutboxRegistration>().Where(r => r.Options.AutoStartProcessor).ToArray(), sp));
        }

        return registration;
    }
}
