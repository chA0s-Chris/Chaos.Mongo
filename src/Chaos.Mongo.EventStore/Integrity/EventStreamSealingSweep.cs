// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using Chaos.Mongo.EventStore.Errors;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

/// <summary>
/// Background sweep that retroactively seals every event stored without integrity data, for example
/// events written before protection was enabled or while it was disabled.
/// </summary>
/// <remarks>
/// A pass runs on one instance at a time under a distributed lock. It enumerates stream heads from the
/// events collection and relies on sealed events forming a prefix of their stream: a stream whose head is
/// sealed is fully sealed. Progress is persisted after every stream, so an interrupted pass resumes.
/// </remarks>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
internal sealed class EventStreamSealingSweep<TAggregate> where TAggregate : class, IAggregate, new()
{
    private readonly ILogger _logger;
    private readonly IMongoHelper _mongoHelper;
    private readonly MongoEventStoreOptions<TAggregate> _options;
    private readonly EventStreamSealer<TAggregate> _sealer;
    private readonly TimeProvider _timeProvider;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task _sweepLoop = Task.CompletedTask;

    public EventStreamSealingSweep(IMongoHelper mongoHelper,
                                   MongoEventStoreOptions<TAggregate> options,
                                   TimeProvider timeProvider,
                                   ILogger<EventStreamSealingSweep<TAggregate>> logger)
    {
        ArgumentNullException.ThrowIfNull(mongoHelper);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _mongoHelper = mongoHelper;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _sealer = new EventStreamSealer<TAggregate>(mongoHelper, options);
    }

    /// <summary>
    /// Gets the name of the distributed lock that ensures a single sweep per events collection.
    /// </summary>
    public String LockName => $"Chaos.Mongo.EventStore.IntegritySealing:{_options.EventsCollectionName}";

    /// <summary>
    /// Gets the persisted progress of the current or last pass.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The sealing state, or <c>null</c> when no pass has run yet.</returns>
    public async Task<IntegritySealingState?> GetStatusAsync(CancellationToken cancellationToken = default)
        => await GetStateCollection()
                 .Find(s => s.Id == IntegritySealingState.DocumentId)
                 .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Runs one sealing pass, or resumes an interrupted one, unless another instance holds the sweep lock.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><c>true</c> when the pass completed; <c>false</c> when the lock was unavailable or lost.</returns>
    public async Task<Boolean> RunPassAsync(CancellationToken cancellationToken = default)
    {
        await using var sweepLock = await _mongoHelper.TryAcquireLockAsync(LockName, Chaos.Mongo.MongoDefaults.LockLeaseTime, cancellationToken);
        if (sweepLock is null)
        {
            _logger.LogDebug("Integrity sealing pass for collection {CollectionName} skipped; another instance holds the lock",
                             _options.EventsCollectionName);
            return false;
        }

        var state = await GetStatusAsync(cancellationToken);
        if (state is not { IsPassInProgress: true })
        {
            state = new IntegritySealingState
            {
                PassStartedUtc = _timeProvider.GetUtcNow().UtcDateTime
            };
            await SaveStateAsync(state, cancellationToken);
        }

        while (await EnsureLockAsync(sweepLock, cancellationToken))
        {
            var aggregateId = await FindNextStreamAsync(state.Cursor, cancellationToken);
            if (aggregateId is not { } streamId)
            {
                state.Cursor = null;
                state.PassCompletedUtc = _timeProvider.GetUtcNow().UtcDateTime;
                await SaveStateAsync(state, cancellationToken);
                return true;
            }

            if (!await IsStreamHeadSealedAsync(streamId, cancellationToken))
            {
                Int32? sealedEvents;
                try
                {
                    sealedEvents = await SealStreamAsync(streamId, sweepLock, cancellationToken);
                }
                catch (MongoEventStoreException ex)
                {
                    _logger.LogError(ex, "Stream {AggregateId} in collection {CollectionName} cannot be sealed",
                                     streamId, _options.EventsCollectionName);
                    sealedEvents = 0;
                }

                if (sealedEvents is not { } count)
                {
                    break;
                }

                if (count > 0)
                {
                    state.StreamsSealed++;
                    state.EventsSealed += count;
                }
            }

            state.Cursor = streamId;
            state.StreamsChecked++;
            await SaveStateAsync(state, cancellationToken);
        }

        _logger.LogWarning("Integrity sealing pass for collection {CollectionName} lost its lock and stops at the last checked stream",
                           _options.EventsCollectionName);
        return false;
    }

