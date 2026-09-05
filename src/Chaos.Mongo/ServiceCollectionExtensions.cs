// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo;

using Chaos.Mongo.Configuration;
using Chaos.Mongo.Migrations;
using Chaos.Mongo.Queues;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

/// <summary>
/// Extension methods for configuring MongoDB services in an <see cref="IServiceCollection"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds MongoDB services to the service collection with optional configuration.
    /// </summary>
    /// <param name="services">The service collection to add MongoDB services to.</param>
    /// <param name="configure">Optional action to configure <see cref="MongoOptions"/>.</param>
    /// <returns>A <see cref="MongoBuilder"/> for configuring MongoDB services.</returns>
    public static MongoBuilder AddMongo(this IServiceCollection services, Action<MongoOptions>? configure = null)
    {
        var builder = services.AddOptions<MongoOptions>();

        if (configure is not null)
        {
            services.Configure(configure);
        }

        return services.AddMongoInternal(builder);
    }

    /// <summary>
    /// Adds MongoDB services to the service collection using configuration from an <see cref="IConfiguration"/> section.
    /// </summary>
    /// <param name="services">The service collection to add MongoDB services to.</param>
    /// <param name="configuration">The configuration instance containing MongoDB settings.</param>
    /// <param name="sectionName">The name of the configuration section containing MongoDB options.</param>
    /// <returns>A <see cref="MongoBuilder"/> for configuring MongoDB services.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configuration"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="sectionName"/> is null or whitespace.</exception>
    public static MongoBuilder AddMongo(this IServiceCollection services, IConfiguration configuration, String sectionName)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);

        var builder = services.AddOptions<MongoOptions>();
        builder.Bind(configuration.GetSection(sectionName));

        return services.AddMongoInternal(builder);
    }


    /// <summary>
    /// Adds MongoDB services to the service collection using a connection string.
    /// </summary>
    /// <param name="services">The service collection to add MongoDB services to.</param>
    /// <param name="connectionString">The MongoDB connection string.</param>
    /// <param name="databaseName">
    /// Optional database name to use. If not specified, the database name from the connection
    /// string is used.
    /// </param>
    /// <param name="configure">Optional action to configure additional <see cref="MongoOptions"/>.</param>
    /// <returns>A <see cref="MongoBuilder"/> for configuring MongoDB services.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="connectionString"/> is null or whitespace.</exception>
    public static MongoBuilder AddMongo(this IServiceCollection services, String connectionString, String? databaseName = null, Action<MongoOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return services.AddMongo(options =>
        {
            options.Url = new MongoUrl(connectionString);
            options.DefaultDatabase = databaseName;
            configure?.Invoke(options);
        });
    }

    /// <summary>
    /// Adds MongoDB services to the service collection using a MongoDB URL.
    /// </summary>
    /// <param name="services">The service collection to add MongoDB services to.</param>
    /// <param name="url">The MongoDB URL.</param>
    /// <param name="databaseName">Optional database name to use. If not specified, the database name from the URL is used.</param>
    /// <param name="configure">Optional action to configure additional <see cref="MongoOptions"/>.</param>
    /// <returns>A <see cref="MongoBuilder"/> for configuring MongoDB services.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="url"/> is null.</exception>
    public static MongoBuilder AddMongo(this IServiceCollection services, MongoUrl url, String? databaseName = null, Action<MongoOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(url);

        return services.AddMongo(options =>
        {
            options.Url = url;
            options.DefaultDatabase = databaseName;
            configure?.Invoke(options);
        });
    }

    internal static IServiceCollection AddMongoQueue(this IServiceCollection services)
    {
        services.TryAddSingleton<IMongoQueueCollectionNameGenerator, MongoQueueCollectionNameGenerator>();
        services.TryAddSingleton<IMongoQueuePayloadHandlerFactory, MongoQueuePayloadHandlerFactory>();
        services.TryAddSingleton<IMongoQueuePayloadPrioritizer, MongoQueuePayloadPrioritizer>();
        services.TryAddSingleton<IMongoQueueSubscriptionFactory, MongoQueueSubscriptionFactory>();
        services.TryAddSingleton<IMongoQueuePublisher, MongoQueuePublisher>();
        return services;
    }

    private static MongoBuilder AddMongoInternal(this IServiceCollection services, OptionsBuilder<MongoOptions> builder)
    {
        services.AddSingleton<IValidateOptions<MongoOptions>, MongoOptionsValidation>();
        builder.ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IMongoClientSettingsFactory, MongoClientSettingsFactory>();
        services.TryAddSingleton<IMongoConnectionFactory, MongoConnectionFactory>();
        services.AddSingleton<IMongoConnection>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<MongoOptions>>().Value;
            var url = options.Url ?? throw new InvalidOperationException("MongoOptions.Url is not configured");

            var factory = serviceProvider.GetRequiredService<IMongoConnectionFactory>();
            return factory.CreateConnection(url, options.DefaultDatabase);
        });

        services.AddSingleton<ICollectionTypeMap, CollectionTypeMap>();
        services.AddSingleton<IMongoHelper, MongoHelper>();

        services.AddTransient<IMongoConfiguratorRunner, MongoConfiguratorRunner>();
        services.AddTransient<IMongoMigrationRunner, MongoMigrationRunner>();

        services.AddHostedService<MongoHostedService>(); // only registered once even if called for multiple queues

        return new MongoBuilder(services);
    }
}
