// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

/// <summary>
/// Polls the outbox collection for pending messages and publishes them
/// via the user-provided <see cref="IOutboxPublisher"/>.
/// </summary>
public sealed class OutboxProcessor : IOutboxProcessor
{
    private readonly ILogger<OutboxProcessor> _logger;
    private readonly IMongoHelper _mongoHelper;
    private readonly OutboxOptions _options;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task _processingTask = Task.CompletedTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboxProcessor"/> class.
    /// </summary>
    /// <param name="mongoHelper">The MongoDB helper for database operations.</param>
    /// <param name="options">The outbox configuration options.</param>
    /// <param name="serviceScopeFactory">The service scope factory for resolving the publisher.</param>
    /// <param name="timeProvider">The time provider for timestamp operations.</param>
    /// <param name="logger">The logger for diagnostic messages.</param>
    public OutboxProcessor(IMongoHelper mongoHelper,
                           OutboxOptions options,
                           IServiceScopeFactory serviceScopeFactory,
                           TimeProvider timeProvider,
                           ILogger<OutboxProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(mongoHelper);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serviceScopeFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _mongoHelper = mongoHelper;
        _options = options;
        _serviceScopeFactory = serviceScopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private DateTime ComputeNextAttemptUtc(Int32 retryCount)
    {
        // Exponential backoff: initialDelay * 2^(retryCount - 1), capped at maxDelay
        var delayTicks = _options.RetryBackoffInitialDelay.Ticks * (1L << Math.Min(retryCount - 1, 30));
        var delay = TimeSpan.FromTicks(Math.Min(delayTicks, _options.RetryBackoffMaxDelay.Ticks));
        return _timeProvider.GetUtcNow().UtcDateTime.Add(delay);
    }

    private IMongoCollection<OutboxMessage> GetCollection()
    {
        return _mongoHelper.Database.GetCollection<OutboxMessage>(_options.CollectionName);
    }

    private async Task HandleFailureAsync(IMongoCollection<OutboxMessage> collection,
                                          ObjectId messageId,
                                          String lockId,
                                          Int32 currentRetryCount,
                                          Exception exception)
    {
        var newRetryCount = currentRetryCount + 1;
        var failureFilter = Builders<OutboxMessage>.Filter.Eq(m => m.Id, messageId) &
                            Builders<OutboxMessage>.Filter.Eq(m => m.LockId, lockId);

        UpdateDefinition<OutboxMessage> failureUpdate;

        if (newRetryCount >= _options.MaxRetries)
        {
            // Permanently failed
            failureUpdate = Builders<OutboxMessage>.Update
                                                   .Set(m => m.State, OutboxMessageState.Failed)
                                                   .Set(m => m.FailedUtc, _timeProvider.GetUtcNow().UtcDateTime)
                                                   .Set(m => m.RetryCount, newRetryCount)
                                                   .Set(m => m.Error, exception.Message)
                                                   .Set(m => m.NextAttemptUtc, null)
                                                   .Set(m => m.IsLocked, false)
                                                   .Set(m => m.LockedUtc, null)
                                                   .Set(m => m.LockId, null);

            _logger.LogWarning(
                "Outbox message {MessageId} permanently failed after {RetryCount} attempts",
                messageId, newRetryCount);
        }
        else
        {
            // Schedule retry with exponential backoff
            var nextAttemptUtc = ComputeNextAttemptUtc(newRetryCount);

            failureUpdate = Builders<OutboxMessage>.Update
                                                   .Set(m => m.RetryCount, newRetryCount)
                                                   .Set(m => m.Error, exception.Message)
                                                   .Set(m => m.NextAttemptUtc, nextAttemptUtc)
                                                   .Set(m => m.IsLocked, false)
                                                   .Set(m => m.LockedUtc, null)
                                                   .Set(m => m.LockId, null);

            _logger.LogWarning(
                "Outbox message {MessageId} failed (attempt {RetryCount}/{MaxRetries}); next attempt at {NextAttemptUtc}",
                messageId, newRetryCount, _options.MaxRetries, nextAttemptUtc);
        }

        var result = await collection.UpdateOneAsync(failureFilter, failureUpdate);

        if (result.ModifiedCount == 0)
        {
            _logger.LogWarning(
                "Outbox message {MessageId} failure update skipped — ownership was lost to another processor",
                messageId);
        }
    }

    private async Task<Int32> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var collection = GetCollection();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var staleLockThreshold = now - _options.LockTimeout;

        // Query for eligible messages:
        // State == Pending AND (NextAttemptUtc is null OR NextAttemptUtc <= now)
        // AND (IsLocked == false OR LockedUtc <= staleLockThreshold)
        // ORDER BY _id ASC, LIMIT batchSize
        var filter = Builders<OutboxMessage>.Filter.Eq(m => m.State, OutboxMessageState.Pending) &
                     (Builders<OutboxMessage>.Filter.Eq(m => m.NextAttemptUtc, null) |
                      Builders<OutboxMessage>.Filter.Lte(m => m.NextAttemptUtc, now)) &
                     (Builders<OutboxMessage>.Filter.Eq(m => m.IsLocked, false) |
                      Builders<OutboxMessage>.Filter.Lte(m => m.LockedUtc, staleLockThreshold));

        var sort = Builders<OutboxMessage>.Sort.Ascending(m => m.Id);

        var messages = await collection
                             .Find(filter)
                             .Sort(sort)
                             .Limit(_options.BatchSize)
                             .ToListAsync(cancellationToken);

        if (messages.Count == 0)
        {
            return 0;
        }

        _logger.LogDebug("Found {MessageCount} eligible outbox messages", messages.Count);

        using var scope = _serviceScopeFactory.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IOutboxPublisher>();

        foreach (var message in messages)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            await ProcessMessageAsync(collection, message, publisher, cancellationToken);
        }

