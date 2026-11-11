// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

/// <summary>
/// Polls the outbox collection for pending messages and publishes them
/// via the user-provided <see cref="IOutboxPublisher"/>.
/// </summary>
internal sealed class OutboxProcessor : IOutboxProcessor
{
    private static readonly TimeSpan FinalizationTimeout = TimeSpan.FromSeconds(3);
    private readonly ILogger<OutboxProcessor> _logger;
    private readonly IMongoHelper _mongoHelper;
    private readonly OutboxOptions _options;
    private readonly Object? _publisherKey;
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

    internal OutboxProcessor(IMongoHelper mongoHelper, OutboxOptions options, IServiceScopeFactory serviceScopeFactory,
                             TimeProvider timeProvider, ILogger<OutboxProcessor> logger, Object publisherKey)
        : this(mongoHelper, options, serviceScopeFactory, timeProvider, logger)
    {
        _publisherKey = publisherKey;
    }

    private IDisposable? BeginScope() => _logger.BeginScope(new Dictionary<String, Object>
    {
        ["OutboxIdentity"] = _options.Identity,
        ["CollectionName"] = _options.CollectionName
    });

    private DateTime ComputeNextAttemptUtc(Int64 attemptCount)
    {
        var delay = _options.RetryBackoffInitialDelay;
        var maxDelay = _options.RetryBackoffMaxDelay;

        if (delay > maxDelay)
        {
            delay = maxDelay;
        }

        for (var attempt = 1L; attempt < attemptCount && delay < maxDelay; attempt++)
        {
            if (delay.Ticks > maxDelay.Ticks / 2)
            {
                delay = maxDelay;
                break;
            }

            delay = TimeSpan.FromTicks(delay.Ticks * 2);
        }

        return _timeProvider.GetUtcNow().UtcDateTime.Add(delay);
    }

    /// <summary>
    /// Builds the claim aggregation-pipeline update that atomically decides, from the document's own
    /// pre-update <see cref="OutboxMessage.AttemptCount"/> compared against <see cref="OutboxOptions.MaxRetries"/>,
    /// whether this claim is <b>granted</b> a new attempt or the message transitions directly to <b>terminal</b>
    /// without granting a lock. This is the only place <see cref="OutboxMessage.AttemptCount"/> is incremented,
    /// and it runs on every claim regardless of why a previous attempt stopped (a resolved failure, or a crash
    /// between claim and <see cref="HandleFailureAsync"/>/<see cref="MarkProcessedAsync"/>), which is what makes
    /// every claim count as a delivery attempt. The field paths below rely on <see cref="OutboxMessage"/>'s class
    /// map pinning each element name to its member name (see <see cref="OutboxSerializationSetup"/>). The terminal
    /// <see cref="OutboxMessageState.Failed"/> literal is rendered through that same registered class-map
    /// serializer, looked up exactly like an unparented typed filter resolves it, instead of a hard-coded BSON
    /// representation, so it stays correct however <see cref="OutboxMessage.State"/> is actually configured to
    /// serialize — the exact path typed filters (for example the claim/TTL filters below) already go through.
    /// </summary>
    private UpdateDefinition<OutboxMessage> CreateClaimUpdate(DateTime now, String lockId)
    {
        BsonValue existingAttemptCount = new BsonDocument("$ifNull", new BsonArray
        {
            $"${nameof(OutboxMessage.AttemptCount)}",
            0L
        });
        BsonValue grant = _options.MaxRetries.HasValue
            ? new BsonDocument("$lte", new BsonArray
            {
                existingAttemptCount,
                _options.MaxRetries.Value
            })
            : BsonBoolean.True;
        BsonValue nowLiteral = new BsonDateTime(now);
        var failedStateLiteral = Builders<OutboxMessage>.Filter.Eq(m => m.State, OutboxMessageState.Failed)
                                                        .Render(new RenderArgs<OutboxMessage>(BsonSerializer.LookupSerializer<OutboxMessage>(), BsonSerializer.SerializerRegistry))
                                                        [nameof(OutboxMessage.State)];

        var setStage = new BsonDocument("$set", new BsonDocument
        {
            {
                nameof(OutboxMessage.AttemptCount), new BsonDocument("$cond", new BsonArray
                {
                    grant,
                    new BsonDocument("$add", new BsonArray
                    {
                        existingAttemptCount,
                        1L
                    }),
                    $"${nameof(OutboxMessage.AttemptCount)}"
                })
            },
            {
                nameof(OutboxMessage.State), new BsonDocument("$cond", new BsonArray
                {
                    grant,
                    $"${nameof(OutboxMessage.State)}",
                    failedStateLiteral
                })
            },
            {
                nameof(OutboxMessage.FailedUtc), new BsonDocument("$cond", new BsonArray
                {
                    grant,
                    "$$REMOVE",
                    nowLiteral
                })
            },
            {
                nameof(OutboxMessage.IsLocked), new BsonDocument("$cond", new BsonArray
                {
                    grant,
                    true,
                    false
                })
            },
            {
                nameof(OutboxMessage.LockedUtc), new BsonDocument("$cond", new BsonArray
                {
                    grant,
                    nowLiteral,
                    "$$REMOVE"
                })
            },
            {
                nameof(OutboxMessage.LockId), new BsonDocument("$cond", new BsonArray
                {
                    grant,
                    lockId,
                    "$$REMOVE"
                })
            },
            {
                nameof(OutboxMessage.NextAttemptUtc), new BsonDocument("$cond", new BsonArray
                {
                    grant,
                    $"${nameof(OutboxMessage.NextAttemptUtc)}",
                    "$$REMOVE"
                })
            }
        });

        var pipeline = new EmptyPipelineDefinition<OutboxMessage>()
            .AppendStage<OutboxMessage, OutboxMessage, OutboxMessage>(setStage);

        return Builders<OutboxMessage>.Update.Pipeline(pipeline);
    }

