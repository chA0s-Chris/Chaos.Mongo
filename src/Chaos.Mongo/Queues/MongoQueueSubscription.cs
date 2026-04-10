// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Queues;

using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

/// <summary>
/// Manages a MongoDB-based queue subscription that monitors and processes queue items.
/// </summary>
/// <typeparam name="TPayload">The type of payload stored in queue items.</typeparam>
/// <remarks>
/// This subscription uses a dual approach for queue processing:
/// <list type="bullet">
///     <item>MongoDB change streams to detect new items in real-time</item>
///     <item>Polling mechanism to process existing items and handle missed events</item>
/// </list>
/// Items are locked during processing to ensure single-consumer semantics.
/// </remarks>
public class MongoQueueSubscription<TPayload> : IMongoQueueSubscription<TPayload>
    where TPayload : class, new()
{
    private static readonly TimeSpan MaxLeaseRecoveryWakeInterval = TimeSpan.FromSeconds(1);
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly ILogger _logger;
    private readonly IMongoHelper _mongoHelper;
    private readonly IMongoQueuePayloadHandlerFactory _payloadHandlerFactory;
    private readonly IMongoQueuePayloadPrioritizer _payloadPrioritizer;
    private readonly MongoQueueDefinition _queueDefinition;
    private readonly SemaphoreSlim _signalSemaphore = new(1, Int32.MaxValue);
    private readonly TimeProvider _timeProvider;
    private Boolean _isActive;
    private Boolean _isDisposed;
    private Task _monitorTask = Task.CompletedTask;
    private Task _processingTask = Task.CompletedTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="MongoQueueSubscription{TPayload}"/> class.
    /// </summary>
    /// <param name="queueDefinition">The queue configuration.</param>
    /// <param name="mongoHelper">The MongoDB helper for database operations.</param>
    /// <param name="payloadHandlerFactory">The factory for creating payload handlers.</param>
    /// <param name="payloadPrioritizer">The prioritizer for sorting queue items.</param>
    /// <param name="timeProvider">The time provider for timestamp operations.</param>
    /// <param name="logger">The logger for diagnostic messages.</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    public MongoQueueSubscription(MongoQueueDefinition queueDefinition,
                                  IMongoHelper mongoHelper,
                                  IMongoQueuePayloadHandlerFactory payloadHandlerFactory,
                                  IMongoQueuePayloadPrioritizer payloadPrioritizer,
                                  TimeProvider timeProvider,
                                  ILogger<MongoQueueSubscription<TPayload>> logger)
    {
        ArgumentNullException.ThrowIfNull(queueDefinition);
        ArgumentNullException.ThrowIfNull(mongoHelper);
        ArgumentNullException.ThrowIfNull(payloadHandlerFactory);
        ArgumentNullException.ThrowIfNull(payloadPrioritizer);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _queueDefinition = queueDefinition;
        _mongoHelper = mongoHelper;
        _payloadHandlerFactory = payloadHandlerFactory;
        _payloadPrioritizer = payloadPrioritizer;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Boolean IsActive => _isActive && !_isDisposed;

    private FilterDefinition<MongoQueueItem<TPayload>> CreateAvailableQueueItemFilter()
    {
        var filterBuilder = Builders<MongoQueueItem<TPayload>>.Filter;
        var lockExpiryUtc = _timeProvider.GetUtcNow().UtcDateTime - _queueDefinition.LockLeaseTime;

        return filterBuilder.Eq(x => x.IsClosed, false) &
               (filterBuilder.Eq(x => x.IsLocked, false) |
                (filterBuilder.Eq(x => x.IsLocked, true) &
                 (filterBuilder.Eq(x => x.LockedUtc, null) |
                  filterBuilder.Lt(x => x.LockedUtc, lockExpiryUtc))));
    }

    private FilterDefinition<MongoQueueItem<TPayload>> CreateAvailableQueueItemFilter(ObjectId queueItemId)
        => Builders<MongoQueueItem<TPayload>>.Filter.Eq(x => x.Id, queueItemId) &
           CreateAvailableQueueItemFilter();

    /// <summary>
    /// Ensures that required indexes exist on the queue collection for efficient querying.
    /// </summary>
    /// <param name="collection">The MongoDB collection to create indexes on.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private Task EnsureIndexesAsync(IMongoCollection<MongoQueueItem<TPayload>> collection, CancellationToken cancellationToken)
        => collection.Indexes
                     .CreateOneOrUpdateAsync(
                         new(Builders<MongoQueueItem<TPayload>>.IndexKeys
                                                               .Ascending(x => x.IsClosed)
                                                               .Ascending(x => x.IsLocked)
                                                               .Ascending(x => x.LockedUtc)),
                         cancellationToken: cancellationToken);

    /// <summary>
    /// Monitors a MongoDB change stream for insert operations and signals the processing task when new items arrive.
    /// </summary>
    /// <param name="collection">The MongoDB collection to monitor for insert operations.</param>
    /// <param name="cancellationToken">The cancellation token to stop monitoring.</param>
    /// <returns>A task that completes when monitoring stops.</returns>
    private async Task MonitorChangeStreamAsync(IMongoCollection<MongoQueueItem<TPayload>> collection, CancellationToken cancellationToken)
    {
        var options = new ChangeStreamOptions
        {
            FullDocument = ChangeStreamFullDocumentOption.Default
        };
        var pipeline = new EmptyPipelineDefinition<ChangeStreamDocument<MongoQueueItem<TPayload>>>()
            .Match(x => x.OperationType == ChangeStreamOperationType.Insert);

        _logger.LogInformation("Watching change stream for collection {CollectionName}", _queueDefinition.CollectionName);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var cursor = await collection.WatchAsync(pipeline, options, cancellationToken);
                while (await cursor.MoveNextAsync(cancellationToken))
                {
                    var batch = cursor.Current;
                    if (batch.Any())
                    {
                        // signal only once per batch
                        _signalSemaphore.Release();
                    }
                }
            }
            catch (Exception e)
            {
                if (e is OperationCanceledException)
                    break;

                _logger.LogError(e, "Watching change stream for collection {CollectionName} failed", _queueDefinition.CollectionName);
                await Task.Delay(300, cancellationToken);
            }
        }

        _logger.LogInformation("Stopped watching change stream for collection {CollectionName}", _queueDefinition.CollectionName);
    }

    /// <summary>
    /// Processes a queue item with the given identifier.
    /// </summary>
    /// <param name="collection">The MongoDB collection to process queue items from.</param>
    /// <param name="queueItemId">The identifier of the queue item to process.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <remarks>
    /// The queue item is locked during processing and closed after processing has completed.
    /// If the queue item is already closed or still within its lease, it is skipped.
    /// </remarks>
    private async Task ProcessQueueItemAsync(IMongoCollection<MongoQueueItem<TPayload>> collection,
                                             ObjectId queueItemId,
                                             CancellationToken cancellationToken)
    {
        _logger.LogDebug("Processing queue item {QueueItemId} with payload {PayloadType}", queueItemId, typeof(TPayload).FullName);

        try
        {
            var acquiredLockUtc = _timeProvider.GetUtcNow().UtcDateTime;
            var queueItem = await collection.FindOneAndUpdateAsync(
                CreateAvailableQueueItemFilter(queueItemId),
                Builders<MongoQueueItem<TPayload>>.Update
                                                  .Set(x => x.IsLocked, true)
                                                  .Set(x => x.LockedUtc, acquiredLockUtc),
                new()
                {
                    ReturnDocument = ReturnDocument.Before
                },
                cancellationToken);

            if (queueItem is null)
            {
                // skip item
                return;
            }

            if (queueItem.IsLocked)
            {
                _logger.LogWarning("Recovering expired queue item lock {QueueItemId} with payload {PayloadType}",
                                   queueItemId,
                                   typeof(TPayload).FullName);
            }

            var payloadHandler = _payloadHandlerFactory.CreateHandler<TPayload>();
            try
            {
                _logger.LogDebug("Handling payload {PayloadType} with handler {PayloadHandlerType}", typeof(TPayload).FullName,
                                 payloadHandler.GetType().FullName);
                await payloadHandler.HandlePayloadAsync(queueItem.Payload, cancellationToken);
            }
            finally
            {
                // ReSharper disable SuspiciousTypeConversion.Global
                if (payloadHandler is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync();
                }
                else if (payloadHandler is IDisposable disposable)
                {
                    disposable.Dispose();
                }
                // ReSharper restore SuspiciousTypeConversion.Global
            }

            var completionFilter = Builders<MongoQueueItem<TPayload>>.Filter.Eq(x => x.Id, queueItemId) &
                                   Builders<MongoQueueItem<TPayload>>.Filter.Eq(x => x.IsClosed, false) &
                                   Builders<MongoQueueItem<TPayload>>.Filter.Eq(x => x.IsLocked, true) &
                                   Builders<MongoQueueItem<TPayload>>.Filter.Eq(x => x.LockedUtc, acquiredLockUtc);
            var completionResult = await collection.UpdateOneAsync(
                completionFilter,
                Builders<MongoQueueItem<TPayload>>.Update
                                                  .Set(x => x.IsClosed, true)
                                                  .Set(x => x.ClosedUtc, _timeProvider.GetUtcNow().UtcDateTime)
                                                  .Set(x => x.IsLocked, false)
                                                  .Unset(x => x.LockedUtc),
                cancellationToken: cancellationToken);

            if (completionResult.ModifiedCount == 0)
            {
                _logger.LogWarning("Skipping completion for queue item {QueueItemId} with payload {PayloadType} because lock ownership changed",
                                   queueItemId,
                                   typeof(TPayload).FullName);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            _logger.LogError(e, "Processing queue item {QueueItemId} with payload {PayloadType} failed", queueItemId, typeof(TPayload).FullName);
        }
    }

    /// <summary>
    /// Continuously processes queue items by polling for unlocked, unclosed items and invoking their handlers.
    /// </summary>
    /// <param name="collection">The MongoDB collection to process queue items from.</param>
    /// <param name="cancellationToken">The cancellation token to stop processing.</param>
    /// <returns>A task that completes when processing stops.</returns>
    /// <remarks>
    /// This method waits for signals from the change stream monitor or self-signals when more items exist.
    /// Items are locked during processing using optimistic concurrency with lease expiry.
    /// Failed operations are retried after a delay.
    /// </remarks>
    private async Task ProcessQueueItemsAsync(IMongoCollection<MongoQueueItem<TPayload>> collection, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing items with payload {PayloadType} for queue {CollectionName}",
                               _queueDefinition.PayloadType.FullName,
                               _queueDefinition.CollectionName);

        var sortDefinition = _payloadPrioritizer.CreateSortDefinition<TPayload>();
        var leaseRecoveryWakeInterval = _queueDefinition.LockLeaseTime < MaxLeaseRecoveryWakeInterval
            ? _queueDefinition.LockLeaseTime
            : MaxLeaseRecoveryWakeInterval;
        var countOptions = new CountOptions
        {
            Limit = 1
        };

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _ = await _signalSemaphore.WaitAsync(leaseRecoveryWakeInterval, cancellationToken);

                var queueItemIds = await collection.Find(CreateAvailableQueueItemFilter())
                                                   .Sort(sortDefinition)
                                                   .Project(x => x.Id)
                                                   .Limit(_queueDefinition.QueryLimit)
                                                   .ToListAsync(cancellationToken);

                if (queueItemIds.Count == 0)
                {
                    // Brief delay to avoid busy-waiting, then re-signal to check again
                    // This prevents deadlock when messages are published before change stream starts
                    await Task.Delay(100, cancellationToken);
                    _signalSemaphore.Release();
                    continue;
                }

                foreach (var queueItemId in queueItemIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ProcessQueueItemAsync(collection, queueItemId, cancellationToken);
                }

                // check if there are unprocessed items left so we don't wait
                var count = await collection.CountDocumentsAsync(CreateAvailableQueueItemFilter(), countOptions, cancellationToken);
                if (count > 0)
                {
                    _signalSemaphore.Release();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                _logger.LogError(e,
                                 "Processing items with payload {PayloadType} of queue {CollectionName} failed",
                                 _queueDefinition.PayloadType.FullName,
                                 _queueDefinition.CollectionName);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }

        _logger.LogInformation("Stopped processing items with payload {PayloadType} for queue {CollectionName}",
                               _queueDefinition.PayloadType.FullName,
                               _queueDefinition.CollectionName);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        await _cancellationTokenSource.CancelAsync();
        _cancellationTokenSource.Dispose();
        _signalSemaphore.Dispose();
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
        {
            throw new InvalidOperationException("Subscription is disposed.");
        }

        if (_isActive)
        {
            return;
        }

        _isActive = true;

        var collection = _mongoHelper.Database.GetCollection<MongoQueueItem<TPayload>>(_queueDefinition.CollectionName);
        await EnsureIndexesAsync(collection, cancellationToken);

        // Signal initially to start processing any existing messages
        _signalSemaphore.Release();

        _monitorTask = Task.Run(async () => await MonitorChangeStreamAsync(collection, _cancellationTokenSource.Token), cancellationToken);
        _processingTask = Task.Run(async () => await ProcessQueueItemsAsync(collection, _cancellationTokenSource.Token), cancellationToken);
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
        {
            return;
        }

        await _cancellationTokenSource.CancelAsync();
        _isActive = false;

        try
        {
            await Task.WhenAll(_monitorTask, _processingTask).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Stopping queue subscription for payload {PayloadType} has been cancelled, " +
                               "background tasks may still be running", _queueDefinition.PayloadType.FullName);
        }
    }
}
