// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.BooksApi;

public sealed class BookAggregate : Aggregate
{
    public String Author { get; set; } = String.Empty;
    public List<BookEdition> Editions { get; set; } = [];
    public Boolean IsDeleted { get; set; }
    public Dictionary<String, String> Metadata { get; set; } = [];
    public String Name { get; set; } = String.Empty;
    public Int32 ReleaseYear { get; set; }
}

public sealed class BookEdition
{
    public Guid Id { get; set; }
    public String Name { get; set; } = String.Empty;
    public Int32 ReleaseYear { get; set; }
}
