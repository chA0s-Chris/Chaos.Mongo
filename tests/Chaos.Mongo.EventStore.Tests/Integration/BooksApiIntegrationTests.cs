// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Tests.BooksApi;
using FluentAssertions;
using NUnit.Framework;
using System.Net;
using System.Net.Http.Json;
using Testcontainers.MongoDb;

public class BooksApiIntegrationTests
{
    private HttpClient? _client;
    private MongoDbContainer _container = null!;
    private BooksApiFactory? _factory;

    private HttpClient Client => _client ?? throw new InvalidOperationException("HTTP client is not initialized.");

    [Test]
    public async Task BookLifecycle_UsesAllEndpoints_AndVerifiesResults()
    {
        var bookId = Guid.NewGuid();
        var editionId = Guid.NewGuid();

        var createRequest = new CreateBookRequest
        {
            Id = bookId,
            Name = "Weaveworld",
            Author = "Clive Barker",
            ReleaseYear = 1987,
            Metadata = new()
            {
                ["genre"] = "Dark Fantasy"
            }
        };

        var createResponse = await PostCreateBookAsync(createRequest);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var createdBook = await createResponse.Content.ReadFromJsonAsync<BookResponse>();
        createdBook.Should().NotBeNull();
        createdBook.Version.Should().Be(1);
        createdBook.Metadata.Should().ContainSingle(x => x.Key == "genre" && x.Value == "Dark Fantasy");

        var duplicateCreateResponse = await PostCreateBookAsync(createRequest);
        duplicateCreateResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var setMetadataResponse = await Client.PatchAsJsonAsync(
            $"/books/{bookId}",
            new SetMetadataRequest
            {
                Metadata = new()
                {
                    ["genre"] = "Epic Fantasy",
                    ["language"] = "English"
                }
            });

        setMetadataResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var metadataUpdatedBook = await setMetadataResponse.Content.ReadFromJsonAsync<BookResponse>();
        metadataUpdatedBook.Should().NotBeNull();
        metadataUpdatedBook.Version.Should().Be(2);
        metadataUpdatedBook.Metadata.Should().Contain(x => x.Key == "genre" && x.Value == "Epic Fantasy");
        metadataUpdatedBook.Metadata.Should().Contain(x => x.Key == "language" && x.Value == "English");

        var readResponse = await Client.GetAsync($"/books/{bookId}");
        readResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var readBook = await readResponse.Content.ReadFromJsonAsync<BookResponse>();
        readBook.Should().NotBeNull();
        readBook.Id.Should().Be(bookId);
        readBook.Version.Should().Be(2);

        var updateBookResponse = await Client.PatchAsJsonAsync(
            $"/books/{bookId}/details",
            new UpdateBookRequest
            {
                Author = "Clive Barker",
                ReleaseYear = 1988
            });

        updateBookResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var updatedBook = await updateBookResponse.Content.ReadFromJsonAsync<BookResponse>();
        updatedBook.Should().NotBeNull();
        updatedBook.Version.Should().Be(3);
        updatedBook.Author.Should().Be("Clive Barker");
        updatedBook.ReleaseYear.Should().Be(1988);

        var addEditionResponse = await Client.PostAsJsonAsync(
            $"/books/{bookId}/editions",
            new AddEditionRequest
            {
                EditionId = editionId,
                Name = "Weaveworld (Special Edition)",
                ReleaseYear = 1990
            });

        addEditionResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var bookWithEdition = await addEditionResponse.Content.ReadFromJsonAsync<BookResponse>();
        bookWithEdition.Should().NotBeNull();
        bookWithEdition.Version.Should().Be(4);
        bookWithEdition.Editions.Should().ContainSingle(x => x.Id == editionId && x.ReleaseYear == 1990);

        var updateEditionResponse = await Client.PatchAsJsonAsync(
            $"/books/{bookId}/editions/{editionId}",
            new UpdateEditionRequest
            {
                ReleaseYear = 1991
            });

        updateEditionResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var updatedEditionBook = await updateEditionResponse.Content.ReadFromJsonAsync<BookResponse>();
        updatedEditionBook.Should().NotBeNull();
        updatedEditionBook.Version.Should().Be(5);
        updatedEditionBook.Editions.Should().ContainSingle(x => x.Id == editionId && x.ReleaseYear == 1991);

        var searchBeforeDeleteResponse = await Client.GetAsync("/books/search?name=wea");
        searchBeforeDeleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var searchBeforeDelete = await searchBeforeDeleteResponse.Content.ReadFromJsonAsync<List<BookResponse>>();
        searchBeforeDelete.Should().NotBeNull();
        searchBeforeDelete.Should().ContainSingle(x => x.Id == bookId);

        var deleteResponse = await Client.DeleteAsync($"/books/{bookId}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var deletedBook = await deleteResponse.Content.ReadFromJsonAsync<BookResponse>();
        deletedBook.Should().NotBeNull();
        deletedBook.Version.Should().Be(6);
        deletedBook.IsDeleted.Should().BeTrue();

        var readDeletedResponse = await Client.GetAsync($"/books/{bookId}");
        readDeletedResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var readDeletedBook = await readDeletedResponse.Content.ReadFromJsonAsync<BookResponse>();
        readDeletedBook.Should().NotBeNull();
        readDeletedBook.IsDeleted.Should().BeTrue();

        var searchAfterDeleteResponse = await Client.GetAsync("/books/search?name=wea");
        searchAfterDeleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var searchAfterDelete = await searchAfterDeleteResponse.Content.ReadFromJsonAsync<List<BookResponse>>();
        searchAfterDelete.Should().NotBeNull();
        searchAfterDelete.Should().BeEmpty();
    }

    [Test]
    public async Task CreateBook_SameIdWithDifferentPayload_ReturnsConflict()
    {
        var bookId = Guid.NewGuid();

        var firstRequest = BuildCreateBookRequest(bookId, "Clive Barker");
        var firstCreateResponse = await PostCreateBookAsync(firstRequest);
        firstCreateResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var secondRequest = BuildCreateBookRequest(bookId, "C. Barker");
        var secondCreateResponse = await PostCreateBookAsync(secondRequest);
        secondCreateResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [SetUp]
    public void Setup()
    {
        Environment.SetEnvironmentVariable(BooksApiFactory.ContentRootEnvironmentVariable, BooksApiFactory.GetRepositoryRootPath());
        _factory = new(_container.GetConnectionString());
        _client = _factory.CreateClient();
    }

    [OneTimeSetUp]
    public async Task StartContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [TearDown]
    public void TearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
        Environment.SetEnvironmentVariable(BooksApiFactory.ContentRootEnvironmentVariable, null);
    }

    [Test]
    public async Task UpdateBook_ConcurrentRequests_RetrySucceeds()
    {
        var bookId = Guid.NewGuid();

        var createResponse = await PostCreateBookAsync(BuildCreateBookRequest(bookId, "Clive Barker"));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var metadataResponse = await Client.PatchAsJsonAsync(
            $"/books/{bookId}",
            new SetMetadataRequest
            {
                Metadata = new()
                {
                    ["genre"] = "Epic Fantasy"
                }
            });
        metadataResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var firstUpdateTask = Client.PatchAsJsonAsync(
            $"/books/{bookId}/details",
            new UpdateBookRequest
            {
                Author = "Author from request 1"
            });

        var secondUpdateTask = Client.PatchAsJsonAsync(
            $"/books/{bookId}/details",
            new UpdateBookRequest
            {
                Author = "Author from request 2"
            });

        var responses = await Task.WhenAll(firstUpdateTask, secondUpdateTask);
        responses.Should().OnlyContain(x => x.StatusCode == HttpStatusCode.OK || x.StatusCode == HttpStatusCode.Conflict);

        var retryResponse = await Client.PatchAsJsonAsync(
            $"/books/{bookId}/details",
            new UpdateBookRequest
            {
                Author = "Author after retry"
            });

        retryResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static CreateBookRequest BuildCreateBookRequest(Guid bookId, String author)
    {
        return new()
        {
            Id = bookId,
            Name = "Weaveworld",
            Author = author,
            ReleaseYear = 1987,
            Metadata = new()
            {
                ["genre"] = "Dark Fantasy"
            }
        };
    }

    private async Task<HttpResponseMessage> PostCreateBookAsync(CreateBookRequest request)
        => await Client.PostAsJsonAsync("/books", request);
}
