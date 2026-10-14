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
    /// Recomputes the hash chain of a stream and reports the first broken version. For an intact stream it also
    /// reports erased values that no later erasure event of the stream records.
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
        var discriminatorElement = EventDocumentFields<TAggregate>.Discriminator;

        using var cursor = await collection.Find(EventDocumentFields<TAggregate>.ForAggregate<RawBsonDocument>(aggregateId))
                                           .Sort(new BsonDocument(versionElement, 1))
                                           .ToCursorAsync(cancellationToken);

        var expectedVersion = 1L;
        var previousHash = EventIntegrityChain.ComputeGenesis(_aggregateTypeName, aggregateId);
        var erasures = new List<UnrecordedErasure>();
        var recordedErasures = new HashSet<UnrecordedErasure>();

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
                    var failure = VerifyEvent(rawDocument.ToBson(), integrityValue, previousHash, out var hash, out var erasedPaths);
                    if (failure is { } verificationFailure)
                    {
                        return StreamVerificationResult.Broken(expectedVersion, verificationFailure);
                    }

                    foreach (var path in erasedPaths)
                    {
                        erasures.Add(new UnrecordedErasure(expectedVersion, path));
                    }

                    // The erasure event is identified by its reserved discriminator; its records are trusted once its own hash verified.
                    if (rawDocument.TryGetValue(discriminatorElement, out var discriminator) &&
                        EventDocumentFields<TAggregate>.IsErasureEvent(discriminator))
                    {
                        CollectErasureRecords(rawDocument, expectedVersion, recordedErasures);
                    }

                    previousHash = hash;
                    expectedVersion++;
                }
            }
        }

        erasures.RemoveAll(recordedErasures.Contains);
        return StreamVerificationResult.IntactWith(erasures);
    }

    /// <summary>
    /// Reads the <c>(Version, Path)</c> records of an erasure event from its raw document. Only records of earlier
    /// versions document an erasure. Entries that do not have the pinned shape are ignored.
    /// </summary>
    private static void CollectErasureRecords(RawBsonDocument erasureEvent, Int64 erasureVersion, HashSet<UnrecordedErasure> recordedErasures)
    {
        if (!erasureEvent.TryGetValue(PersonalDataErased<TAggregate>.EntriesElement, out var entriesValue) ||
            entriesValue is not BsonArray entries)
        {
            return;
        }

        foreach (var entryValue in entries)
        {
            if (entryValue is not BsonDocument entry ||
                !entry.TryGetValue(PersonalDataErasureEntry.VersionElement, out var versionValue) ||
                !TryReadVersion(versionValue, out var version) ||
                version >= erasureVersion ||
                !entry.TryGetValue(PersonalDataErasureEntry.PathsElement, out var pathsValue) ||
                pathsValue is not BsonArray paths)
            {
                continue;
            }

            foreach (var path in paths)
            {
                if (path is BsonString { Value: var erasedPath })
                {
                    recordedErasures.Add(new UnrecordedErasure(version, erasedPath));
                }
            }
        }
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

    /// <summary>
    /// Verifies one event and reports the recorded paths whose values are erased.
    /// </summary>
    private static StreamVerificationFailure? VerifyEvent(Byte[] document,
                                                          BsonValue? integrityValue,
                                                          Byte[] expectedPreviousHash,
                                                          out Byte[] hash,
                                                          out List<String> erasedPaths)
    {
        hash = [];
        erasedPaths = [];
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

        var storedDocument = RawBsonElements.RemoveTopLevelElement(document, EventIntegrityChain.ElementName);
        var canonicalDocument = storedDocument;
        StreamVerificationFailure? personalDataFailure = null;

        // Only the recorded paths take part, so changing or removing marks later does not affect sealed events.
        if (integrity.PersonalData is { } personalData)
        {
            if (!PersonalDataCommitments.IsWellFormed(personalData))
            {
                return StreamVerificationFailure.MalformedIntegrity;
            }

            foreach (var commitment in personalData)
            {
                switch (PersonalDataCommitments.Verify(storedDocument, commitment))
                {
                    case PersonalDataVerification.Unresolved:
                        return StreamVerificationFailure.MalformedIntegrity;
                    case PersonalDataVerification.Mismatch:
                        personalDataFailure ??= StreamVerificationFailure.PersonalDataMismatch;
                        break;
                    case PersonalDataVerification.NotErased:
                        personalDataFailure ??= StreamVerificationFailure.ErasedPersonalDataPresent;
                        break;
                    case PersonalDataVerification.Erased:
                        erasedPaths.Add(commitment.Path);
                        break;
                }
            }

            canonicalDocument = PersonalDataCommitments.Canonicalize(storedDocument, personalData);
        }

        var recomputedHash = EventIntegrityChain.ComputeHash(canonicalDocument, integrity.PersonalData, expectedPreviousHash);
        if (!recomputedHash.AsSpan().SequenceEqual(integrity.Hash))
        {
            return StreamVerificationFailure.HashMismatch;
        }

        if (personalDataFailure is { } failure)
        {
            return failure;
        }

        hash = integrity.Hash;
        return null;
    }
}
