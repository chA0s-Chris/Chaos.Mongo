// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.BooksApi;

public sealed class CreateBookRequest
{
    public String Author { get; set; } = String.Empty;
    public Guid Id { get; set; }
    public Dictionary<String, String>? Metadata { get; set; }
    public String Name { get; set; } = String.Empty;
    public Int32 ReleaseYear { get; set; }
}

public sealed class SetMetadataRequest
{
    public Dictionary<String, String> Metadata { get; set; } = [];
}

public sealed class UpdateBookRequest
{
    public String? Author { get; set; }
    public String? Name { get; set; }
    public Int32? ReleaseYear { get; set; }
}

public sealed class AddEditionRequest
{
    public Guid EditionId { get; set; }
    public String Name { get; set; } = String.Empty;
    public Int32 ReleaseYear { get; set; }
}

public sealed class UpdateEditionRequest
{
    public String? Name { get; set; }
    public Int32? ReleaseYear { get; set; }
}

public sealed class BookResponse
{
    public String Author { get; set; } = String.Empty;
    public List<BookEditionResponse> Editions { get; set; } = [];
    public Guid Id { get; set; }
    public Boolean IsDeleted { get; set; }
    public Dictionary<String, String> Metadata { get; set; } = [];
    public String Name { get; set; } = String.Empty;
    public Int32 ReleaseYear { get; set; }
    public Int64 Version { get; set; }

    public static BookResponse FromAggregate(BookAggregate aggregate)
    {
        return new()
        {
            Id = aggregate.Id,
            Name = aggregate.Name,
            Author = aggregate.Author,
            ReleaseYear = aggregate.ReleaseYear,
            IsDeleted = aggregate.IsDeleted,
            Version = aggregate.Version,
            Metadata = new(aggregate.Metadata, StringComparer.Ordinal),
            Editions = aggregate.Editions
                                .Select(x => new BookEditionResponse
                                {
                                    Id = x.Id,
                                    Name = x.Name,
                                    ReleaseYear = x.ReleaseYear
                                })
                                .ToList()
        };
    }
}

public sealed class BookEditionResponse
{
    public Guid Id { get; set; }
    public String Name { get; set; } = String.Empty;
    public Int32 ReleaseYear { get; set; }
}
