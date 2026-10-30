// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo;

/// <summary>
/// Provides default configuration values for MongoDB operations.
/// </summary>
public static class MongoDefaults
{
    /// <summary>
    /// The default name of the collection used to store distributed locks.
    /// </summary>
    public const String LockCollectionName = "_locks";

    /// <summary>
    /// The default name of the collection used to store migration history.
    /// </summary>
    public const String MigrationHistoryCollectionName = "_migrations";

    /// <summary>
    /// The default name of the distributed lock used for migration coordination.
    /// </summary>
    public const String MigrationsLockName = "ChaosMongoMigrations";

    /// <summary>
    /// Gets the default value indicating whether migrations should run automatically on application startup.
    /// </summary>
    public static Boolean ApplyMigrationsOnStartup => false;

    /// <summary>
    /// Gets the default value indicating whether queue subscriptions should start automatically on application startup.
    /// </summary>
    public static Boolean AutoStartSubscription => false;

    /// <summary>
    /// Gets the default lease time for distributed locks.
    /// </summary>
    public static TimeSpan LockLeaseTime => TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets the default lease time for the migration distributed lock.
    /// </summary>
    public static TimeSpan MigrationLockLeaseTime => TimeSpan.FromMinutes(10);

    /// <summary>
    /// Gets the default maximum number of queue items to fetch and process in a single query.
    /// </summary>
    public static Int32 QueueBatchSize => 1;

    /// <summary>
    /// Gets the default lease time for queue item locks.
    /// </summary>
    public static TimeSpan QueueLockLeaseTime => TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets the default maximum number of retries for failed queue items before they become terminal.
    /// </summary>
    public static Int32? QueueMaxRetries => null;

    /// <summary>
    /// Gets the default retention time for successfully processed queue items before TTL cleanup removes them.
    /// </summary>
    public static TimeSpan? QueueRetentionPeriod => TimeSpan.FromHours(1);

    /// <summary>
    /// Gets the default delay between lock acquisition retry attempts.
    /// </summary>
    public static TimeSpan RetryDelay => TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Gets the default value indicating whether configurators should run automatically on application startup.
    /// </summary>
    public static Boolean RunConfiguratorsOnStartup => false;

    /// <summary>
    /// Gets the default value indicating whether to use CLR type names as collection names when no mapping is found.
    /// </summary>
    public static Boolean UseDefaultCollectionNames => true;

    /// <summary>
    /// Gets the default value indicating whether to use transactions for migrations when available.
    /// </summary>
    public static Boolean UseTransactionsForMigrationsIfAvailable => true;
}
