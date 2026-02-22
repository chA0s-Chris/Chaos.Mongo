// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

using Chaos.Mongo.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for <see cref="MongoBuilder"/> to register event store services.
/// </summary>
public static class MongoBuilderExtensions
{
    /// <summary>
    /// Registers an event store for the specified aggregate type.
    /// </summary>
    /// <typeparam name="TAggregate">The aggregate type.</typeparam>
    /// <param name="builder">The Mongo builder.</param>
    /// <param name="configure">Action to configure the event store builder.</param>
    /// <returns>The Mongo builder for method chaining.</returns>
    public static MongoBuilder WithEventStore<TAggregate>(this MongoBuilder builder,
                                                          Action<MongoEventStoreBuilder<TAggregate>> configure)
        where TAggregate : class, IAggregate, new()
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        if (builder.Services.Any(s => s.ServiceType == typeof(IEventStore<TAggregate>)))
        {
            throw new InvalidOperationException($"An event store for aggregate type {typeof(TAggregate).Name} is already registered.");
        }

        var esBuilder = new MongoEventStoreBuilder<TAggregate>();
        configure(esBuilder);

        var options = esBuilder.Options;

        // Register serialization (discriminators, GuidSerializer)
        MongoEventStoreSerializationSetup.EnsureGuidSerializer();
        MongoEventStoreSerializationSetup.RegisterClassMaps(options);

        // Register options as singleton
        builder.Services.AddSingleton(options);

        // Register the configurator for index creation
        builder.Services.AddTransient<IMongoConfigurator>(sp =>
                                                              new MongoEventStoreConfigurator<TAggregate>(
                                                                  sp.GetRequiredService<MongoEventStoreOptions<TAggregate>>()));

        // Register IEventStore<TAggregate>
        builder.Services.AddScoped<IEventStore<TAggregate>, MongoEventStore<TAggregate>>();

        // Register IAggregateRepository<TAggregate>
        builder.Services.AddScoped<IAggregateRepository<TAggregate>, MongoAggregateRepository<TAggregate>>();

        return builder;
    }
}
