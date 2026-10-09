// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Integrity;

using MongoDB.Bson;
using MongoDB.Driver;
using System.Globalization;

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
    /// <remarks>
    /// A consistent chain does not reveal deleted newest events or a deleted stream, because the
    /// remaining events still form a valid chain. Detecting that requires a trusted expected head,
    /// which anchoring provides.
    /// </remarks>
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
                    if (!rawDocument.TryGetValue(versionElement, out var versionValue) ||
                        !TryReadVersion(versionValue, out var version) ||
                        version != expectedVersion)
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

    /// <summary>
    /// Reads a stored version without throwing on tampered values. Only whole numbers within the
    /// <see cref="Int64"/> range are versions.
    /// </summary>
    private static Boolean TryReadVersion(BsonValue value, out Int64 version)
    {
        // 2^63, the first double above Int64.MaxValue.
        const Double int64Limit = 9223372036854775808d;

        switch (value)
        {
            case BsonInt32 int32:
                version = int32.Value;
                return true;
            case BsonInt64 int64:
                version = int64.Value;
                return true;
            case BsonDouble { Value: var number } when Double.IsInteger(number) && number >= -int64Limit && number < int64Limit:
                version = (Int64)number;
                return true;
            case BsonDecimal128 decimal128:
                return Int64.TryParse(decimal128.Value.ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out version);
            default:
                version = 0;
                return false;
        }
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
