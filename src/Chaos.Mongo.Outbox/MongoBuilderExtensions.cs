// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox;

using Chaos.Mongo.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for <see cref="MongoBuilder"/> to register outbox services.
/// </summary>
public static class MongoBuilderExtensions
{
    /// <summary>
    /// Registers the transactional outbox with the specified configuration.
    /// </summary>
    /// <param name="builder">The Mongo builder.</param>
    /// <param name="configure">Action to configure the outbox builder.</param>
    /// <returns>The Mongo builder for method chaining.</returns>
    public static MongoBuilder WithOutbox(this MongoBuilder builder, Action<OutboxBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        if (builder.Services.Any(s => s.ServiceType == typeof(OutboxOptions)))
        {
            throw new InvalidOperationException("An outbox is already registered.");
        }

        var outboxBuilder = new OutboxBuilder();
        configure(outboxBuilder);
        outboxBuilder.Validate();

        // Register serialization (class maps for OutboxMessage and payload types)
        OutboxSerializationSetup.RegisterClassMaps(outboxBuilder.MessageTypes);

        // Build the frozen options
        var options = outboxBuilder.Build();

        // Register options as singleton
        builder.Services.AddSingleton(options);

        // Register the configurator (both as concrete type for OutboxConfiguratorRunner and as IMongoConfigurator)
        builder.Services.AddSingleton(sp => new OutboxConfigurator(sp.GetRequiredService<OutboxOptions>()));
        builder.Services.AddTransient<IMongoConfigurator>(sp => sp.GetRequiredService<OutboxConfigurator>());

        // Register the outbox configurator runner
        builder.Services.AddTransient<IOutboxConfiguratorRunner, OutboxConfiguratorRunner>();

        // Register IOutbox as singleton
        builder.Services.AddSingleton<IOutbox, MongoOutbox>();

        // Register the user's IOutboxPublisher implementation
        var publisherDescriptor = new ServiceDescriptor(
            typeof(IOutboxPublisher),
            outboxBuilder.PublisherType!,
            outboxBuilder.PublisherLifetime);
        builder.Services.Add(publisherDescriptor);

        // Register IOutboxProcessor as singleton
        builder.Services.AddSingleton<IOutboxProcessor, OutboxProcessor>();

        // Register OutboxHostedService only if auto-start is enabled
        if (options.AutoStartProcessor)
        {
            builder.Services.AddHostedService<OutboxHostedService>();
        }

        return builder;
    }
}
