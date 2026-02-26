// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.BooksApi;

using Chaos.Mongo.EventStore.Errors;

public sealed class BookCreatedEvent : Event<BookAggregate>
{
    public String Author { get; set; } = String.Empty;
    public Dictionary<String, String> Metadata { get; set; } = [];
    public String Name { get; set; } = String.Empty;
    public Int32 ReleaseYear { get; set; }

    public override void Execute(BookAggregate aggregate)
    {
        aggregate.Id = AggregateId;
        aggregate.Name = Name;
        aggregate.Author = Author;
        aggregate.ReleaseYear = ReleaseYear;
        aggregate.Metadata = new(Metadata, StringComparer.Ordinal);
        aggregate.Editions = [];
        aggregate.IsDeleted = false;
    }
}

public sealed class BookMetadataSetEvent : Event<BookAggregate>
{
    public Dictionary<String, String> Metadata { get; set; } = [];

    public override void Execute(BookAggregate aggregate)
    {
        if (aggregate.IsDeleted)
        {
            throw new MongoEventValidationException("Deleted books cannot be updated.");
        }

        foreach (var (key, value) in Metadata)
        {
            aggregate.Metadata[key] = value;
        }
    }
}

public sealed class BookUpdatedEvent : Event<BookAggregate>
{
    public String? Author { get; set; }
    public String? Name { get; set; }
    public Int32? ReleaseYear { get; set; }

    public override void Execute(BookAggregate aggregate)
    {
        if (aggregate.IsDeleted)
        {
            throw new MongoEventValidationException("Deleted books cannot be updated.");
        }

        if (Name is not null)
        {
            aggregate.Name = Name;
        }

        if (Author is not null)
        {
            aggregate.Author = Author;
        }

        if (ReleaseYear.HasValue)
        {
            aggregate.ReleaseYear = ReleaseYear.Value;
        }
    }
}

public sealed class BookEditionAddedEvent : Event<BookAggregate>
{
    public Guid EditionId { get; set; }
    public String Name { get; set; } = String.Empty;
    public Int32 ReleaseYear { get; set; }

    public override void Execute(BookAggregate aggregate)
    {
        if (aggregate.IsDeleted)
        {
            throw new MongoEventValidationException("Deleted books cannot be updated.");
        }

        if (aggregate.Editions.Any(x => x.Id == EditionId))
        {
            throw new MongoEventValidationException("Edition already exists.");
        }

        aggregate.Editions.Add(new()
        {
            Id = EditionId,
            Name = Name,
            ReleaseYear = ReleaseYear
        });
    }
}

public sealed class BookEditionUpdatedEvent : Event<BookAggregate>
{
    public Guid EditionId { get; set; }
    public String? Name { get; set; }
    public Int32? ReleaseYear { get; set; }

    public override void Execute(BookAggregate aggregate)
    {
        if (aggregate.IsDeleted)
        {
            throw new MongoEventValidationException("Deleted books cannot be updated.");
        }

        var edition = aggregate.Editions.FirstOrDefault(x => x.Id == EditionId);
        if (edition is null)
        {
            throw new MongoEventValidationException("Edition does not exist.");
        }

        if (Name is not null)
        {
            edition.Name = Name;
        }

        if (ReleaseYear.HasValue)
        {
            edition.ReleaseYear = ReleaseYear.Value;
        }
    }
}

public sealed class BookDeletedEvent : Event<BookAggregate>
{
    public override void Execute(BookAggregate aggregate)
    {
        if (aggregate.IsDeleted)
        {
            throw new MongoEventValidationException("Book is already deleted.");
        }

        aggregate.IsDeleted = true;
    }
}
