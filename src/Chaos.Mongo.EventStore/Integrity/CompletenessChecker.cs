// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using MongoDB.Bson;
using MongoDB.Driver;
using System.Runtime.CompilerServices;

/// <summary>
/// Checks that every event of an aggregate type is covered by a completed anchor whose leaf for its stream has a
/// version at or above the event's, and that every anchored stream still exists up to its anchored version.
/// </summary>
/// <remarks>
/// An explicit audit operation: it walks the streams in aggregate identifier order, reading each event's version,
/// seal mode and creation time through a projection, and merges this walk with the type's leaves in completed anchors,
/// so leaves without events reveal deleted streams. Its cost grows with events and leaves. Findings are streamed, so
/// memory grows with the largest stream and with the number of completed anchors, which are held as one small entry
/// each, about 8,600 per month at the default anchoring interval. The chain of anchors is verified first, because
/// deleted-stream detection relies on it. Event documents whose aggregate identifier or version cannot be read are
/// reported as <see cref="CompletenessFindingKind.MalformedEvent"/>.
/// </remarks>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
internal sealed class CompletenessChecker<TAggregate> where TAggregate : class, IAggregate, new()
{
    private readonly String _aggregateTypeName = typeof(TAggregate).Name;
    private readonly AnchorVerifier? _anchors;
    private readonly IMongoHelper _mongoHelper;
    private readonly MongoEventStoreOptions<TAggregate> _options;
    private readonly TimeProvider _timeProvider;

