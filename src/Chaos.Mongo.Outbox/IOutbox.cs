// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox;

using MongoDB.Driver;

/// <summary>
/// Provides the ability to write typed outbox messages within a MongoDB transaction.
/// </summary>
/// <remarks>
/// The outbox guarantees that messages are persisted atomically within the same MongoDB transaction
/// as the originating write operation. An active transaction is required; the outbox will throw
/// if used without one.
/// </remarks>
public interface IOutbox
{
    /// <summary>
    /// Adds a typed message to the outbox within the caller's MongoDB transaction.
    /// </summary>
    /// <typeparam name="TPayload">
    /// The payload type. Must be registered via the builder API using <c>WithMessage&lt;TPayload&gt;()</c>.
    /// </typeparam>
    /// <param name="session">
    /// The caller's client session handle with an active transaction.
    /// The insert participates in this transaction.
    /// </param>
    /// <param name="payload">The message payload to store in the outbox.</param>
    /// <param name="correlationId">Optional correlation identifier for tracing across systems.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the message has been inserted.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="session"/> or <paramref name="payload"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the session does not have an active transaction, or when <typeparamref name="TPayload"/>
    /// is not a registered payload type.
    /// </exception>
    Task AddMessageAsync<TPayload>(IClientSessionHandle session,
                                   TPayload payload,
                                   String? correlationId = null,
                                   CancellationToken cancellationToken = default)
        where TPayload : class, new();
}
