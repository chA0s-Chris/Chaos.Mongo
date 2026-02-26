// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.BooksApi;

using Chaos.Mongo.EventStore.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using System.Text.RegularExpressions;

public sealed class BooksApiFactory : WebApplicationFactory<BooksApiEntryPoint>
{
    public const String ContentRootEnvironmentVariable = "ASPNETCORE_TEST_CONTENTROOT_CHAOS_MONGO_EVENTSTORE_TESTS";

    private readonly String _connectionString;
    private readonly String _databaseName;

    public BooksApiFactory(String connectionString)
    {
        _connectionString = connectionString;
        _databaseName = $"BooksApiTestDb_{Guid.NewGuid():N}";
    }

    public static String GetRepositoryRootPath()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var appBuilder = WebApplication.CreateBuilder();
        appBuilder.WebHost.UseTestServer();

        appBuilder.Services
                  .AddMongo(_connectionString, configure: options =>
                  {
                      options.DefaultDatabase = _databaseName;
                      options.RunConfiguratorsOnStartup = true;
                  })
                  .WithEventStore<BookAggregate>(es => es
                                                       .WithEvent<BookCreatedEvent>("BookCreated")
                                                       .WithEvent<BookMetadataSetEvent>("BookMetadataSet")
                                                       .WithEvent<BookUpdatedEvent>("BookUpdated")
                                                       .WithEvent<BookEditionAddedEvent>("BookEditionAdded")
                                                       .WithEvent<BookEditionUpdatedEvent>("BookEditionUpdated")
                                                       .WithEvent<BookDeletedEvent>("BookDeleted")
                                                       .WithCollectionPrefix("Books"));

        var app = appBuilder.Build();

        app.MapPost(
            "/books",
            async (
                CreateBookRequest request,
                IEventStore<BookAggregate> eventStore,
                IAggregateRepository<BookAggregate> repository,
                CancellationToken cancellationToken) =>
            {
                if (request.Id == Guid.Empty ||
                    String.IsNullOrWhiteSpace(request.Name) ||
                    String.IsNullOrWhiteSpace(request.Author))
                {
                    return Results.BadRequest("Book id, name and author are required.");
                }

                var createEvent = new BookCreatedEvent
                {
                    Id = Guid.NewGuid(),
                    AggregateId = request.Id,
                    Version = 1,
                    Name = request.Name,
                    Author = request.Author,
                    ReleaseYear = request.ReleaseYear,
                    Metadata = request.Metadata ?? []
                };

                try
                {
                    await eventStore.AppendEventsAsync(
                        [createEvent],
                        cancellationToken: cancellationToken);
                }
                catch (Exception e) when (e is ArgumentException or MongoEventValidationException or MongoConcurrencyException)
                {
                    return Results.Conflict("Book already exists.");
                }

                var created = await repository.GetAsync(request.Id, cancellationToken);
                return created is null
                    ? Results.Problem("Book creation did not produce a read model.")
                    : Results.Created($"/books/{request.Id}", BookResponse.FromAggregate(created));
            });

        app.MapPatch(
            "/books/{bookId:guid}",
            async (
                Guid bookId,
                SetMetadataRequest request,
                IEventStore<BookAggregate> eventStore,
                IAggregateRepository<BookAggregate> repository,
                CancellationToken cancellationToken) =>
            {
                if (request.Metadata.Count == 0)
                {
                    return Results.BadRequest("Metadata cannot be empty.");
                }

                var aggregate = await repository.GetAsync(bookId, cancellationToken);
                if (aggregate is null)
                {
                    return Results.NotFound();
                }

                if (aggregate.IsDeleted)
                {
                    return Results.Conflict("Deleted books cannot be updated.");
                }

                var expectedNextVersion = await eventStore.GetExpectedNextVersionAsync(bookId, cancellationToken);

                try
                {
                    await eventStore.AppendEventsAsync(
                        [
                            new BookMetadataSetEvent
                            {
                                Id = Guid.NewGuid(),
                                AggregateId = bookId,
                                Version = expectedNextVersion,
                                Metadata = request.Metadata
                            }
                        ],
                        cancellationToken: cancellationToken);
                }
                catch (MongoConcurrencyException)
                {
                    return Results.Conflict("Book was modified concurrently.");
                }

                var updated = await repository.GetAsync(bookId, cancellationToken);
                return updated is null ? Results.NotFound() : Results.Ok(BookResponse.FromAggregate(updated));
            });