    private IMongoCollection<OutboxMessage> GetCollection()
    {
        return _mongoHelper.Database.GetCollection<OutboxMessage>(_options.CollectionName);
    }

    /// <summary>
    /// Handles a resolved failure (a failed <see cref="IOutboxPublisher.PublishAsync"/> call or a finalization
    /// exception) for the attempt the caller already knows was just granted. Unlike claiming, a resolved failure
    /// never needs to increment <see cref="OutboxMessage.AttemptCount"/> itself — that already happened
    /// atomically when this attempt was granted — so this method only decides, from the already-known
    /// <paramref name="attemptCount"/>, whether to schedule a retry or terminalize permanently.
    /// </summary>
    private async Task HandleFailureAsync(IMongoCollection<OutboxMessage> collection,
                                          ObjectId messageId,
                                          String lockId,
                                          Int64 attemptCount,
                                          Exception exception)
    {
        var failureFilter = Builders<OutboxMessage>.Filter.Eq(m => m.Id, messageId) &
                            Builders<OutboxMessage>.Filter.Eq(m => m.LockId, lockId);

        UpdateDefinition<OutboxMessage> failureUpdate;

        if (_options.MaxRetries.HasValue && attemptCount > _options.MaxRetries.Value)
        {
            // Permanently failed
            failureUpdate = Builders<OutboxMessage>.Update
                                                   .Set(m => m.State, OutboxMessageState.Failed)
                                                   .Set(m => m.FailedUtc, _timeProvider.GetUtcNow().UtcDateTime)
                                                   .Set(m => m.Error, exception.Message)
                                                   .Set(m => m.IsLocked, false)
                                                   .Unset(m => m.NextAttemptUtc)
                                                   .Unset(m => m.LockedUtc)
                                                   .Unset(m => m.LockId);

            _logger.LogWarning(
                "Outbox message {MessageId} in outbox '{OutboxIdentity}' (collection '{CollectionName}') " +
                "permanently failed after {AttemptCount} attempts",
                messageId, _options.Identity, _options.CollectionName, attemptCount);
        }
        else
        {
            // Schedule retry with exponential backoff
            var nextAttemptUtc = ComputeNextAttemptUtc(attemptCount);

            failureUpdate = Builders<OutboxMessage>.Update
                                                   .Set(m => m.Error, exception.Message)
                                                   .Set(m => m.NextAttemptUtc, nextAttemptUtc)
                                                   .Set(m => m.IsLocked, false)
                                                   .Unset(m => m.LockedUtc)
                                                   .Unset(m => m.LockId);

            _logger.LogWarning(
                "Outbox message {MessageId} in outbox '{OutboxIdentity}' (collection '{CollectionName}') " +
                "failed (attempt {AttemptCount}/{MaxRetries}); next attempt at {NextAttemptUtc}",
                messageId, _options.Identity, _options.CollectionName, attemptCount, _options.MaxRetries, nextAttemptUtc);
        }

        var result = await collection.UpdateOneAsync(failureFilter, failureUpdate);

        if (result.ModifiedCount == 0)
        {
            _logger.LogWarning(
                "Outbox message {MessageId} in outbox '{OutboxIdentity}' (collection '{CollectionName}') " +
                "failure update skipped — ownership was lost to another processor",
                messageId, _options.Identity, _options.CollectionName);
        }
    }

