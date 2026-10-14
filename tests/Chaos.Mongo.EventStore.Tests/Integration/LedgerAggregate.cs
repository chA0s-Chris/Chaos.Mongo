// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Errors;

public class LedgerAggregate : Aggregate
{
    public Decimal Balance { get; set; }
    public Int64 EntryCount { get; set; }
    public List<String?> Memos { get; set; } = [];
}

public class LedgerEntryRecordedEvent : Event<LedgerAggregate>
{
    /// <summary>
    /// The category of an entry whose replay fails once its memo is erased.
    /// </summary>
    public const String RequiresMemoCategory = "requires-memo";

    public Decimal Amount { get; set; }
    public Int64 Counter { get; set; }
    public LedgerCounterparty Counterparty { get; set; } = new();
    public LedgerEntryDetails Details { get; set; } = new();
    public String? Memo { get; set; }
    public Guid Reference { get; set; }

    public override void Execute(LedgerAggregate aggregate)
    {
        if (Memo is null && Details.Category == RequiresMemoCategory)
        {
            throw new MongoEventValidationException("The entry requires a memo.");
        }

        aggregate.Balance += Amount;
        aggregate.EntryCount++;
        aggregate.Memos.Add(Memo);
    }
}

public class LedgerAuditedEvent : ObservationalEvent<LedgerAggregate>
{
    public String Auditor { get; set; } = String.Empty;
}

public class LedgerCounterparty
{
    public String? Account { get; set; }
    public String? Name { get; set; }
}

public class LedgerEntryDetails
{
    public String Category { get; set; } = String.Empty;
    public Double Ratio { get; set; }
    public List<String> Tags { get; set; } = [];
}
