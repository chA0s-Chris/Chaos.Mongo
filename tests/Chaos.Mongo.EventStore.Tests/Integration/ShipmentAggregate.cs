// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

/// <summary>
/// A second protected aggregate type, so tests can cover code that spans all protected types.
/// </summary>
public class ShipmentAggregate : Aggregate
{
    public Int64 DispatchCount { get; set; }
}

public class ShipmentDispatchedEvent : Event<ShipmentAggregate>
{
    public String Carrier { get; set; } = String.Empty;

    public static ShipmentDispatchedEvent Create(Guid aggregateId, Int64 version)
        => new()
        {
            Id = Guid.NewGuid(),
            AggregateId = aggregateId,
            Version = version,
            Carrier = "carrier"
        };

    public override void Execute(ShipmentAggregate aggregate) => aggregate.DispatchCount++;
}
