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
    private readonly Object _gate = new();
    private readonly ILogger<OutboxProcessor> _logger;
    private readonly IMongoHelper _mongoHelper;
    private readonly OutboxOptions _options;
    private readonly Object? _publisherKey;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task _processingTask = Task.CompletedTask;
    private Task? _stopOperation;

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

    private static Exception? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>
    /// Rolls back a run whose start failed before its loop began. The loop is settled without ever running, which ends the
    /// wait of a stop that was admitted in the meantime; that stop then releases the run. Without such a stop the run is
    /// released here, so a later start succeeds.
    /// </summary>
    private void AbandonStart(TaskCompletionSource loopCompletion)
    {
        CancellationTokenSource? abandoned = null;
        lock (_gate)
        {
            if (_stopOperation is null && ReferenceEquals(_processingTask, loopCompletion.Task))
            {
                abandoned = _cancellationTokenSource;
                _cancellationTokenSource = null;
                _processingTask = Task.CompletedTask;
            }
        }

        abandoned?.Dispose();
        loopCompletion.SetResult();
    }

    private IDisposable? BeginScope() => _logger.BeginScope(new Dictionary<String, Object>
    {
        ["OutboxIdentity"] = _options.Identity,
        ["CollectionName"] = _options.CollectionName
    });

    /// <summary>
    /// Cancels <paramref name="source"/>. A cancellation callback that throws, for example one a publisher registered
    /// on its token, faults <see cref="CancellationTokenSource.CancelAsync"/> after every callback has run; the failure
    /// is logged instead of failing the stop.
    /// </summary>
    private async Task CancelLoggingFailuresAsync(CancellationTokenSource source)
    {
        try
        {
            await source.CancelAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                               "A cancellation callback failed while stopping outbox processor '{OutboxIdentity}' for collection '{CollectionName}'",
                               _options.Identity, _options.CollectionName);
        }
    }

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

        // One failed attempt counts once, whether the publisher threw or finalizing a published message failed.
        OutboxDiagnostics.RecordPublishFailed(_options.CollectionName);

        UpdateDefinition<OutboxMessage> failureUpdate;
        var permanentlyFailed = _options.MaxRetries.HasValue && attemptCount > _options.MaxRetries.Value;

        if (permanentlyFailed)
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
        else if (permanentlyFailed)
        {
            OutboxDiagnostics.RecordPublishTerminalFailed(_options.CollectionName);
        }
    }

    private async Task MarkProcessedAsync(IMongoCollection<OutboxMessage> collection,
                                          OutboxMessage claimed,
                                          String lockId,
                                          TimeSpan publishDuration,
                                          CancellationToken cancellationToken)
    {
        var successFilter = Builders<OutboxMessage>.Filter.Eq(m => m.Id, claimed.Id) &
                            Builders<OutboxMessage>.Filter.Eq(m => m.LockId, lockId);

        var processedUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var successUpdate = Builders<OutboxMessage>.Update
                                                   .Set(m => m.State, OutboxMessageState.Processed)
                                                   .Set(m => m.ProcessedUtc, processedUtc)
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
                // Recorded only for the transition to Processed, so a publish whose finalization failed, timed out
                // or lost ownership is counted by the redelivery that completes it. A creation timestamp in the
                // future (writer clock skew) would yield a negative age, so it is clamped to zero.
                var messageAge = processedUtc - claimed.CreatedUtc;
                OutboxDiagnostics.RecordPublished(_options.CollectionName,
                                                  publishDuration,
                                                  messageAge < TimeSpan.Zero ? TimeSpan.Zero : messageAge);
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
            OutboxDiagnostics.RecordPublishTerminalFailed(_options.CollectionName);
            return;
        }

        var publishStartTimestamp = _timeProvider.GetTimestamp();

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

        var publishDuration = _timeProvider.GetElapsedTime(publishStartTimestamp);
        await MarkProcessedAsync(collection, claimed, lockId, publishDuration, cancellationToken);
    }

    /// <summary>
    /// The one stop operation of a run, shared by every overlapping <see cref="StopAsync"/>: it cancels the loop's token,
    /// joins the loop, and only then disposes the token source and clears the state, whatever happens to its callers'
    /// own waits. Disposing the source earlier would drop cancellation callbacks that have not run yet.
    /// </summary>
    private async Task RunStopAsync(TaskCompletionSource operation, CancellationTokenSource source, Task loop)
    {
        // The operation always cancels, joins, cleans up and settles, however diagnostic logging behaves. A logging
        // failure surfaces as the failure of the operation after the cleanup instead of stranding the callers.
        Exception? failure = null;
        IDisposable? scope = null;
        failure ??= Capture(() =>
        {
            scope = BeginScope();
            _logger.LogInformation("Stopping outbox processor for collection '{CollectionName}'", _options.CollectionName);
        });

        try
        {
            await CancelLoggingFailuresAsync(source);
        }
        catch (Exception ex)
        {
            failure ??= ex;
        }

        try
        {
            await loop;
        }
        catch (Exception ex)
        {
            failure ??= ex;
        }

        // The source is released while the stop is still published: an overlapping stop keeps waiting for this operation and
        // a start still sees the run as stopping, until the operation is settled together with the state below.
        source.Dispose();

        // Each cleanup runs whatever an earlier step captured; the first failure alone becomes the operation's failure.
        var stoppedFailure = Capture(() => _logger.LogInformation("Outbox processor stopped for collection '{CollectionName}'", _options.CollectionName));
        var scopeFailure = Capture(() => scope?.Dispose());
        failure ??= stoppedFailure ?? scopeFailure;

        // Only the state of the run this operation stops is cleared; a later run has its own source. Clearing and
        // settling are one transition, so no caller or start can observe the run as stopped before the operation ends.
        lock (_gate)
        {
            if (ReferenceEquals(_cancellationTokenSource, source))
            {
                _cancellationTokenSource = null;
                _processingTask = Task.CompletedTask;
                _stopOperation = null;
            }

            if (failure is null)
            {
                operation.SetResult();
            }
            else
            {
                operation.SetException(failure);
            }
        }

        // Every caller may have abandoned its wait, so the operation observes its own fault.
        _ = operation.Task.Exception;
    }

    /// <summary>
    /// Runs the polling loop and mirrors its outcome into <paramref name="completion"/>, which a stop awaits. The loop
    /// starts synchronously on the caller's thread, outside the gate.
    /// </summary>
    private async Task TrackLoopAsync(CancellationToken cancellationToken, TaskCompletionSource completion)
    {
        try
        {
            await ProcessLoopAsync(cancellationToken);
            completion.SetResult();
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
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
        TaskCompletionSource? loopCompletion = null;
        CancellationToken loopToken = default;
        lock (_gate)
        {
            if (_cancellationTokenSource is null)
            {
                _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                loopCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _processingTask = loopCompletion.Task;
                loopToken = _cancellationTokenSource.Token;
            }
        }

        try
        {
            using (BeginScope())
            {
                if (loopCompletion is null)
                {
                    _logger.LogWarning(
                        "Outbox processor '{OutboxIdentity}' for collection '{CollectionName}' is already running; " +
                        "it is likely being started both automatically (WithAutoStartProcessor) and manually (StartAsync)",
                        _options.Identity, _options.CollectionName);
                    return Task.CompletedTask;
                }

                _logger.LogInformation("Starting outbox processor for collection '{CollectionName}'", _options.CollectionName);
            }
        }
        catch
        {
            // A start that fails never reaches the publisher. The run published above is rolled back instead.
            if (loopCompletion is not null)
            {
                AbandonStart(loopCompletion);
            }

            throw;
        }

        _ = TrackLoopAsync(loopToken, loopCompletion);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource? created = null;
        Task operation;
        CancellationTokenSource source;
        Task loop;
        lock (_gate)
        {
            if (_cancellationTokenSource is null)
            {
                return;
            }

            if (_stopOperation is null)
            {
                created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _stopOperation = created.Task;
            }

            operation = _stopOperation;
            source = _cancellationTokenSource;
            loop = _processingTask;
        }

        // The operation is published above, so its work starts only now. It runs off this caller's stack: its diagnostic
        // logging and scope calls are provider code that may block, and they must not keep this caller, whose own token
        // limits its wait, from returning.
        if (created is not null)
        {
            _ = Task.Run(() => RunStopAsync(created, source, loop), CancellationToken.None);
        }

        try
        {
            await operation.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
    }
}
