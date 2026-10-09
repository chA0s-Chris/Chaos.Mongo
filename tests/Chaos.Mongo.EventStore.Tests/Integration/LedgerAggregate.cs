// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

public class LedgerAggregate : Aggregate
{
    public Decimal Balance { get; set; }
    public Int64 EntryCount { get; set; }
}

public class LedgerEntryRecordedEvent : Event<LedgerAggregate>
{
    public Decimal Amount { get; set; }
    public Int64 Counter { get; set; }
    public LedgerEntryDetails Details { get; set; } = new();
    public Guid Reference { get; set; }

    public override void Execute(LedgerAggregate aggregate)
    {
        aggregate.Balance += Amount;
        aggregate.EntryCount++;
    }
}

public class LedgerEntryDetails
{
    public String Category { get; set; } = String.Empty;
    public Double Ratio { get; set; }
    public List<String> Tags { get; set; } = [];
}
