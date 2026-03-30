// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

public class OrderAggregate : Aggregate
{
    public String CustomerName { get; set; } = String.Empty;
    public String Status { get; set; } = String.Empty;
    public Decimal TotalAmount { get; set; }
}

public class OrderCreatedEvent : Event<OrderAggregate>
{
    public String CustomerName { get; set; } = String.Empty;
    public Decimal TotalAmount { get; set; }

    public override void Execute(OrderAggregate aggregate)
    {
        aggregate.CustomerName = CustomerName;
        aggregate.TotalAmount = TotalAmount;
        aggregate.Status = "Created";
    }
}

public class OrderShippedEvent : Event<OrderAggregate>
{
    public override void Execute(OrderAggregate aggregate)
    {
        aggregate.Status = "Shipped";
    }
}

public class OrderCompletedEvent : Event<OrderAggregate>
{
    public override void Execute(OrderAggregate aggregate)
    {
        aggregate.Status = "Completed";
    }
}

public class OrderCancelledEvent : Event<OrderAggregate>
{
    public override void Execute(OrderAggregate aggregate)
    {
        if (aggregate.Status == "Shipped")
        {
            throw new Errors.MongoEventValidationException(
                "Cannot cancel an order that has already been shipped.");
        }

        aggregate.Status = "Cancelled";
    }
}

public class OutboxMessage
{
    public Guid AggregateId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public Guid Id { get; set; }
    public String MessageType { get; set; } = String.Empty;
}