    /// <summary>
    /// Starts the background loop: one pass immediately, then one per
    /// <see cref="MongoEventStoreOptions{TAggregate}.SealingSweepInterval"/>.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token linked to the loop.</param>
    /// <returns>A completed task; the loop runs in the background.</returns>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_cancellationTokenSource is not null)
        {
            return Task.CompletedTask;
        }

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _sweepLoop = RunLoopAsync(_cancellationTokenSource.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the background loop and waits for the current pass to observe cancellation.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that limits the wait.</param>
    /// <returns>A task that completes when the loop has stopped.</returns>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_cancellationTokenSource is null)
        {
            return;
        }

        await _cancellationTokenSource.CancelAsync();
        try
        {
            await _sweepLoop.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        finally
        {
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
        }
    }

    private async Task<Boolean> EnsureLockAsync(IMongoLock sweepLock, CancellationToken cancellationToken)
    {
        if (!sweepLock.IsValid)
        {
            return false;
        }

        var remaining = sweepLock.ValidUntilUtc - _timeProvider.GetUtcNow().UtcDateTime;
        return remaining >= Chaos.Mongo.MongoDefaults.LockLeaseTime / 2 || await sweepLock.TryExtendAsync(Chaos.Mongo.MongoDefaults.LockLeaseTime, cancellationToken);
    }

    private async Task<Guid?> FindNextStreamAsync(Guid? cursor, CancellationToken cancellationToken)
    {
        var query = SealingSweepQueries<TAggregate>.NextStream(cursor);
        var next = await GetEventsCollection()
                         .Find(query.Filter)
                         .Sort(query.Sort)
                         .Project(query.Projection)
                         .Limit(1)
                         .FirstOrDefaultAsync(cancellationToken);

        return next?[EventDocumentFields<TAggregate>.AggregateId].AsGuid;
    }

    private IMongoCollection<BsonDocument> GetEventsCollection()
        => _mongoHelper.Database.GetCollection<BsonDocument>(_options.EventsCollectionName);

    private IMongoCollection<IntegritySealingState> GetStateCollection()
        => _mongoHelper.Database.GetCollection<IntegritySealingState>(_options.IntegrityStateCollectionName);

    private async Task<Boolean> IsStreamHeadSealedAsync(Guid aggregateId, CancellationToken cancellationToken)
    {
        var query = SealingSweepQueries<TAggregate>.StreamHead(aggregateId);
        var head = await GetEventsCollection()
                         .Find(query.Filter)
                         .Sort(query.Sort)
                         .Project(query.Projection)
                         .Limit(1)
                         .FirstOrDefaultAsync(cancellationToken);

        return head is null || head.Contains(EventIntegrityChain.ElementName);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunPassAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Integrity sealing pass for collection {CollectionName} failed", _options.EventsCollectionName);
            }

            try
            {
                await Task.Delay(_options.SealingSweepInterval, _timeProvider, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private Task SaveStateAsync(IntegritySealingState state, CancellationToken cancellationToken)
        => GetStateCollection().ReplaceOneAsync(
            s => s.Id == IntegritySealingState.DocumentId,
            state,
            new ReplaceOptions
            {
                IsUpsert = true
            },
            cancellationToken);

    /// <summary>
    /// Seals a stream chunk by chunk, one transaction per chunk, and returns the number of sealed events.
    /// </summary>
    private async Task<Int32?> SealStreamAsync(Guid aggregateId, IMongoLock sweepLock, CancellationToken cancellationToken)
    {
        var sealedEvents = 0;
        Int64? knownSealedVersion = null;

        while (await EnsureLockAsync(sweepLock, cancellationToken))
        {
            SealingChunkResult result;
            try
            {
                result = await _mongoHelper.ExecuteInTransaction(
                    (_, session, ct) => _sealer.SealNextChunkAsync(session, aggregateId, knownSealedVersion, ct),
                    cancellationToken: cancellationToken);
            }
            catch (MongoConcurrencyException)
            {
                // Another writer sealed part of the stream; re-read it and continue.
                knownSealedVersion = null;
                continue;
            }

            sealedEvents += result.SealedCount;
            knownSealedVersion = result.LastSealedVersion;
            if (!result.HasMore)
            {
                return sealedEvents;
            }
        }

        return null;
    }
}
