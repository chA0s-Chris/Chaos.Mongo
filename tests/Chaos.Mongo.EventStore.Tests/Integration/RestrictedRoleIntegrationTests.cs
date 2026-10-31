// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.Configuration;
using Chaos.Mongo.EventStore.Errors;
using Chaos.Mongo.TestSupport;
using Docker.DotNet.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

/// <summary>
/// Covers appends on a connection authenticated as the documented "Application" role, which is granted only
/// <c>insert</c> and <c>find</c> on the events collection and therefore cannot create the concurrency index
/// itself. This needs its own authenticated container; the shared <see cref="MongoDbTestContainer"/> runs without
/// authentication.
/// </summary>
public class RestrictedRoleIntegrationTests
{
    private const String ApplicationPassword = "application-secret";
    private const String ApplicationUsername = "eventStoreApplication";
    private const String DatabaseName = "RestrictedRoleTestDb";
    private const String EventsCollectionName = "Orders_Events";
    private const String RootPassword = "root-secret";
    private const String RootUsername = "root";

    private MongoDbContainer _container;

    [Test]
    public async Task AppendEventsAsync_RestrictedRoleWithOptOutAfterIndexProvisionedOutOfBand_Succeeds()
    {
        await ProvisionConcurrencyIndexWithElevatedConnectionAsync();

        await using var serviceProvider = CreateApplicationServiceProvider(true);
        var store = serviceProvider.GetRequiredService<IEventStore<OrderAggregate>>();

        var aggregate = await store.AppendEventsAsync([CreateOrder(Guid.NewGuid())]);

        aggregate.Status.Should().Be("Created");
    }