    private async Task MarkProcessedAsync(IMongoCollection<OutboxMessage> collection,
                                          OutboxMessage claimed,
                                          String lockId,
                                          CancellationToken cancellationToken)
    {
        var successFilter = Builders<OutboxMessage>.Filter.Eq(m => m.Id, claimed.Id) &
                            Builders<OutboxMessage>.Filter.Eq(m => m.LockId, lockId);

        var successUpdate = Builders<OutboxMessage>.Update
                                                   .Set(m => m.State, OutboxMessageState.Processed)
                                                   .Set(m => m.ProcessedUtc, _timeProvider.GetUtcNow().UtcDateTime)
                                                   .Set(m => m.IsLocked, false)
                                                   .Unset(m => m.NextAttemptUtc)
                                                   .Unset(m => m.LockedUtc)
                                                   .Unset(m => m.LockId);

        // The message has been delivered, so finalizing it must not be canceled by shutdown outright.
        // While the processor runs, only the driver bounds the update; once a stop is requested, a short
        // grace period keeps the stopping processor from waiting indefinitely.
        using var finalizationTimeout = new CancellationTokenSource(Timeout.InfiniteTimeSpan, _timeProvider);
        await using var shutdownRegistration = cancellationToken.Register(static state =>
        {
            if (state is CancellationTokenSource gracePeriod)
                gracePeriod.CancelAfter(FinalizationTimeout);
        }, finalizationTimeout);

        try
        {
            var successResult = await collection.UpdateOneAsync(successFilter, successUpdate, cancellationToken: finalizationTimeout.Token);

            if (successResult.ModifiedCount == 0)
            {
                _logger.LogWarning(
                    "Outbox message {MessageId} in outbox '{OutboxIdentity}' (collection '{CollectionName}') " +
                    "was published but ownership was lost before finalizing state; another processor may have reclaimed it",
                    claimed.Id, _options.Identity, _options.CollectionName);
            }
            else
            {
                _logger.LogDebug("Outbox message {MessageId} of type '{MessageType}' processed successfully", claimed.Id, claimed.Type);
            }
        }
        catch (OperationCanceledException) when (finalizationTimeout.IsCancellationRequested)
        {
            // Not a publish failure: release the lock so the message is redelivered instead of retried with an error
            _logger.LogWarning(
                "Outbox message {MessageId} in outbox '{OutboxIdentity}' (collection '{CollectionName}') " +
                "was published but finalizing its state timed out; attempting to release the lock so it can be redelivered",
                claimed.Id, _options.Identity, _options.CollectionName);
            await TryReleaseLockAsync(collection, claimed.Id, lockId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to finalize outbox message {MessageId} of type '{MessageType}' after publishing", claimed.Id, claimed.Type);
            await HandleFailureAsync(collection, claimed.Id, lockId, claimed.AttemptCount, ex);
        }
    }

    private async Task<Int32> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var collection = GetCollection();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var staleLockThreshold = now - _options.LockLeaseTime;

        // Query for eligible messages:
        // State == Pending AND (NextAttemptUtc is null OR NextAttemptUtc <= now)
        // AND (IsLocked == false OR LockedUtc <= staleLockThreshold)
        // ORDER BY NextAttemptUtc ASC, LockedUtc ASC, _id ASC, LIMIT batchSize
        var filter = Builders<OutboxMessage>.Filter.Eq(m => m.State, OutboxMessageState.Pending) &
                     (Builders<OutboxMessage>.Filter.Eq(m => m.NextAttemptUtc, null) |
                      Builders<OutboxMessage>.Filter.Lte(m => m.NextAttemptUtc, now)) &
                     (Builders<OutboxMessage>.Filter.Eq(m => m.IsLocked, false) |
                      Builders<OutboxMessage>.Filter.Lte(m => m.LockedUtc, staleLockThreshold));

        if (_options.ProcessingFilter is { } processingFilter)
        {
            filter &= processingFilter;
        }

        var sort = Builders<OutboxMessage>.Sort
                                          .Ascending(m => m.NextAttemptUtc)
                                          .Ascending(m => m.LockedUtc)
                                          .Ascending(m => m.Id);

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

        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var publisher = _publisherKey is null
            ? scope.ServiceProvider.GetRequiredService<IOutboxPublisher>()
            : scope.ServiceProvider.GetRequiredKeyedService<IOutboxPublisher>(_publisherKey);

        var processedMessageCount = 0;

        foreach (var message in messages)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Outbox processor cancellation requested");
                break;
            }

