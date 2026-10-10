// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.LoadTests;

internal sealed class LoadTestAdjustedEvent : Event<LoadTestAggregate>
{
    public Decimal AmountDelta { get; set; }

    public static LoadTestAdjustedEvent Create(Guid aggregateId, Int64 version)
        => new()
        {
            Id = Guid.CreateVersion7(),
            AggregateId = aggregateId,
            Version = version,
            AmountDelta = version
        };

    public override void Execute(LoadTestAggregate aggregate)
        => aggregate.TotalAmount += AmountDelta;
}