    [Test]
    public async Task AppendEventsAsync_RestrictedRoleWithoutOptOut_ThrowsMongoConcurrencyIndexMissingException()
    {
        // Independent of execution order relative to the opt-out test above: reset the events collection with the
        // elevated connection first, so the missing-index precondition this test is named for actually holds,
        // rather than coincidentally passing because the restricted role lacks createIndex regardless of whether
        // the index already exists.
        var eventsCollection = await ResetEventsCollectionWithElevatedConnectionAsync();
        (await (await eventsCollection.Indexes.ListAsync()).ToListAsync())
            .Should().NotContain(index => index["name"] == IndexNames.AggregateIdWithVersionUnique);

        await using var serviceProvider = CreateApplicationServiceProvider(false);
        var store = serviceProvider.GetRequiredService<IEventStore<OrderAggregate>>();

        var act = () => store.AppendEventsAsync([CreateOrder(Guid.NewGuid())]);

        await act.Should().ThrowAsync<MongoConcurrencyIndexMissingException>()
                 .WithMessage("*lacks permission to create indexes*");

        (await eventsCollection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty)).Should().Be(0);
    }

    [OneTimeSetUp]
    public async Task StartContainerAndCreateApplicationUserAsync()
    {
        _container = new MongoDbBuilder()
                     .WithImage(MongoDbTestImages.Selected)
                     .WithUsername(RootUsername)
                     .WithPassword(RootPassword)
                     .WithReplicaSet()
                     .WithCommand("--setParameter", "enableTestCommands=1")
                     .WithEnvironment("GLIBC_TUNABLES", "glibc.pthread.rseq=1")
                     .WithCreateParameterModifier(parameters =>
                     {
                         parameters.HostConfig ??= new HostConfig();
                         parameters.HostConfig.Ulimits =
                         [
                             new Ulimit
                             {
                                 Name = "nofile",
                                 Soft = 65536,
                                 Hard = 65536
                             }
                         ];
                     })
                     .Build();

        await _container.StartAsync();

        await _container.ExecScriptAsync($$"""
                                           db.getSiblingDB('{{DatabaseName}}').createRole({
                                               role: 'eventStoreApplicationRole',
                                               privileges: [
                                                   {
                                                       resource: { db: '{{DatabaseName}}', collection: '{{EventsCollectionName}}' },
                                                       actions: ['insert', 'find']
                                                   },
                                                   {
                                                       resource: { db: '{{DatabaseName}}', collection: 'Orders' },
                                                       actions: ['insert', 'update', 'find']
                                                   }
                                               ],
                                               roles: []
                                           });
                                           db.getSiblingDB('{{DatabaseName}}').createUser({
                                               user: '{{ApplicationUsername}}',
                                               pwd: '{{ApplicationPassword}}',
                                               roles: [ { role: 'eventStoreApplicationRole', db: '{{DatabaseName}}' } ]
                                           });
                                           """);
    }

    [OneTimeTearDown]
    public async Task StopContainerAsync() => await _container.DisposeAsync();

    private static OrderCreatedEvent CreateOrder(Guid aggregateId)
        => new()
        {
            Id = Guid.NewGuid(),
            AggregateId = aggregateId,
            CustomerName = "Ada",
            TotalAmount = 12.34m
        };

    private MongoUrl BuildMongoUrl(String username, String password, String authenticationSource)
        // Overrides only the credentials on the container's own connection string, so the replica-set
        // discovery parameters it already sets (e.g. directConnection) survive unchanged.
        => new MongoUrlBuilder(_container.GetConnectionString())
        {
            Username = username,
            Password = password,
            AuthenticationSource = authenticationSource
        }.ToMongoUrl();

    private ServiceProvider CreateApplicationServiceProvider(Boolean optOut)
    {
        var url = BuildMongoUrl(ApplicationUsername, ApplicationPassword, DatabaseName);
        return new ServiceCollection()
               .AddMongo(url, configure: options =>
               {
                   options.DefaultDatabase = DatabaseName;
                   options.RunConfiguratorsOnStartup = false;
               })
               .WithEventStore<OrderAggregate>(es =>
               {
                   es.WithEvent<OrderCreatedEvent>("OrderCreated")
                     .WithCollectionPrefix("Orders");

                   if (optOut)
                   {
                       es.WithoutConcurrencyIndexVerification();
                   }
               })
               .Services
               .BuildServiceProvider();
    }

    private async Task ProvisionConcurrencyIndexWithElevatedConnectionAsync()
    {
        var url = BuildMongoUrl(RootUsername, RootPassword, "admin");
        await using var serviceProvider = new ServiceCollection()
                                          .AddLogging()
                                          .AddMongo(url, configure: options =>
                                          {
                                              options.DefaultDatabase = DatabaseName;
                                              options.RunConfiguratorsOnStartup = false;
                                          })
                                          .WithEventStore<OrderAggregate>(es => es.WithEvent<OrderCreatedEvent>("OrderCreated")
                                                                                  .WithCollectionPrefix("Orders"))
                                          .Services
                                          .BuildServiceProvider();

        await serviceProvider.GetRequiredService<IMongoConfiguratorRunner>().RunConfiguratorsAsync();
    }

    /// <summary>
    /// Drops and recreates the (empty) events collection with the elevated connection, so neither leftover
    /// documents nor a leftover concurrency index (for example from
    /// <see cref="ProvisionConcurrencyIndexWithElevatedConnectionAsync"/> in another test) can affect this test's
    /// precondition. Recreating the collection, rather than leaving it dropped, keeps <c>listIndexes</c>
    /// well-defined across the supported server versions. Returns an elevated-connection handle to the
    /// collection for the caller's own precondition and outcome assertions.
    /// </summary>
    private async Task<IMongoCollection<BsonDocument>> ResetEventsCollectionWithElevatedConnectionAsync()
    {
        var client = new MongoClient(BuildMongoUrl(RootUsername, RootPassword, "admin"));
        var database = client.GetDatabase(DatabaseName);
        await database.DropCollectionAsync(EventsCollectionName);
        await database.CreateCollectionAsync(EventsCollectionName);
        return database.GetCollection<BsonDocument>(EventsCollectionName);
    }
}
