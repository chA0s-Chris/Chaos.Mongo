// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using MongoDB.Bson;
using MongoDB.Driver;

/// <summary>
/// Verifies the hash chain of an event stream by recomputing it from the raw stored documents.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
internal sealed class EventStreamVerifier<TAggregate> where TAggregate : class, IAggregate, new()
{
    private readonly String _aggregateTypeName = typeof(TAggregate).Name;
    private readonly IMongoHelper _mongoHelper;
    private readonly MongoEventStoreOptions<TAggregate> _options;

    public EventStreamVerifier(IMongoHelper mongoHelper, MongoEventStoreOptions<TAggregate> options)
    {
        ArgumentNullException.ThrowIfNull(mongoHelper);
        ArgumentNullException.ThrowIfNull(options);
        _mongoHelper = mongoHelper;
        _options = options;
    }

    /// <summary>
    /// Recomputes the hash chain of a stream and reports the first broken version.
    /// </summary>
    /// <param name="aggregateId">The aggregate identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The verification result.</returns>
    public async Task<StreamVerificationResult> VerifyStreamAsync(Guid aggregateId, CancellationToken cancellationToken = default)
    {
        var collection = _mongoHelper.Database.GetCollection<RawBsonDocument>(_options.EventsCollectionName);
        var versionElement = EventDocumentFields<TAggregate>.Version;

        using var cursor = await collection.Find(EventDocumentFields<TAggregate>.ForAggregate<RawBsonDocument>(aggregateId))
                                           .Sort(new BsonDocument(versionElement, 1))
                                           .ToCursorAsync(cancellationToken);

        var expectedVersion = 1L;
        var previousHash = EventIntegrityChain.ComputeGenesis(_aggregateTypeName, aggregateId);

        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var rawDocument in cursor.Current)
            {
                using (rawDocument)
                {
                    if (!rawDocument.TryGetValue(versionElement, out var version) ||
                        !version.IsNumeric ||
                        version.ToInt64() != expectedVersion)
                    {
                        return StreamVerificationResult.Broken(expectedVersion, StreamVerificationFailure.VersionGap);
                    }

                    rawDocument.TryGetValue(EventIntegrityChain.ElementName, out var integrityValue);
                    var failure = VerifyEvent(rawDocument.ToBson(), integrityValue, previousHash, out var hash);
                    if (failure is { } verificationFailure)
                    {
                        return StreamVerificationResult.Broken(expectedVersion, verificationFailure);
                    }

                    previousHash = hash;
                    expectedVersion++;
                }
            }
        }

        return StreamVerificationResult.Intact;
    }

    private static StreamVerificationFailure? VerifyEvent(Byte[] document,
                                                          BsonValue? integrityValue,
                                                          Byte[] expectedPreviousHash,
                                                          out Byte[] hash)
    {
        hash = [];
        if (integrityValue is null)
        {
            return StreamVerificationFailure.NotSealed;
        }

        if (!EventIntegrityChain.TryRead(integrityValue, out var integrity))
        {
            return StreamVerificationFailure.MalformedIntegrity;
        }

        if (integrity.FormatVersion != EventIntegrityChain.FormatVersion ||
            !String.Equals(integrity.Algorithm, EventIntegrityChain.Algorithm, StringComparison.Ordinal))
        {
            return StreamVerificationFailure.UnsupportedFormat;
        }

        if (!integrity.PreviousHash.AsSpan().SequenceEqual(expectedPreviousHash))
        {
            return StreamVerificationFailure.PreviousHashMismatch;
        }

        var canonicalDocument = RawBsonElements.RemoveTopLevelElement(document, EventIntegrityChain.ElementName);
        var recomputedHash = EventIntegrityChain.ComputeHash(canonicalDocument, expectedPreviousHash);

        if (!recomputedHash.AsSpan().SequenceEqual(integrity.Hash))
        {
            return StreamVerificationFailure.HashMismatch;
        }

        hash = integrity.Hash;
        return null;
    }
}
