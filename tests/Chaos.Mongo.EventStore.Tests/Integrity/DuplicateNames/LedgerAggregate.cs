// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity.DuplicateNames;

/// <summary>
/// An aggregate that shares its simple name with <see cref="Integration.LedgerAggregate"/>, to test that protected
/// aggregate types must have unique names.
/// </summary>
public class LedgerAggregate : Aggregate
{
    public Int64 EntryCount { get; set; }
}

public class DuplicateLedgerEntryRecordedEvent : Event<LedgerAggregate>
{
    public override void Execute(LedgerAggregate aggregate) => aggregate.EntryCount++;
}
