// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests.OrdersApi;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;

public sealed class OrdersApiFactory : WebApplicationFactory<OrdersApiEntryPoint>
{
    public const String ContentRootEnvironmentVariable = "ASPNETCORE_TEST_CONTENTROOT_CHAOS_MONGO_OUTBOX_TESTS";
    public const String OrdersCollectionName = "Orders";

    private readonly String _connectionString;
    private readonly String _databaseName;

    public OrdersApiFactory(String connectionString)
    {
        _connectionString = connectionString;
        _databaseName = $"OrdersApiTestDb_{Guid.NewGuid():N}";
    }

    public static String GetRepositoryRootPath()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var appBuilder = WebApplication.CreateBuilder();
        appBuilder.WebHost.UseTestServer();

        appBuilder.Services
                  .AddMongo(_connectionString, configure: options =>
                  {
                      options.DefaultDatabase = _databaseName;
                      options.RunConfiguratorsOnStartup = true;
                  })
                  .WithOutbox(o => o
                                   .WithPublisher<TestNotificationPublisher>(ServiceLifetime.Singleton)
                                   .WithMessage<OrderPlacedMessage>("OrderPlaced")
                                   .WithMessage<OrderShippedMessage>("OrderShipped")
                                   .WithMessage<OrderCancelledMessage>("OrderCancelled")
                                   .WithMaxRetries(3)
                                   .WithPollingInterval(TimeSpan.FromMilliseconds(200))
                                   .WithLockTimeout(TimeSpan.FromSeconds(10))
                                   .WithAutoStartProcessor());

        var app = appBuilder.Build();

        app.MapPost(
            "/orders",
            async (
                PlaceOrderRequest request,
                IMongoHelper mongoHelper,
                IOutbox outbox,
                CancellationToken cancellationToken) =>
            {
                if (String.IsNullOrWhiteSpace(request.CustomerName) || request.Items.Count == 0)
                {
                    return Results.BadRequest("Customer name and at least one item are required.");
                }

                var order = new Order
                {
                    Id = ObjectId.GenerateNewId(),
                    CustomerName = request.CustomerName,
                    Status = OrderStatus.Placed,
                    CreatedUtc = DateTime.UtcNow,
                    Items = request.Items
                                   .Select(i => new OrderItem
                                   {
                                       ProductName = i.ProductName,
                                       Quantity = i.Quantity,
                                       Price = i.Price
                                   })
                                   .ToList()
                };

                var totalAmount = order.Items.Sum(i => i.Price * i.Quantity);

                using var session = await mongoHelper.Client.StartSessionAsync(cancellationToken: cancellationToken);
                session.StartTransaction();

                var ordersCollection = mongoHelper.Database.GetCollection<Order>(OrdersCollectionName);
                await ordersCollection.InsertOneAsync(session, order, cancellationToken: cancellationToken);

                await outbox.AddMessageAsync(session, new OrderPlacedMessage
                {
                    OrderId = order.Id,
                    CustomerName = order.CustomerName,
                    TotalAmount = totalAmount
                }, order.Id.ToString(), cancellationToken);

                await session.CommitTransactionAsync(cancellationToken);

                return Results.Created($"/orders/{order.Id}", OrderResponse.FromOrder(order));
            });

        app.MapGet(
            "/orders/{orderId}",
            async (
                String orderId,
                IMongoHelper mongoHelper,
                CancellationToken cancellationToken) =>
            {
                if (!ObjectId.TryParse(orderId, out var id))
                {
                    return Results.BadRequest("Invalid order ID.");
                }

                var collection = mongoHelper.Database.GetCollection<Order>(OrdersCollectionName);
                var order = await collection.Find(o => o.Id == id).FirstOrDefaultAsync(cancellationToken);

                return order is null ? Results.NotFound() : Results.Ok(OrderResponse.FromOrder(order));
            });

