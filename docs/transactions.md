# Transactions

Chaos.Mongo provides helpers for executing MongoDB operations in a transaction and for detecting deployments where transactions are unavailable.

MongoDB transactions require a replica set or sharded deployment. They are not available on standalone servers.

## Execute in a transaction

`ExecuteInTransaction` starts a session, commits successful work, and aborts when the callback throws:

```csharp
public sealed class OrderService(IMongoHelper mongo)
{
    public Task<Order> CreateOrderAsync(
        Order order,
        Payment payment,
        CancellationToken cancellationToken = default)
    {
        return mongo.ExecuteInTransaction(async (helper, session, ct) =>
        {
            var orders = helper.GetCollection<Order>();
            await orders.InsertOneAsync(session, order, cancellationToken: ct);

            var payments = helper.GetCollection<Payment>();
            await payments.InsertOneAsync(session, payment, cancellationToken: ct);

            var products = helper.GetCollection<Product>();
            await products.UpdateOneAsync(
                session,
                product => product.Id == order.ProductId,
                Builders<Product>.Update.Inc(product => product.Stock, -order.Quantity),
                cancellationToken: ct);

            return order;
        }, cancellationToken: cancellationToken);
    }
}
```

Every operation that must be atomic needs to receive the callback's `session`.

## Try to start a transaction

Use `TryStartTransactionAsync` when an operation can deliberately fall back to non-transactional behavior:

```csharp
public async Task ProcessAsync(CancellationToken cancellationToken = default)
{
    using var session = await mongo.TryStartTransactionAsync(cancellationToken: cancellationToken);

    if (session is null)
    {
        await DoWorkAsync(session: null, cancellationToken);
        return;
    }

    try
    {
        await DoWorkAsync(session, cancellationToken);
        await session.CommitTransactionAsync(cancellationToken);
    }
    catch
    {
        await session.AbortTransactionAsync(cancellationToken);
        throw;
    }
}
```

Only use a non-transactional fallback when partial completion is acceptable. Features such as the [Transactional Outbox](transactional-outbox.md) require transaction support to provide their guarantees.