    public CompletenessChecker(IMongoHelper mongoHelper,
                               MongoEventStoreOptions<TAggregate> options,
                               AnchorVerifier? anchors,
                               TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(mongoHelper);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _mongoHelper = mongoHelper;
        _options = options;
        _anchors = anchors;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Runs the completeness check over all streams of the aggregate type.
    /// </summary>
    /// <param name="trustedHead">
    /// An anchor kept outside the database that the chain of anchors must contain, or <c>null</c> to check its
    /// consistency only.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The findings: a broken chain of anchors first, then the findings of each stream in identifier order.</returns>
    /// <exception cref="InvalidOperationException">Thrown when anchors are not configured.</exception>
    public async IAsyncEnumerable<CompletenessFinding> CheckAsync(TrustedAnchorHead? trustedHead = null,
                                                                  [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var anchors = _anchors ?? throw new InvalidOperationException(
            "The completeness check needs anchors; configure integrity anchoring or verification at module level.");

        var chain = await anchors.VerifyAnchorChainAsync(trustedHead, cancellationToken);
        if (!chain.IsIntact)
        {
            yield return new CompletenessFinding(CompletenessFindingKind.AnchorChainBroken, null, null, null, null, null, null, chain);
        }

        var completedAnchors = await anchors.Store.ReadCompletedAnchorReferencesAsync(cancellationToken);
        await using var leaves = new LeafReader(anchors.Store.StreamLeavesOfTypeAsync(_aggregateTypeName, cancellationToken), completedAnchors);
        await leaves.InitializeAsync();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var malformedEvents = new List<CompletenessFinding>();

        await foreach (var (aggregateId, events) in ReadStreamsAsync(malformedEvents, cancellationToken))
        {
            foreach (var finding in malformedEvents)
            {
                yield return finding;
            }

            malformedEvents.Clear();

            // Streams that only appear in leaves come first in identifier order: every event of them was deleted.
            while (leaves.Current is { } leaf && GuidOrder.Compare(leaf.AggregateId, aggregateId) < 0)
            {
                foreach (var finding in CheckStream(leaf.AggregateId, [], await leaves.TakeStreamAsync(leaf.AggregateId), now))
                {
                    yield return finding;
                }
            }

            var streamLeaves = leaves.Current?.AggregateId == aggregateId ? await leaves.TakeStreamAsync(aggregateId) : [];
            foreach (var finding in CheckStream(aggregateId, events, streamLeaves, now))
            {
                yield return finding;
            }
        }

        foreach (var finding in malformedEvents)
        {
            yield return finding;
        }

        while (leaves.Current is { } remaining)
        {
            foreach (var finding in CheckStream(remaining.AggregateId, [], await leaves.TakeStreamAsync(remaining.AggregateId), now))
            {
                yield return finding;
            }
        }
    }

    /// <summary>
    /// Assigns each version its sealing anchor: the completed anchor with the lowest sequence number whose leaf has a
    /// version at or above it.
    /// </summary>
    private static AnchorReference?[] AssignSealingAnchors(IReadOnlyList<EventSummary> events, IReadOnlyList<StreamLeaf> leaves)
    {
        var byVersionDescending = leaves.OrderByDescending(leaf => leaf.Version).ToList();
        var anchors = new AnchorReference?[events.Count];
        AnchorReference? best = null;
        var next = 0;
        for (var index = events.Count - 1; index >= 0; index--)
        {
            while (next < byVersionDescending.Count && byVersionDescending[next].Version >= events[index].Version)
            {
                var candidate = byVersionDescending[next++].Anchor;
                best = best is null || candidate.Sequence < best.Sequence ? candidate : best;
            }

            anchors[index] = best;
        }

        return anchors;
    }

    private static IEnumerable<CompletenessFinding> CheckStream(Guid aggregateId,
                                                                IReadOnlyList<EventSummary> events,
                                                                IReadOnlyList<StreamLeaf> leaves,
                                                                DateTime now)
    {
        var head = events.Count == 0 ? 0 : events[^1].Version;
        var highest = leaves.OrderByDescending(leaf => leaf.Version).ThenBy(leaf => leaf.Anchor.Sequence).FirstOrDefault();
        var anchoredVersion = highest?.Version ?? 0;

        if (highest is not null && anchoredVersion > head)
        {
            yield return new CompletenessFinding(head == 0 ? CompletenessFindingKind.StreamDeleted : CompletenessFindingKind.StreamTruncated,
                                                 aggregateId,
                                                 head + 1,
                                                 anchoredVersion,
                                                 head,
                                                 null,
                                                 highest.Anchor,
                                                 null);
        }

        if (events.FirstOrDefault(e => !e.IsSealed) is { } firstUnsealed)
        {
            yield return new CompletenessFinding(CompletenessFindingKind.Unsealed, aggregateId, firstUnsealed.Version, head, head, null, null, null);
        }

        if (events.FirstOrDefault(e => e.Version > anchoredVersion) is { } firstUnanchored)
        {
            var age = firstUnanchored.CreatedUtc is { } createdUtc ? now - createdUtc : (TimeSpan?)null;
            yield return new CompletenessFinding(CompletenessFindingKind.Unanchored, aggregateId, firstUnanchored.Version, head, head, age, null, null);
        }

        foreach (var range in FindRetroactiveRanges(events, AssignSealingAnchors(events, leaves)))
        {
            yield return new CompletenessFinding(CompletenessFindingKind.RetroactivelyProtected,
                                                 aggregateId,
                                                 range.First,
                                                 range.Last,
                                                 head,
                                                 null,
                                                 range.Anchor,
                                                 null);
        }
    }

    /// <summary>
    /// Groups consecutive retroactively sealed versions that are protected since the same anchor.
    /// </summary>
    private static List<(Int64 First, Int64 Last, AnchorReference Anchor)> FindRetroactiveRanges(IReadOnlyList<EventSummary> events,
                                                                                                 AnchorReference?[] anchors)
    {
        var ranges = new List<(Int64 First, Int64 Last, AnchorReference Anchor)>();
        for (var index = 0; index < events.Count; index++)
        {
            if (events[index].SealMode != IntegritySealMode.Retroactive || anchors[index] is not { } anchor)
            {
                continue;
            }

            var version = events[index].Version;
            if (ranges.Count > 0 && ranges[^1].Last == version - 1 && ranges[^1].Anchor == anchor)
            {
                ranges[^1] = (ranges[^1].First, version, anchor);
            }
            else
            {
                ranges.Add((version, version, anchor));
            }
        }

        return ranges;
    }

    /// <summary>
    /// Reads a projected event. Its readable aggregate identifier and version are returned even when the other one
    /// cannot be read, so a malformed event can be reported with them.
    /// </summary>
    /// <returns>The event, or <c>null</c> when its aggregate identifier or version cannot be read.</returns>
    private static EventSummary? ReadSummary(BsonDocument document, out Guid? aggregateId, out Int64? version)
    {
        aggregateId = document.TryGetValue(EventDocumentFields<TAggregate>.AggregateId, out var id) &&
                      id is BsonBinaryData { SubType: BsonBinarySubType.UuidStandard } binary
            ? binary.ToGuid(GuidRepresentation.Standard)
            : null;
        version = document.TryGetValue(EventDocumentFields<TAggregate>.Version, out var versionValue) &&
                  StoredVersion.TryRead(versionValue, out var storedVersion) &&
                  storedVersion >= 1
            ? storedVersion
            : null;
        if (aggregateId is null || version is not { } eventVersion)
        {
            return null;
        }

        var integrity = document.GetValue(EventIntegrityChain.ElementName, BsonNull.Value) as BsonDocument;
        IntegritySealMode? sealMode = integrity is not null &&
                                      integrity.TryGetValue(nameof(EventIntegrity.SealMode), out var mode) &&
                                      mode is BsonString { Value: var name } &&
                                      Enum.TryParse<IntegritySealMode>(name, out var parsed)
            ? parsed
            : null;
        DateTime? createdUtc = document.TryGetValue(EventDocumentFields<TAggregate>.CreatedUtc, out var created) && created is BsonDateTime date
            ? date.ToUniversalTime()
            : null;
        return new EventSummary(eventVersion, integrity is not null, sealMode, createdUtc);
    }

    /// <summary>
    /// Reads the events of the aggregate type in identifier and version order and yields them grouped by stream.
    /// Documents whose identifier or version cannot be read are left out of the streams and added to
    /// <paramref name="malformedEvents"/> as findings before the stream read after them is yielded.
    /// </summary>
    private async IAsyncEnumerable<(Guid AggregateId, List<EventSummary> Events)> ReadStreamsAsync(List<CompletenessFinding> malformedEvents,
                                                                                                   [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var projection = new BsonDocument
        {
            { "_id", 0 },
            { EventDocumentFields<TAggregate>.AggregateId, 1 },
            { EventDocumentFields<TAggregate>.Version, 1 },
            { EventDocumentFields<TAggregate>.CreatedUtc, 1 },
            { $"{EventIntegrityChain.ElementName}.{nameof(EventIntegrity.SealMode)}", 1 }
        };
        var sort = new BsonDocument
        {
            { EventDocumentFields<TAggregate>.AggregateId, 1 },
            { EventDocumentFields<TAggregate>.Version, 1 }
        };
        using var cursor = await _mongoHelper.Database.GetCollection<BsonDocument>(_options.EventsCollectionName)
                                             .Find(FilterDefinition<BsonDocument>.Empty)
                                             .Sort(sort)
                                             .Project(projection)
                                             .ToCursorAsync(cancellationToken);

        Guid? currentId = null;
        var events = new List<EventSummary>();
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var document in cursor.Current)
            {
                if (ReadSummary(document, out var readId, out var readVersion) is not { } summary || readId is not { } aggregateId)
                {
                    malformedEvents.Add(new CompletenessFinding(CompletenessFindingKind.MalformedEvent, readId, readVersion, readVersion, null, null, null, null));
                    continue;
                }

                if (currentId is { } id && id != aggregateId)
                {
                    yield return (id, events);
                    events = [];
                }

                currentId = aggregateId;
                events.Add(summary);
            }
        }

        if (currentId is { } last)
        {
            yield return (last, events);
        }
    }

    /// <summary>
    /// Orders identifiers as MongoDB orders UUIDs stored in the standard representation: by their bytes in RFC 9562
    /// order.
    /// </summary>
    private static class GuidOrder
    {
        public static Int32 Compare(Guid left, Guid right)
            => left.ToByteArray(true).AsSpan().SequenceCompareTo(right.ToByteArray(true));
    }

    /// <summary>
    /// A projected event: its version, whether it is sealed, its seal mode and its creation time.
    /// </summary>
    private sealed record EventSummary(Int64 Version, Boolean IsSealed, IntegritySealMode? SealMode, DateTime? CreatedUtc);

    /// <summary>
    /// Reads the leaves of the aggregate type stream by stream, keeping only leaves of completed anchors.
    /// </summary>
    private sealed class LeafReader : IAsyncDisposable
    {
        private readonly Dictionary<Guid, AnchorReference> _completedAnchors;
        private readonly IAsyncEnumerator<AnchorLeaf> _leaves;

        public LeafReader(IAsyncEnumerable<AnchorLeaf> leaves, Dictionary<Guid, AnchorReference> completedAnchors)
        {
            _leaves = leaves.GetAsyncEnumerator();
            _completedAnchors = completedAnchors;
        }

        /// <summary>
        /// Gets the next leaf of a completed anchor, or <c>null</c> when no leaf is left.
        /// </summary>
        public AnchorLeaf? Current { get; private set; }

        public Task InitializeAsync() => AdvanceAsync();

        /// <summary>
        /// Takes the leaves of a stream, which must be the stream of <see cref="Current"/> or a later one.
        /// </summary>
        public async Task<IReadOnlyList<StreamLeaf>> TakeStreamAsync(Guid aggregateId)
        {
            var leaves = new List<StreamLeaf>();
            while (Current is { } leaf && leaf.AggregateId == aggregateId)
            {
                leaves.Add(new StreamLeaf(leaf.Version, _completedAnchors[leaf.AnchorId]));
                await AdvanceAsync();
            }

            return leaves;
        }

        private async Task AdvanceAsync()
        {
            Current = null;
            while (await _leaves.MoveNextAsync())
            {
                var leaf = _leaves.Current;
                if (_completedAnchors.TryGetValue(leaf.AnchorId, out var anchor) && anchor.Sequence == leaf.AnchorSequence)
                {
                    Current = leaf;
                    return;
                }
            }
        }

        public ValueTask DisposeAsync() => _leaves.DisposeAsync();
    }

    /// <summary>
    /// A leaf of a stream in a completed anchor.
    /// </summary>
    private sealed record StreamLeaf(Int64 Version, AnchorReference Anchor);
}
