// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests.OrdersApi;

public sealed class PlaceOrderRequest
{
    public String CustomerName { get; set; } = String.Empty;
    public List<OrderItemRequest> Items { get; set; } = [];
}

public sealed class OrderItemRequest
{
    public Decimal Price { get; set; }
    public String ProductName { get; set; } = String.Empty;
    public Int32 Quantity { get; set; }
}

public sealed class CancelOrderRequest
{
    public String Reason { get; set; } = String.Empty;
}

public sealed class OrderResponse
{
    public DateTime CreatedUtc { get; set; }
    public String CustomerName { get; set; } = String.Empty;
    public String Id { get; set; } = String.Empty;
    public List<OrderItemResponse> Items { get; set; } = [];
    public String Status { get; set; } = String.Empty;

    public static OrderResponse FromOrder(Order order)
    {
        return new()
        {
            Id = order.Id.ToString(),
            CustomerName = order.CustomerName,
            Status = order.Status.ToString(),
            CreatedUtc = order.CreatedUtc,
            Items = order.Items
                         .Select(i => new OrderItemResponse
                         {
                             ProductName = i.ProductName,
                             Quantity = i.Quantity,
                             Price = i.Price
                         })
                         .ToList()
        };
    }
}

public sealed class OrderItemResponse
{
    public Decimal Price { get; set; }
    public String ProductName { get; set; } = String.Empty;
    public Int32 Quantity { get; set; }
}

public sealed class NotificationResponse
{
    public String? CorrelationId { get; set; }
    public DateTime DeliveredUtc { get; set; }
    public String Type { get; set; } = String.Empty;
}