            await ProcessMessageAsync(collection, message, publisher, cancellationToken);
            processedMessageCount++;
        }

        _logger.LogDebug("Completed processing batch of {MessageCount} outbox messages", processedMessageCount);
        return processedMessageCount;
    }

    private async Task ProcessLoopAsync(CancellationToken cancellationToken)
    {
        using var logScope = BeginScope();
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
        var staleLockThreshold = now - _options.LockLeaseTime;
        var lockId = Guid.NewGuid().ToString();

        // Atomically claim the message. The claim itself decides, from the document's own pre-update
        // AttemptCount, whether to grant this claim a new attempt or to terminalize the message directly, so a
        // message that crashed on an earlier granted attempt (between claim and HandleFailureAsync/
        // MarkProcessedAsync) still reaches terminal via a later claim instead of cycling forever.
        var claimFilter = Builders<OutboxMessage>.Filter.Eq(m => m.Id, message.Id) &
                          Builders<OutboxMessage>.Filter.Eq(m => m.State, OutboxMessageState.Pending) &
                          (Builders<OutboxMessage>.Filter.Eq(m => m.NextAttemptUtc, null) |
                           Builders<OutboxMessage>.Filter.Lte(m => m.NextAttemptUtc, now)) &
                          (Builders<OutboxMessage>.Filter.Eq(m => m.IsLocked, false) |
                           Builders<OutboxMessage>.Filter.Lte(m => m.LockedUtc, staleLockThreshold));

        if (_options.ProcessingFilter is { } processingFilter)
        {
            claimFilter &= processingFilter;
        }

        var claimed = await collection.FindOneAndUpdateAsync(
            claimFilter,
            CreateClaimUpdate(now, lockId),
            new FindOneAndUpdateOptions<OutboxMessage>
            {
                ReturnDocument = ReturnDocument.After
            },
            cancellationToken);

        if (claimed is null)
        {
            _logger.LogDebug("Outbox message {MessageId} could not be claimed (no longer eligible or locked by another processor)", message.Id);
            return;
        }

        if (claimed.State == OutboxMessageState.Failed)
        {
            // This claim's own terminal branch already fired atomically: the message crashed on an earlier
            // granted attempt and this claim's own pre-update AttemptCount already exceeded MaxRetries, so it
            // terminalizes here without ever calling the publisher.
            _logger.LogWarning(
                "Outbox message {MessageId} in outbox '{OutboxIdentity}' (collection '{CollectionName}') " +
                "permanently failed after {AttemptCount} attempts without granting a further attempt",
                claimed.Id, _options.Identity, _options.CollectionName, claimed.AttemptCount);
            return;
        }

        try
        {
            await publisher.PublishAsync(claimed, cancellationToken);
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
            await HandleFailureAsync(collection, message.Id, lockId, claimed.AttemptCount, ex);
            return;
        }

        await MarkProcessedAsync(collection, claimed, lockId, cancellationToken);
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
                                                       .Unset(m => m.LockedUtc)
                                                       .Unset(m => m.LockId);

            using var releaseTimeout = new CancellationTokenSource(FinalizationTimeout);
            await collection.UpdateOneAsync(releaseFilter, releaseUpdate, cancellationToken: releaseTimeout.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to release lock on outbox message {MessageId} in outbox '{OutboxIdentity}' (collection '{CollectionName}') during shutdown",
                messageId, _options.Identity, _options.CollectionName);
        }
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        using (BeginScope())
        {
            if (_cancellationTokenSource is not null)
            {
                _logger.LogWarning(
                    "Outbox processor '{OutboxIdentity}' for collection '{CollectionName}' is already running; " +
                    "it is likely being started both automatically (WithAutoStartProcessor) and manually (StartAsync)",
                    _options.Identity, _options.CollectionName);
                return Task.CompletedTask;
            }

            _logger.LogInformation("Starting outbox processor for collection '{CollectionName}'", _options.CollectionName);
        }

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _processingTask = ProcessLoopAsync(_cancellationTokenSource.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        using var logScope = BeginScope();
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
