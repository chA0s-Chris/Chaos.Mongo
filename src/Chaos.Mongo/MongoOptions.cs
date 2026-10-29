// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo;

using MongoDB.Driver;

/// <summary>
/// Configuration options for MongoDB connection and behavior.
/// </summary>
public sealed class MongoOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether to automatically run registered <see cref="Migrations.IMongoMigration"/>
    /// instances on application startup.
    /// Defaults to <see cref="MongoDefaults.ApplyMigrationsOnStartup"/>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     When set to <c>true</c>, all registered migrations will be executed automatically during application startup
    ///     via the <see cref="MongoHostedService"/>. When <c>false</c>, migrations must be run manually using
    ///     <see cref="Migrations.IMongoMigrationRunner"/>.
    ///     </para>
    ///     <para>
    ///     <strong>Warning:</strong> In distributed systems with multiple instances, only one instance will acquire
    ///     the migration lock and apply migrations. Other instances will skip migration execution.
    ///     </para>
    /// </remarks>
    public Boolean ApplyMigrationsOnStartup { get; set; } = MongoDefaults.ApplyMigrationsOnStartup;

    /// <summary>
    /// Gets or initializes the dictionary mapping CLR types to MongoDB collection names.
    /// </summary>
    /// <remarks>
    /// If a type is not found in the dictionary, the default collection name is used when
    /// <see cref="UseDefaultCollectionNames"/> is set to <c>true</c>. Otherwise, a <see cref="KeyNotFoundException"/>
    /// is thrown at runtime.
    /// </remarks>
    public Dictionary<Type, String> CollectionTypeMap { get; init; } = [];

    /// <summary>
    /// Gets or sets an action to configure the <see cref="MongoClientSettings"/> used to create
    /// <see cref="IMongoClient"/>'s underlying <see cref="MongoClient"/> instance.
    /// </summary>
    /// <remarks>
    /// The default implementation of <see cref="IMongoClientSettingsFactory"/> invokes this action
    /// if it is set. Otherwise, the settings are created using <see cref="MongoClientSettings.FromUrl(MongoUrl)"/>.
    /// </remarks>
    public Action<MongoClientSettings>? ConfigureClientSettings { get; set; }

    /// <summary>
    /// Gets or sets the default database name to use when not specified in the URL.
    /// </summary>
    public String? DefaultDatabase { get; set; }

    /// <summary>
    /// Gets or sets the unique identifier for this lock holder instance.
    /// </summary>
    /// <remarks>
    /// If not specified, a new GUID will be generated for each instance.
    /// </remarks>
    public String? HolderId { get; set; }

    /// <summary>
    /// Gets or sets the name of the collection used to store distributed locks.
    /// Defaults to <see cref="MongoDefaults.LockCollectionName"/>.
    /// </summary>
    public String LockCollectionName { get; set; } = MongoDefaults.LockCollectionName;

    /// <summary>
    /// Gets or sets the name of the collection used to store migration history.
    /// Defaults to <see cref="MongoDefaults.MigrationHistoryCollectionName"/>.
    /// </summary>
    /// <remarks>
    /// This collection stores <see cref="Migrations.MongoMigrationHistoryItem"/> documents that track
    /// which migrations have been applied, when they were applied, and how long they took.
    /// </remarks>
    public String MigrationHistoryCollectionName { get; set; } = MongoDefaults.MigrationHistoryCollectionName;

    /// <summary>
    /// Gets or sets the lease time for the distributed migration lock.
    /// Defaults to <see cref="MongoDefaults.MigrationLockLeaseTime"/>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     This determines how long a migration lock is valid before it automatically expires.
    ///     The lock prevents concurrent migration execution across multiple application instances.
    ///     </para>
    ///     <para>
    ///     If a migration takes longer than this duration, an <see cref="InvalidOperationException"/> will be thrown.
    ///     Ensure this value is large enough for your longest migration to complete.
    ///     </para>
    /// </remarks>
    public TimeSpan MigrationLockLeaseTime { get; set; } = MongoDefaults.MigrationLockLeaseTime;

    /// <summary>
    /// Gets or sets the name of the distributed lock used to coordinate migration execution.
    /// Defaults to <see cref="MongoDefaults.MigrationsLockName"/>.
    /// </summary>
    /// <remarks>
    /// This lock name is used in the lock collection (see <see cref="LockCollectionName"/>) to ensure
    /// only one application instance runs migrations at a time.
    /// </remarks>
    public String MigrationsLockName { get; set; } = MongoDefaults.MigrationsLockName;

    /// <summary>
    /// Gets or sets a value indicating whether to automatically run registered <see cref="Configuration.IMongoConfigurator"/>
    /// instances on application startup.
    /// Defaults to <see cref="MongoDefaults.RunConfiguratorsOnStartup"/>.
    /// </summary>
    /// <remarks>
    /// When set to <c>true</c>, all registered configurators will be executed automatically during application startup
    /// via the <see cref="MongoHostedService"/>. When <c>false</c>, configurators must be run manually using
    /// <see cref="Configuration.IMongoConfiguratorRunner"/>.
    /// </remarks>
    public Boolean RunConfiguratorsOnStartup { get; set; } = MongoDefaults.RunConfiguratorsOnStartup;

    /// <summary>
    /// Gets or sets the MongoDB connection URL.
    /// </summary>
    public MongoUrl? Url { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to use the CLR type name as the collection name when no mapping is found.
    /// Defaults to <see cref="MongoDefaults.UseDefaultCollectionNames"/>.
    /// </summary>
    /// <remarks>
    /// If a type is not present in <see cref="CollectionTypeMap"/> and <see cref="UseDefaultCollectionNames"/>
    /// is set to <c>false</c>, a <see cref="KeyNotFoundException"/> is thrown at runtime.
    /// </remarks>
    public Boolean UseDefaultCollectionNames { get; set; } = MongoDefaults.UseDefaultCollectionNames;

    /// <summary>
    /// Gets or sets a value indicating whether to wrap each migration in a transaction when transactions are available.
    /// Defaults to <see cref="MongoDefaults.UseTransactionsForMigrationsIfAvailable"/>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     When <c>true</c>, the migration runner will attempt to start a transaction before applying each migration.
    ///     If transactions are not supported (e.g., standalone server, older MongoDB version), the migration
    ///     will run without a transaction.
    ///     </para>
    ///     <para>
    ///     Transactions provide ACID guarantees - if a migration fails, all changes are rolled back.
    ///     However, some operations (e.g., creating indexes, dropping collections) cannot run in transactions.
    ///     </para>
    ///     <para>
    ///     Set to <c>false</c> to disable transactional migration execution entirely.
    ///     </para>
    /// </remarks>
    public Boolean UseTransactionsForMigrationsIfAvailable { get; set; } =
        MongoDefaults.UseTransactionsForMigrationsIfAvailable;

    /// <summary>
    /// Adds a type-to-collection name mapping.
    /// </summary>
    /// <remarks>
    /// Mappings are stored in <see cref="CollectionTypeMap"/>.
    /// This method is a convenience wrapper for adding a mapping to the dictionary.
    /// </remarks>
    /// <typeparam name="T">The CLR type to map.</typeparam>
    /// <param name="collectionName">The MongoDB collection name. If null, the type name is used.</param>
    /// <returns>This <see cref="MongoOptions"/> instance for method chaining.</returns>
    public MongoOptions AddMapping<T>(String? collectionName) => AddMapping(typeof(T), collectionName);

    /// <summary>
    /// Adds a type-to-collection name mapping.
    /// </summary>
    /// <remarks>
    /// Mappings are stored in <see cref="CollectionTypeMap"/>.
    /// This method is a convenience wrapper for adding a mapping to the dictionary.
    /// </remarks>
    /// <param name="type">The CLR type to map.</param>
    /// <param name="collectionName">The MongoDB collection name. If null, the type name is used.</param>
    /// <returns>This <see cref="MongoOptions"/> instance for method chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="type"/> is null.</exception>
    public MongoOptions AddMapping(Type type, String? collectionName)
    {
        ArgumentNullException.ThrowIfNull(type);
        collectionName ??= type.Name;

        CollectionTypeMap.Add(type, collectionName);
        return this;
    }
}