        app.MapPost(
            "/orders/{orderId}/ship",
            async (
                String orderId,
                IMongoHelper mongoHelper,
                IOutbox outbox,
                CancellationToken cancellationToken) =>
            {
                if (!ObjectId.TryParse(orderId, out var id))
                {
                    return Results.BadRequest("Invalid order ID.");
                }

                var collection = mongoHelper.Database.GetCollection<Order>(OrdersCollectionName);
                var order = await collection.Find(o => o.Id == id).FirstOrDefaultAsync(cancellationToken);

                if (order is null)
                {
                    return Results.NotFound();
                }

                if (order.Status != OrderStatus.Placed)
                {
                    return Results.Conflict($"Order cannot be shipped because it is {order.Status}.");
                }

                using var session = await mongoHelper.Client.StartSessionAsync(cancellationToken: cancellationToken);
                session.StartTransaction();

                var updateResult = await collection.UpdateOneAsync(
                    session,
                    o => o.Id == id && o.Status == OrderStatus.Placed,
                    Builders<Order>.Update.Set(o => o.Status, OrderStatus.Shipped),
                    cancellationToken: cancellationToken);

                if (updateResult.ModifiedCount == 0)
                {
                    await session.AbortTransactionAsync(cancellationToken);
                    return Results.Conflict("Order was modified concurrently.");
                }

                await outbox.AddMessageAsync(session, new OrderShippedMessage
                {
                    OrderId = order.Id,
                    CustomerName = order.CustomerName
                }, order.Id.ToString(), cancellationToken);

                await session.CommitTransactionAsync(cancellationToken);

                order.Status = OrderStatus.Shipped;
                return Results.Ok(OrderResponse.FromOrder(order));
            });

        app.MapPost(
            "/orders/{orderId}/cancel",
            async (
                String orderId,
                CancelOrderRequest request,
                IMongoHelper mongoHelper,
                IOutbox outbox,
                CancellationToken cancellationToken) =>
            {
                if (!ObjectId.TryParse(orderId, out var id))
                {
                    return Results.BadRequest("Invalid order ID.");
                }

                if (String.IsNullOrWhiteSpace(request.Reason))
                {
                    return Results.BadRequest("Cancellation reason is required.");
                }

                var collection = mongoHelper.Database.GetCollection<Order>(OrdersCollectionName);
                var order = await collection.Find(o => o.Id == id).FirstOrDefaultAsync(cancellationToken);

                if (order is null)
                {
                    return Results.NotFound();
                }

                if (order.Status != OrderStatus.Placed)
                {
                    return Results.Conflict($"Order cannot be cancelled because it is {order.Status}.");
                }

                using var session = await mongoHelper.Client.StartSessionAsync(cancellationToken: cancellationToken);
                session.StartTransaction();

                var updateResult = await collection.UpdateOneAsync(
                    session,
                    o => o.Id == id && o.Status == OrderStatus.Placed,
                    Builders<Order>.Update.Set(o => o.Status, OrderStatus.Cancelled),
                    cancellationToken: cancellationToken);

                if (updateResult.ModifiedCount == 0)
                {
                    await session.AbortTransactionAsync(cancellationToken);
                    return Results.Conflict("Order was modified concurrently.");
                }

                await outbox.AddMessageAsync(session, new OrderCancelledMessage
                {
                    OrderId = order.Id,
                    CustomerName = order.CustomerName,
                    Reason = request.Reason
                }, order.Id.ToString(), cancellationToken);

                await session.CommitTransactionAsync(cancellationToken);

                order.Status = OrderStatus.Cancelled;
                return Results.Ok(OrderResponse.FromOrder(order));
            });

        app.MapGet(
            "/notifications",
            (IOutboxPublisher publisher) =>
            {
                var testPublisher = (TestNotificationPublisher)publisher;
                var notifications = testPublisher.DeliveredMessages
                                                 .Select(m => new NotificationResponse
                                                 {
                                                     Type = m.Type,
                                                     CorrelationId = m.CorrelationId,
                                                     DeliveredUtc = m.ProcessedUtc ?? DateTime.UtcNow
                                                 })
                                                 .ToList();

                return Results.Ok(notifications);
            });

        app.Start();
        return app;
    }
}
