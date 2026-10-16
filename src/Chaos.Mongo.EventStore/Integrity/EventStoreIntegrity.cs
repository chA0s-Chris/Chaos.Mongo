// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

/// <summary>
/// The verification entry point of one protected aggregate type.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
internal sealed class EventStoreIntegrity<TAggregate> : IEventStoreIntegrity<TAggregate> where TAggregate : class, IAggregate, new()
{
    private readonly CompletenessChecker<TAggregate> _completeness;
    private readonly DerivedDataVerifier<TAggregate> _derivedData;
    private readonly EventStreamVerifier<TAggregate> _streams;
    private readonly EventStreamSealingSweep<TAggregate> _sweep;

    public EventStoreIntegrity(EventStreamVerifier<TAggregate> streams,
                               DerivedDataVerifier<TAggregate> derivedData,
                               CompletenessChecker<TAggregate> completeness,
                               EventStreamSealingSweep<TAggregate> sweep)
    {
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(derivedData);
        ArgumentNullException.ThrowIfNull(completeness);
        ArgumentNullException.ThrowIfNull(sweep);
        _streams = streams;
        _derivedData = derivedData;
        _completeness = completeness;
        _sweep = sweep;
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<CompletenessFinding> CheckCompletenessAsync(TrustedAnchorHead? trustedHead = null, CancellationToken cancellationToken = default)
        => _completeness.CheckAsync(trustedHead, cancellationToken);

    /// <inheritdoc/>
    public async Task<IntegritySealingStatus?> GetSealingStatusAsync(CancellationToken cancellationToken = default)
        => await _sweep.GetStatusAsync(cancellationToken) is { } state
            ? new IntegritySealingStatus(state.StreamsChecked, state.StreamsSealed, state.EventsSealed, state.PassStartedUtc, state.PassCompletedUtc)
            : null;

    /// <inheritdoc/>
    public Task<DerivedDataVerificationResult> VerifyDerivedDataAsync(Guid aggregateId, CancellationToken cancellationToken = default)
        => _derivedData.VerifyAsync(aggregateId, cancellationToken);

    /// <inheritdoc/>
    public Task<EventAnchorVerificationResult> VerifyEventAnchorAsync(Guid aggregateId, Int64 version, CancellationToken cancellationToken = default)
        => _streams.VerifyEventAnchorAsync(aggregateId, version, cancellationToken);

    /// <inheritdoc/>
    public Task<StreamVerificationResult> VerifyStreamAsync(Guid aggregateId, CancellationToken cancellationToken = default)
        => _streams.VerifyStreamAsync(aggregateId, cancellationToken);
}