        _logger.LogDebug("Completed processing batch of {MessageCount} outbox messages", messages.Count);
        return messages.Count;
    }

    private async Task ProcessLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var batchCount = await ProcessBatchAsync(cancellationToken);

                if (batchCount < _options.BatchSize)
                {
                    // Batch was not full, wait before polling again
                    await Task.Delay(_options.PollingInterval, _timeProvider, cancellationToken);
                }
                // If batch was full, immediately poll again
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Graceful shutdown
                break;
            }
            catch (MongoException ex)
            {
                _logger.LogError(ex, "Transient MongoDB error in outbox processor polling loop; retrying after delay");

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _timeProvider, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Unexpected error in outbox processor polling loop; retrying after delay");

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _timeProvider, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task ProcessMessageAsync(IMongoCollection<OutboxMessage> collection,
                                           OutboxMessage message,
                                           IOutboxPublisher publisher,
                                           CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var staleLockThreshold = now - _options.LockTimeout;
        var lockId = Guid.NewGuid().ToString();

        // Atomically claim the message
        var claimFilter = Builders<OutboxMessage>.Filter.Eq(m => m.Id, message.Id) &
                          Builders<OutboxMessage>.Filter.Eq(m => m.State, OutboxMessageState.Pending) &
                          (Builders<OutboxMessage>.Filter.Eq(m => m.NextAttemptUtc, null) |
                           Builders<OutboxMessage>.Filter.Lte(m => m.NextAttemptUtc, now)) &
                          (Builders<OutboxMessage>.Filter.Eq(m => m.IsLocked, false) |
                           Builders<OutboxMessage>.Filter.Lte(m => m.LockedUtc, staleLockThreshold));

        var claimUpdate = Builders<OutboxMessage>.Update
                                                 .Set(m => m.IsLocked, true)
                                                 .Set(m => m.LockedUtc, now)
                                                 .Set(m => m.LockId, lockId);

        var claimed = await collection.FindOneAndUpdateAsync(
            claimFilter,
            claimUpdate,
            new FindOneAndUpdateOptions<OutboxMessage>
            {
                ReturnDocument = ReturnDocument.After
            },
            cancellationToken);

        if (claimed is null)
        {
            _logger.LogDebug("Outbox message {MessageId} could not be claimed (already processed or locked by another processor)", message.Id);
            return;
        }

        try
        {
            await publisher.PublishAsync(claimed, cancellationToken);

            // Success: mark as processed
            var successFilter = Builders<OutboxMessage>.Filter.Eq(m => m.Id, message.Id) &
                                Builders<OutboxMessage>.Filter.Eq(m => m.LockId, lockId);

            var successUpdate = Builders<OutboxMessage>.Update
                                                       .Set(m => m.State, OutboxMessageState.Processed)
                                                       .Set(m => m.ProcessedUtc, _timeProvider.GetUtcNow().UtcDateTime)
                                                       .Set(m => m.IsLocked, false)
                                                       .Set(m => m.LockedUtc, null)
                                                       .Set(m => m.LockId, null);

            var successResult = await collection.UpdateOneAsync(successFilter, successUpdate, cancellationToken: cancellationToken);

            if (successResult.ModifiedCount == 0)
            {
                _logger.LogWarning(
                    "Outbox message {MessageId} was published but ownership was lost before finalizing state; " +
                    "another processor may have reclaimed it",
                    message.Id);
            }
            else
            {
                _logger.LogDebug("Outbox message {MessageId} of type '{MessageType}' processed successfully", message.Id, claimed.Type);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Graceful shutdown: release the lock so another processor can pick it up
            await TryReleaseLockAsync(collection, message.Id, lockId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish outbox message {MessageId} of type '{MessageType}'", message.Id, claimed.Type);
            await HandleFailureAsync(collection, message.Id, lockId, claimed.RetryCount, ex);
        }
    }

    private async Task TryReleaseLockAsync(IMongoCollection<OutboxMessage> collection,
                                           ObjectId messageId,
                                           String lockId)
    {
        try
        {
            var releaseFilter = Builders<OutboxMessage>.Filter.Eq(m => m.Id, messageId) &
                                Builders<OutboxMessage>.Filter.Eq(m => m.LockId, lockId);

            var releaseUpdate = Builders<OutboxMessage>.Update
                                                       .Set(m => m.IsLocked, false)
                                                       .Set(m => m.LockedUtc, null)
                                                       .Set(m => m.LockId, null);

            await collection.UpdateOneAsync(releaseFilter, releaseUpdate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to release lock on outbox message {MessageId} during shutdown", messageId);
        }
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_cancellationTokenSource is not null)
        {
            _logger.LogWarning("Outbox processor is already running");
            return Task.CompletedTask;
        }

        _logger.LogInformation("Starting outbox processor for collection '{CollectionName}'", _options.CollectionName);
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _processingTask = ProcessLoopAsync(_cancellationTokenSource.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_cancellationTokenSource is null)
        {
            return;
        }

        _logger.LogInformation("Stopping outbox processor for collection '{CollectionName}'", _options.CollectionName);

        await _cancellationTokenSource.CancelAsync();

        try
        {
            await _processingTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        finally
        {
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
        }

        _logger.LogInformation("Outbox processor stopped for collection '{CollectionName}'", _options.CollectionName);
    }
}
