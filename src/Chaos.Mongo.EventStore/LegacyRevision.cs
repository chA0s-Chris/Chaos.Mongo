// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore;

/// <summary>
/// Normalizes documents stored before aggregate revisions existed. A non-empty stream always has a
/// revision of at least one, so <c>Revision == 0</c> with <c>Version &gt; 0</c> identifies exactly these
/// documents. They contain only state-changing events, so their revision equals their position.
/// </summary>
/// <remarks>
/// Normalization is in-memory only. Stored documents are never rewritten: adding an element to a sealed
/// event would change its hashed bytes and break the integrity chain.
/// </remarks>
internal static class LegacyRevision
{
    public static void Normalize(IAggregate aggregate)
        => aggregate.Revision = Of(aggregate.Revision, aggregate.Version);

    public static void Normalize<TAggregate>(Event<TAggregate> @event)
        where TAggregate : class, IAggregate, new()
        => @event.Revision = Of(@event.Revision, @event.Version);

    public static void Normalize<TAggregate>(CheckpointDocument<TAggregate> checkpoint)
        where TAggregate : class, IAggregate, new()
    {
        checkpoint.Revision = Of(checkpoint.Revision, checkpoint.Id.Version);
        if (checkpoint.State.Revision == 0 && checkpoint.State.Version > 0)
        {
            checkpoint.State.Revision = checkpoint.Revision;
        }
    }

    public static Int64 Of(Int64 revision, Int64 version)
        => revision == 0 && version > 0 ? version : revision;
}
