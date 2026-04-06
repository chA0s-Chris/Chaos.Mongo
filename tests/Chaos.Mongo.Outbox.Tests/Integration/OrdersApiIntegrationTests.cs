// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests.Integration;

using Chaos.Mongo.Outbox.Tests.OrdersApi;
using FluentAssertions;
using NUnit.Framework;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Testcontainers.MongoDb;

public class OrdersApiIntegrationTests
{
    private HttpClient? _client;
    private MongoDbContainer _container;
    private OrdersApiFactory? _factory;

    private HttpClient Client => _client ?? throw new InvalidOperationException("HTTP client is not initialized.");

    [Test]
    public async Task CancelAfterShip_ReturnsConflict()
    {
        var createResponse = await Client.PostAsJsonAsync("/orders", new PlaceOrderRequest
        {
            CustomerName = "Carol",
            Items =
            [
                new()
                {
                    ProductName = "The Pragmatic Programmer",
                    Quantity = 1,
                    Price = 40.00m
                }
            ]
        });

        var order = await createResponse.Content.ReadFromJsonAsync<OrderResponse>();

        await Client.PostAsync($"/orders/{order!.Id}/ship", null);

        var cancelResponse = await Client.PostAsJsonAsync(
            $"/orders/{order.Id}/cancel",
            new CancelOrderRequest
            {
                Reason = "Too late"
            });

        cancelResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task GetOrder_AfterPlacement_ReturnsOrder()
    {
        var createResponse = await Client.PostAsJsonAsync("/orders", new PlaceOrderRequest
        {
            CustomerName = "Eve",
            Items =
            [
                new()
                {
                    ProductName = "Designing Data-Intensive Applications",
                    Quantity = 1,
                    Price = 42.00m
                }
            ]
        });

        var created = await createResponse.Content.ReadFromJsonAsync<OrderResponse>();

        var getResponse = await Client.GetAsync($"/orders/{created!.Id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var order = await getResponse.Content.ReadFromJsonAsync<OrderResponse>();
        order.Should().NotBeNull();
        order.CustomerName.Should().Be("Eve");
        order.Status.Should().Be("Placed");
    }

    [Test]
    public async Task GetOrder_NonExistent_ReturnsNotFound()
    {
        var getResponse = await Client.GetAsync("/orders/507f1f77bcf86cd799439011");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task OrderLifecycle_PlaceAndCancel_DeliversBothNotifications()
    {
        var createResponse = await Client.PostAsJsonAsync("/orders", new PlaceOrderRequest
        {
            CustomerName = "Bob",
            Items =
            [
                new()
                {
                    ProductName = "Refactoring",
                    Quantity = 1,
                    Price = 50.00m
                }
            ]
        });

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdOrder = await createResponse.Content.ReadFromJsonAsync<OrderResponse>();

        var cancelResponse = await Client.PostAsJsonAsync(
            $"/orders/{createdOrder!.Id}/cancel",
            new CancelOrderRequest
            {
                Reason = "Changed my mind"
            });

        cancelResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var cancelledOrder = await cancelResponse.Content.ReadFromJsonAsync<OrderResponse>();
        cancelledOrder!.Status.Should().Be("Cancelled");

        await WaitForNotificationsAsync(2);

        var notificationsResponse = await Client.GetAsync("/notifications");
        var notifications = await notificationsResponse.Content.ReadFromJsonAsync<List<NotificationResponse>>();

        notifications.Should().Contain(n => n.Type == "OrderPlaced");
        notifications.Should().Contain(n => n.Type == "OrderCancelled");
    }

    [Test]
    public async Task OrderLifecycle_PlaceAndShip_DeliversBothNotifications()
    {
        var createResponse = await Client.PostAsJsonAsync("/orders", new PlaceOrderRequest
        {
            CustomerName = "Alice",
            Items =
            [
                new()
                {
                    ProductName = "Domain-Driven Design",
                    Quantity = 1,
                    Price = 45.00m
                },
                new()
                {
                    ProductName = "Clean Architecture",
                    Quantity = 2,
                    Price = 35.00m
                }
            ]
        });

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var createdOrder = await createResponse.Content.ReadFromJsonAsync<OrderResponse>();
        createdOrder.Should().NotBeNull();
        createdOrder.CustomerName.Should().Be("Alice");
        createdOrder.Status.Should().Be("Placed");
        createdOrder.Items.Should().HaveCount(2);

        var shipResponse = await Client.PostAsync($"/orders/{createdOrder.Id}/ship", null);
        shipResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var shippedOrder = await shipResponse.Content.ReadFromJsonAsync<OrderResponse>();
        shippedOrder.Should().NotBeNull();
        shippedOrder.Status.Should().Be("Shipped");

        await WaitForNotificationsAsync(2);

        var notificationsResponse = await Client.GetAsync("/notifications");
        var notifications = await notificationsResponse.Content.ReadFromJsonAsync<List<NotificationResponse>>();

        notifications.Should().NotBeNull();
        notifications.Should().Contain(n => n.Type == "OrderPlaced");
        notifications.Should().Contain(n => n.Type == "OrderShipped");
    }

    [Test]
    public async Task PlaceOrder_InvalidRequest_ReturnsBadRequest()
    {
        var response = await Client.PostAsJsonAsync("/orders", new PlaceOrderRequest
        {
            CustomerName = "",
            Items = []
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [SetUp]
    public void Setup()
    {
        Environment.SetEnvironmentVariable(OrdersApiFactory.ContentRootEnvironmentVariable, OrdersApiFactory.GetRepositoryRootPath());
        _factory = new(_container.GetConnectionString());
        _client = _factory.CreateClient();
    }

    [Test]
    public async Task ShipAfterCancel_ReturnsConflict()
    {
        var createResponse = await Client.PostAsJsonAsync("/orders", new PlaceOrderRequest
        {
            CustomerName = "Dave",
            Items =
            [
                new()
                {
                    ProductName = "Patterns of Enterprise Application Architecture",
                    Quantity = 1,
                    Price = 55.00m
                }
            ]
        });

        var order = await createResponse.Content.ReadFromJsonAsync<OrderResponse>();

        await Client.PostAsJsonAsync(
            $"/orders/{order!.Id}/cancel",
            new CancelOrderRequest
            {
                Reason = "Out of stock"
            });

        var shipResponse = await Client.PostAsync($"/orders/{order.Id}/ship", null);
        shipResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [OneTimeSetUp]
    public async Task StartContainer() => _container = await MongoDbTestContainer.StartContainerAsync();

    [TearDown]
    public void TearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
        Environment.SetEnvironmentVariable(OrdersApiFactory.ContentRootEnvironmentVariable, null);
    }

    private async Task WaitForNotificationsAsync(Int32 expectedCount, TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(10);
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < timeout)
        {
            var response = await Client.GetAsync("/notifications");
            var notifications = await response.Content.ReadFromJsonAsync<List<NotificationResponse>>();

            if (notifications is not null && notifications.Count >= expectedCount)
                return;

            await Task.Delay(100);
        }

        throw new TimeoutException($"Expected {expectedCount} notifications within {timeout}, but timed out.");
    }
}
