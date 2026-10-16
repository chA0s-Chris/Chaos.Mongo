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
            GetOrAddSingleton<ProtectedStreamRegistry>(builder.Services).Add(new ProtectedStreamType<TAggregate>(options));

            // Erasure appends through the scoped event store, so it is scoped too.
            builder.Services.AddScoped(sp => new PersonalDataEraser<TAggregate>(
                                           sp.GetRequiredService<IMongoHelper>(),
                                           options,
                                           sp.GetRequiredService<IEventStore<TAggregate>>()));
            builder.Services.AddScoped<IPersonalDataEraser<TAggregate>>(sp => sp.GetRequiredService<PersonalDataEraser<TAggregate>>());

            // Verification compares streams with anchors when anchoring or verification is configured at module level.
            builder.Services.AddSingleton(sp => new EventStreamVerifier<TAggregate>(
                                              sp.GetRequiredService<IMongoHelper>(),
                                              options,
                                              sp.GetService<AnchorVerifier>()));
            builder.Services.AddSingleton(sp => new DerivedDataVerifier<TAggregate>(
                                              sp.GetRequiredService<IMongoHelper>(),
                                              options,
                                              sp.GetRequiredService<EventStreamVerifier<TAggregate>>()));
            builder.Services.AddSingleton(sp => new CompletenessChecker<TAggregate>(
                                              sp.GetRequiredService<IMongoHelper>(),
                                              options,
                                              sp.GetService<AnchorVerifier>(),
                                              sp.GetService<TimeProvider>() ?? TimeProvider.System));
            builder.Services.AddSingleton<IEventStoreIntegrity<TAggregate>>(sp => new EventStoreIntegrity<TAggregate>(
                                                                                sp.GetRequiredService<EventStreamVerifier<TAggregate>>(),
                                                                                sp.GetRequiredService<DerivedDataVerifier<TAggregate>>(),
                                                                                sp.GetRequiredService<CompletenessChecker<TAggregate>>(),
                                                                                sp.GetRequiredService<EventStreamSealingSweep<TAggregate>>()));

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
    /// Configures the anchoring pipeline once at module level: a hosted service anchors the changed stream heads of all
    /// protected aggregate types at the configured interval and has the anchors timestamped. It registers the
    /// change-stream candidate source through <see cref="WithAnchorCandidates"/>.
    /// </summary>
    /// <param name="builder">The Mongo builder.</param>
    /// <param name="configure">Configures the authorities, the completion hook and the run settings.</param>
    /// <returns>The Mongo builder for method chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when anchoring is already configured, when no timestamp authority is configured, when a setting is out
    /// of range, or when the verification configuration names other anchor collections.
    /// </exception>
    public static MongoBuilder WithIntegrityAnchoring(this MongoBuilder builder, Action<IntegrityAnchoringBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        if (builder.Services.Any(s => s.ServiceType == typeof(IntegrityAnchoringService)))
        {
            throw new InvalidOperationException("Integrity anchoring is already configured; configure it once at module level.");
        }

        var options = GetOrAddSingleton<IntegrityAnchoringOptions>(builder.Services);
        var configured = builder.Services.FirstOrDefault(s => s.ServiceType == typeof(IntegrityVerificationBuilder))?.ImplementationInstance as IntegrityVerificationBuilder;

        // Collections named for verification never pre-fill the anchoring configuration, so the outcome does not depend
        // on the registration order.
        configured?.RestoreDefaultAnchorCollections();
        var anchoring = new IntegrityAnchoringBuilder(options);
        configure(anchoring);
        anchoring.Validate();
        configured?.EnsureAnchorCollectionsMatch();

        // The trust settings of RFC 3161 authorities also apply when their stored tokens are verified.
        var verification = GetOrAddSingleton<IntegrityVerificationOptions>(builder.Services);
        foreach (var authority in anchoring.Rfc3161Authorities)
        {
            var trust = new TimestampAuthorityTrust
            {
                PolicyId = authority.PolicyId
            };
            trust.TrustedRoots.AddRange(authority.TrustedRoots);
            verification.AnchoringAuthorities[authority.Name] = trust;
        }

        AddAnchorVerifier(builder.Services);
        builder.WithAnchorCandidates();
        builder.Services.AddTransient<IMongoConfigurator>(_ => new IntegrityAnchoringConfigurator(options));
        builder.Services.AddSingleton(sp => new AnchorLeafReader(sp.GetRequiredService<IMongoHelper>(), options));
        builder.Services.AddSingleton(sp => new SealingAnchorLookup(sp.GetRequiredService<IMongoHelper>(), options));
        builder.Services.AddSingleton(sp => new AnchorEvidenceResolver(sp.GetRequiredService<IMongoHelper>(), options));
        builder.Services.AddSingleton(sp =>
        {
            var timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            var notifier = anchoring.CompletionHook is { } hook
                ? new AnchorCompletionNotifier(sp.GetRequiredService<IMongoHelper>(),
                                               options,
                                               sp.GetRequiredService<IServiceScopeFactory>(),
                                               hook,
                                               timeProvider,
                                               sp.GetService<ILogger<AnchorCompletionNotifier>>() ?? NullLogger<AnchorCompletionNotifier>.Instance)
                : null;
            return new AnchoringRun(sp.GetRequiredService<IMongoHelper>(),
                                    sp.GetRequiredService<IAnchorCandidateSource>(),
                                    anchoring.CreateAuthorities(sp),
                                    options,
                                    timeProvider,
                                    sp.GetService<ILogger<AnchoringRun>>() ?? NullLogger<AnchoringRun>.Instance,
                                    notifier);
        });
        builder.Services.AddSingleton(sp => new IntegrityAnchoringService(sp.GetRequiredService<AnchoringRun>(),
                                                                          sp.GetRequiredService<IMongoHelper>(),
                                                                          options,
                                                                          sp.GetService<TimeProvider>() ?? TimeProvider.System,
                                                                          sp.GetService<ILogger<IntegrityAnchoringService>>() ??
                                                                          NullLogger<IntegrityAnchoringService>.Instance));
        builder.Services.AddHostedService(sp => new IntegrityAnchoringHostedService(sp.GetRequiredService<IntegrityAnchoringService>()));
        return builder;
    }

    /// <summary>
    /// Configures how anchors and their stored timestamp tokens are verified, once at module level. It registers the
    /// anchor verification without the anchoring run, so a container that only verifies, for example an audit tool
    /// with read-only credentials, needs no timestamp authority. With anchoring configured as well, the trust settings
    /// of its RFC 3161 authorities apply unless configured here.
    /// </summary>
    /// <param name="builder">The Mongo builder.</param>
    /// <param name="configure">Configures the trust settings, the revocation grace period and the anchor collections.</param>
    /// <returns>The Mongo builder for method chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when verification is already configured, or when it names other anchor collections than the anchoring
    /// configuration.
    /// </exception>
    public static MongoBuilder WithIntegrityVerification(this MongoBuilder builder, Action<IntegrityVerificationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var verification = GetOrAddSingleton<IntegrityVerificationOptions>(builder.Services);
        if (builder.Services.Any(s => s.ServiceType == typeof(IntegrityVerificationBuilder)))
        {
            throw new InvalidOperationException("Integrity verification is already configured; configure it once at module level.");
        }

        var verificationBuilder = new IntegrityVerificationBuilder(verification, GetOrAddSingleton<IntegrityAnchoringOptions>(builder.Services));
        configure?.Invoke(verificationBuilder);

        // Verification must not redirect where an anchoring configuration writes.
        if (builder.Services.Any(s => s.ServiceType == typeof(IntegrityAnchoringService)))
        {
            verificationBuilder.EnsureAnchorCollectionsMatch();
        }
        else
        {
            verificationBuilder.ApplyAnchorCollections();
        }

        // Registered to detect a second configuration and an anchoring configuration with other collections.
        builder.Services.AddSingleton(verificationBuilder);
        AddAnchorVerifier(builder.Services);
        return builder;
    }

    /// <summary>
    /// Collects anchor candidates for all protected aggregate types from one change stream. Event stores may be
    /// registered before or after this call; the source reads the protected types when it is resolved.
    /// </summary>
    /// <param name="builder">The Mongo builder.</param>
    /// <returns>The Mongo builder for method chaining.</returns>
    internal static MongoBuilder WithAnchorCandidates(this MongoBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var anchoring = GetOrAddSingleton<IntegrityAnchoringOptions>(builder.Services);
        if (builder.Services.Any(s => s.ServiceType == typeof(IAnchorCandidateSource)))
        {
            return builder;
        }

        builder.Services.AddSingleton<IAnchorCandidateSource>(sp => new ChangeStreamAnchorCandidateSource(
                                                                  sp.GetRequiredService<IMongoHelper>(),
                                                                  anchoring,
                                                                  sp.GetService<ProtectedStreamRegistry>() ?? new ProtectedStreamRegistry(),
                                                                  sp.GetService<TimeProvider>() ?? TimeProvider.System,
                                                                  sp.GetService<ILogger<ChangeStreamAnchorCandidateSource>>() ??
                                                                  NullLogger<ChangeStreamAnchorCandidateSource>.Instance));
        return builder;
    }

    private static void AddAnchorVerifier(IServiceCollection services)
    {
        if (services.Any(s => s.ServiceType == typeof(AnchorVerifier)))
        {
            return;
        }

        var anchoring = GetOrAddSingleton<IntegrityAnchoringOptions>(services);
        var verification = GetOrAddSingleton<IntegrityVerificationOptions>(services);
        services.AddSingleton(sp => new AnchorVerifier(sp.GetRequiredService<IMongoHelper>(), anchoring, verification));
        services.AddSingleton<IAnchorVerifier>(sp => sp.GetRequiredService<AnchorVerifier>());
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