        app.MapGet(
            "/books/{bookId:guid}",
            async (Guid bookId, IAggregateRepository<BookAggregate> repository, CancellationToken cancellationToken) =>
            {
                var book = await repository.GetAsync(bookId, cancellationToken);
                return book is null ? Results.NotFound() : Results.Ok(BookResponse.FromAggregate(book));
            });

        app.MapPatch(
            "/books/{bookId:guid}/details",
            async (
                Guid bookId,
                UpdateBookRequest request,
                IEventStore<BookAggregate> eventStore,
                IAggregateRepository<BookAggregate> repository,
                CancellationToken cancellationToken) =>
            {
                var aggregate = await repository.GetAsync(bookId, cancellationToken);
                if (aggregate is null)
                {
                    return Results.NotFound();
                }

                if (aggregate.IsDeleted)
                {
                    return Results.Conflict("Deleted books cannot be updated.");
                }

                var expectedNextVersion = await eventStore.GetExpectedNextVersionAsync(bookId, cancellationToken);

                if (request.Name is null && request.Author is null && request.ReleaseYear is null)
                {
                    return Results.BadRequest("At least one field must be provided for update.");
                }

                try
                {
                    await eventStore.AppendEventsAsync(
                        [
                            new BookUpdatedEvent
                            {
                                Id = Guid.NewGuid(),
                                AggregateId = bookId,
                                Version = expectedNextVersion,
                                Name = request.Name,
                                Author = request.Author,
                                ReleaseYear = request.ReleaseYear
                            }
                        ],
                        cancellationToken: cancellationToken);
                }
                catch (MongoConcurrencyException)
                {
                    return Results.Conflict("Book was modified concurrently.");
                }

                var updated = await repository.GetAsync(bookId, cancellationToken);
                return updated is null ? Results.NotFound() : Results.Ok(BookResponse.FromAggregate(updated));
            });

        app.MapPost(
            "/books/{bookId:guid}/editions",
            async (
                Guid bookId,
                AddEditionRequest request,
                IEventStore<BookAggregate> eventStore,
                IAggregateRepository<BookAggregate> repository,
                CancellationToken cancellationToken) =>
            {
                if (request.EditionId == Guid.Empty || String.IsNullOrWhiteSpace(request.Name))
                {
                    return Results.BadRequest("Edition id and name are required.");
                }

                var aggregate = await repository.GetAsync(bookId, cancellationToken);
                if (aggregate is null)
                {
                    return Results.NotFound();
                }

                if (aggregate.IsDeleted)
                {
                    return Results.Conflict("Deleted books cannot be updated.");
                }

                var expectedNextVersion = await eventStore.GetExpectedNextVersionAsync(bookId, cancellationToken);

                try
                {
                    await eventStore.AppendEventsAsync(
                        [
                            new BookEditionAddedEvent
                            {
                                Id = Guid.NewGuid(),
                                AggregateId = bookId,
                                Version = expectedNextVersion,
                                EditionId = request.EditionId,
                                Name = request.Name,
                                ReleaseYear = request.ReleaseYear
                            }
                        ],
                        cancellationToken: cancellationToken);
                }
                catch (MongoEventValidationException ex)
                {
                    return Results.Conflict(ex.Message);
                }
                catch (MongoConcurrencyException)
                {
                    return Results.Conflict("Book was modified concurrently.");
                }

                var updated = await repository.GetAsync(bookId, cancellationToken);
                return updated is null ? Results.NotFound() : Results.Ok(BookResponse.FromAggregate(updated));
            });

