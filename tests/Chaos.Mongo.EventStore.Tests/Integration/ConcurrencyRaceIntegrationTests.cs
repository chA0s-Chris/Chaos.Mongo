// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.Configuration;
using Chaos.Mongo.EventStore.Errors;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using NUnit.Framework;
using Testcontainers.MongoDb;

/// <summary>
/// Reproduces the issue's scenario: many concurrent appends racing for the same explicit version of a fresh
/// aggregate. Without the unique concurrency index, several of them would commit a duplicate position. The index
/// must protect the stream whether or not configurators provisioned it ahead of the first append.
/// </summary>
[TestFixture(false)]
[TestFixture(true)]
public class ConcurrencyRaceIntegrationTests
{
    private const Int32 ConcurrencyPerRound = 8;
    private const Int32 Rounds = 15;
    private readonly Boolean _runConfiguratorsOnStartup;
    private MongoDbContainer _container;

    [OneTimeSetUp]
    public async Task GetMongoDbContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    public ConcurrencyRaceIntegrationTests(Boolean runConfiguratorsOnStartup)
    {
        _runConfiguratorsOnStartup = runConfiguratorsOnStartup;
    }

    [Test]
    public async Task AppendEventsAsync_ConcurrentAppendsAtSameVersion_ExactlyOneSucceedsPerRound()
    {
        var databaseName = $"ConcurrencyRace_{Guid.NewGuid():N}";
        await using var serviceProvider = new ServiceCollection()
                                          .AddLogging()
                                          .AddMongo(MongoUrl.Create(_container.GetConnectionString()), configure: options =>
                                          {
                                              options.DefaultDatabase = databaseName;
                                              options.RunConfiguratorsOnStartup = _runConfiguratorsOnStartup;
                                          })
                                          .WithEventStore<OrderAggregate>(es => es.WithEvent<OrderCreatedEvent>("OrderCreated")
                                                                                  .WithCollectionPrefix("Orders"))
                                          .Services
                                          .BuildServiceProvider();

        if (_runConfiguratorsOnStartup)
        {
            // Simulates the startup-hosted-service path, which provisions the index ahead of any append.
            await serviceProvider.GetRequiredService<IMongoConfiguratorRunner>().RunConfiguratorsAsync();
        }

        var store = serviceProvider.GetRequiredService<IEventStore<OrderAggregate>>();
        var eventsCollection = serviceProvider.GetRequiredService<IMongoHelper>().Database
                                              .GetCollection<Event<OrderAggregate>>("Orders_Events");

        for (var round = 0; round < Rounds; round++)
        {
            var aggregateId = Guid.NewGuid();
            var attempts = Enumerable.Range(0, ConcurrencyPerRound)
                                     .Select(_ => store.AppendEventsAsync([CreateOrder(aggregateId)]))
                                     .ToList();

            var results = await Task.WhenAll(attempts.Select(WaitForOutcomeAsync));

            results.Count(outcome => outcome.Succeeded).Should().Be(1, $"round {round} must have exactly one winner");
            results.Count(outcome => !outcome.Succeeded).Should().Be(ConcurrencyPerRound - 1);
            results.Where(outcome => !outcome.Succeeded)
                   .Should().OnlyContain(outcome => outcome.Exception is MongoConcurrencyException);

            var storedVersions = await eventsCollection.Find(e => e.AggregateId == aggregateId)
                                                       .Project(e => e.Version)
                                                       .ToListAsync();
            storedVersions.Should().Equal(1);
        }
    }

    private static OrderCreatedEvent CreateOrder(Guid aggregateId)
        => new()
        {
            Id = Guid.NewGuid(),
            AggregateId = aggregateId,
            Version = 1,
            CustomerName = "Ada",
            TotalAmount = 12.34m
        };

    private static async Task<(Boolean Succeeded, Exception? Exception)> WaitForOutcomeAsync(Task<OrderAggregate> append)
    {
        try
        {
            await append;
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }
}
