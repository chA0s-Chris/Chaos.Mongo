// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

using Chaos.Mongo.EventStore.Errors;

/// <summary>
/// Abstract base class for events that document something about an aggregate without changing it,
/// such as a read access.
/// </summary>
/// <remarks>
///     <para>
///     Observational events take a position in the ordered, hash-chained stream like every other event,
///     but they neither change the aggregate's state nor its <see cref="IAggregate.Revision"/> and
///     <see cref="IAggregate.Version"/>. Appending only observational events writes neither the read model
///     nor a checkpoint, and replay skips them.
///     </para>
///     <para>
///     An observational event must observe an existing aggregate: appending one to a stream without a
///     preceding state-changing event fails with <see cref="MongoEventValidationException"/>.
///     </para>
/// </remarks>
/// <typeparam name="TAggregate">The aggregate type this event observes.</typeparam>
public abstract class ObservationalEvent<TAggregate> : Event<TAggregate>
    where TAggregate : class, IAggregate, new()
{
    /// <summary>
    /// Validates this event against the observed aggregate without changing it.
    /// </summary>
    /// <param name="aggregate">The observed aggregate.</param>
    public sealed override void Execute(TAggregate aggregate) => Validate(aggregate);

    /// <summary>
    /// Validates that the observed aggregate permits this event. The default implementation accepts every
    /// aggregate. Implementations must not modify the aggregate.
    /// </summary>
    /// <param name="aggregate">The observed aggregate.</param>
    /// <exception cref="MongoEventValidationException">
    /// Thrown by implementations when the aggregate's state does not permit the event.
    /// </exception>
    protected virtual void Validate(TAggregate aggregate) { }
}
