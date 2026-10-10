// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

using Chaos.Mongo.Configuration;
using Chaos.Mongo.EventStore.Integrity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
        ValidateIntegrityOptions(options);

        if (options.MaxAppendRetries < 0)
        {
            throw new InvalidOperationException(
                $"The maximum number of append retries for aggregate type {typeof(TAggregate).Name} must not be negative.");
        }

        if (options.EventTypes.Count == 0)
        {
            throw new InvalidOperationException(
                $"At least one event type must be registered for aggregate type {typeof(TAggregate).Name}. " +
                "Use WithEvent<TEvent>() to register event types.");
        }

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

        if (options.IntegrityProtectionEnabled)
        {
            options.Anchoring = GetOrAddSingleton<IntegrityAnchoringOptions>(builder.Services);
            GetOrAddSingleton<ProtectedStreamRegistry>(builder.Services).Add(new ProtectedStreamType<TAggregate>(options));

            // Retroactively seal events stored without integrity data
            builder.Services.AddSingleton(sp => new EventStreamSealingSweep<TAggregate>(
                                              sp.GetRequiredService<IMongoHelper>(),
                                              options,
                                              sp.GetService<TimeProvider>() ?? TimeProvider.System,
                                              sp.GetService<ILogger<EventStreamSealingSweep<TAggregate>>>() ??
                                              NullLogger<EventStreamSealingSweep<TAggregate>>.Instance));
            builder.Services.AddHostedService(sp => new EventStreamSealingHostedService<TAggregate>(
                                                  sp.GetRequiredService<EventStreamSealingSweep<TAggregate>>()));
        }

        return builder;
    }

    /// <summary>
    /// Selects the module-level source of anchor candidates for all protected aggregate types.
    /// </summary>
    /// <param name="builder">The Mongo builder.</param>
    /// <param name="candidateSource">The candidate source.</param>
    /// <returns>The Mongo builder for method chaining.</returns>
    internal static MongoBuilder WithAnchorCandidateSource(this MongoBuilder builder, AnchorCandidateSourceKind candidateSource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        GetOrAddSingleton<IntegrityAnchoringOptions>(builder.Services).CandidateSource = candidateSource;
        AddAnchorCandidateSource(builder.Services);
        return builder;
    }

    /// <summary>
    /// Registers the candidate source once. It reads the selected kind and the registry when it is resolved,
    /// so the event stores may be registered before or after the selection.
    /// </summary>
    private static void AddAnchorCandidateSource(IServiceCollection services)
    {
        if (services.Any(s => s.ServiceType == typeof(IAnchorCandidateSource)))
        {
            return;
        }

        services.AddSingleton<IAnchorCandidateSource>(sp =>
        {
            var anchoring = sp.GetRequiredService<IntegrityAnchoringOptions>();
            return anchoring.CandidateSource switch
            {
                AnchorCandidateSourceKind.PendingCollection =>
                    new PendingAnchorCandidateSource(sp.GetRequiredService<IMongoHelper>(), anchoring),
                AnchorCandidateSourceKind.ChangeStream =>
                    new ChangeStreamAnchorCandidateSource(sp.GetRequiredService<IMongoHelper>(),
                                                          anchoring,
                                                          GetOrCreateRegistry(sp)),
                _ => throw new InvalidOperationException("No anchor candidate source is configured.")
            };
        });
    }

    /// <summary>
    /// Returns the instance registered for <typeparamref name="T"/>, registering a new one first if needed. Shared
    /// module-level state must be one instance regardless of how many event stores are registered.
    /// </summary>
    private static T GetOrAddSingleton<T>(IServiceCollection services) where T : class, new()
    {
        if (services.FirstOrDefault(s => s.ServiceType == typeof(T))?.ImplementationInstance is T existing)
        {
            return existing;
        }

        var instance = new T();
        services.AddSingleton(instance);
        return instance;
    }

    private static ProtectedStreamRegistry GetOrCreateRegistry(IServiceProvider services)
        => services.GetService<ProtectedStreamRegistry>() ?? new ProtectedStreamRegistry();

    private static void ValidateIntegrityOptions<TAggregate>(MongoEventStoreOptions<TAggregate> options)
        where TAggregate : class, IAggregate, new()
    {
        if (!options.IntegrityProtectionEnabled)
        {
            return;
        }

        if (options.SealingChunkSize <= 0)
        {
            throw new InvalidOperationException(
                $"The sealing chunk size for aggregate type {typeof(TAggregate).Name} must be greater than 0.");
        }

        if (options.SealingSweepInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"The sealing sweep interval for aggregate type {typeof(TAggregate).Name} must be greater than zero.");
        }

        if (options.SealingSweepRetryDelay <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"The sealing sweep retry delay for aggregate type {typeof(TAggregate).Name} must be greater than zero.");
        }
    }
}
