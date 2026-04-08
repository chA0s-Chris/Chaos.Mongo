// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests.OrdersApi;

using MongoDB.Bson;

public sealed class Order
{
    public DateTime CreatedUtc { get; set; }
    public String CustomerName { get; set; } = String.Empty;
    public ObjectId Id { get; set; }
    public List<OrderItem> Items { get; set; } = [];
    public OrderStatus Status { get; set; }
}

public sealed class OrderItem
{
    public Decimal Price { get; set; }
    public String ProductName { get; set; } = String.Empty;
    public Int32 Quantity { get; set; }
}

public enum OrderStatus
{
    Placed,
    Shipped,
    Cancelled
}
