// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests.OrdersApi;

using MongoDB.Bson;

public class OrderPlacedMessage
{
    public String CustomerName { get; set; } = String.Empty;
    public ObjectId OrderId { get; set; }
    public Decimal TotalAmount { get; set; }
}

public class OrderShippedMessage
{
    public String CustomerName { get; set; } = String.Empty;
    public ObjectId OrderId { get; set; }
}

public class OrderCancelledMessage
{
    public String CustomerName { get; set; } = String.Empty;
    public ObjectId OrderId { get; set; }
    public String Reason { get; set; } = String.Empty;
}