        app.MapPatch(
            "/books/{bookId:guid}/editions/{editionId:guid}",
            async (
                Guid bookId,
                Guid editionId,
                UpdateEditionRequest request,
                IEventStore<BookAggregate> eventStore,
                IAggregateRepository<BookAggregate> repository,
                CancellationToken cancellationToken) =>
            {
                if (request.Name is null && request.ReleaseYear is null)
                {
                    return Results.BadRequest("At least one field must be provided for edition update.");
                }

                var aggregate = await repository.GetAsync(bookId, cancellationToken);
                if (aggregate is null)
                {
                    return Results.NotFound();
                }

                if (aggregate.IsDeleted)
                {
                    return Results.Conflict("Deleted books cannot be updated.");
                }

                var expectedNextVersion = await eventStore.GetExpectedNextVersionAsync(bookId, cancellationToken);

                try
                {
                    await eventStore.AppendEventsAsync(
                        [
                            new BookEditionUpdatedEvent
                            {
                                Id = Guid.NewGuid(),
                                AggregateId = bookId,
                                Version = expectedNextVersion,
                                EditionId = editionId,
                                Name = request.Name,
                                ReleaseYear = request.ReleaseYear
                            }
                        ],
                        cancellationToken: cancellationToken);
                }
                catch (MongoEventValidationException ex)
                {
                    return Results.Conflict(ex.Message);
                }
                catch (MongoConcurrencyException)
                {
                    return Results.Conflict("Book was modified concurrently.");
                }

                var updated = await repository.GetAsync(bookId, cancellationToken);
                return updated is null ? Results.NotFound() : Results.Ok(BookResponse.FromAggregate(updated));
            });

        app.MapDelete(
            "/books/{bookId:guid}",
            async (
                Guid bookId,
                IEventStore<BookAggregate> eventStore,
                IAggregateRepository<BookAggregate> repository,
                CancellationToken cancellationToken) =>
            {
                var aggregate = await repository.GetAsync(bookId, cancellationToken);
                if (aggregate is null)
                {
                    return Results.NotFound();
                }

                var expectedNextVersion = await eventStore.GetExpectedNextVersionAsync(bookId, cancellationToken);

                try
                {
                    await eventStore.AppendEventsAsync(
                        [
                            new BookDeletedEvent
                            {
                                Id = Guid.NewGuid(),
                                AggregateId = bookId,
                                Version = expectedNextVersion
                            }
                        ],
                        cancellationToken: cancellationToken);
                }
                catch (MongoEventValidationException ex)
                {
                    return Results.Conflict(ex.Message);
                }
                catch (MongoConcurrencyException)
                {
                    return Results.Conflict("Book was modified concurrently.");
                }

                var deleted = await repository.GetAsync(bookId, cancellationToken);
                return deleted is null ? Results.NotFound() : Results.Ok(BookResponse.FromAggregate(deleted));
            });

        app.MapGet(
            "/books/search",
            async (
                String name,
                IAggregateRepository<BookAggregate> repository,
                CancellationToken cancellationToken) =>
            {
                if (String.IsNullOrWhiteSpace(name))
                {
                    return Results.BadRequest("Search name is required.");
                }

                var filter = Builders<BookAggregate>.Filter.And(
                    Builders<BookAggregate>.Filter.Eq(x => x.IsDeleted, false),
                    Builders<BookAggregate>.Filter.Regex(
                        x => x.Name,
                        new(Regex.Escape(name), "i")));

                var books = await repository.Collection
                                            .Find(filter)
                                            .SortBy(x => x.Name)
                                            .ToListAsync(cancellationToken);

                return Results.Ok(books.Select(BookResponse.FromAggregate).ToList());
            });

        app.Start();
        return app;
    }
}
