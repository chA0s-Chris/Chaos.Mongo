// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Integrity;
using Chaos.Mongo.EventStore.Tests.Integrity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using NUnit.Framework;
using System.Diagnostics;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Testcontainers.MongoDb;

/// <summary>
/// Runs the Python reference verifier, which verifies tokens and CRLs with OpenSSL, against the committed test vectors
/// and against data produced by the library. On CI a missing <c>python3</c> or <c>openssl</c> fails these tests;
/// locally they are ignored.
/// </summary>
public class ReferenceVerifierIntegrationTests
{
    private const String CertificateIssuerOid = "2.5.29.29";
    private const String InvalidityDateOid = "2.5.29.24";
    private const String IssuingDistributionPointOid = "2.5.29.28";
    private const String ReasonCodeOid = "2.5.29.21";
    private readonly List<LedgerStoreContext> _contexts = [];
    private readonly List<TestTimestampAuthority> _testAuthorities = [];
    private MongoDbContainer _container;
    private String _directory;

    [Test]
    public async Task Check_AnchorDateOutsideTheReadableYears_MakesTheAnchorMalformedLikeTheLibrary()
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        // The operational dates are not in the anchor digest, so this is a plain write; .NET cannot read a date after 9999.
        await GetStore(context).Anchors.UpdateOneAsync(FilterDefinition<BsonDocument>.Empty,
                                                       Builders<BsonDocument>.Update.Set("PeriodStartUtc", new BsonDateTime(253402300800001)));

        await DumpAsync(context);
        var (_, report) = await RunVerifierAsync(CheckArguments(aggregateId));
        var chain = await context.ServiceProvider.GetRequiredService<AnchorVerifier>().VerifyAnchorChainAsync();

        chain.Failure.Should().Be(AnchorChainFailure.MalformedAnchor);
        await AssertReportMatchesLibraryAsync(context, aggregateId, report);
    }

    [Test]
    [TestCase("leaf")]
    [TestCase("anchor")]
    public async Task Check_AnchorDocumentNameNotValidUtf8_ReportsLikeTheLibrary(String document)
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        var store = GetStore(context);
        var typed = document == "leaf" ? store.Leaves : store.Anchors;
        var collection = typed.Database.GetCollection<RawBsonDocument>(typed.CollectionNamespace.CollectionName);
        var raw = await collection.Find(FilterDefinition<RawBsonDocument>.Empty).SingleAsync();
        var bytes = raw.ToBson();
        // The second element's name comes before Sequence and Version; the identifier is an ObjectId or a UUID.
        var identifierEnd = 4 + 1 + 4 + (bytes[4] == 0x07 ? 12 : 5 + BitConverter.ToInt32(bytes, 4 + 1 + 4));
        bytes[identifierEnd + 1] = 0xFF;
        await collection.DeleteOneAsync(new BsonDocument("_id", raw["_id"]));
        await collection.InsertOneAsync(new RawBsonDocument(bytes));

        await DumpAsync(context);
        var (code, report) = await RunVerifierAsync(CheckArguments(aggregateId));
        var stream = await context.CreateVerifier().VerifyStreamAsync(aggregateId);
        var chain = await context.ServiceProvider.GetRequiredService<AnchorVerifier>().VerifyAnchorChainAsync();

        code.Should().Be(1);
        stream.Should().Be(StreamVerificationResult.Broken(3, StreamVerificationFailure.AnchoredLeafInvalid));
        (report["stream"]?["failure"]?.GetValue<String>()).Should().Be(nameof(StreamVerificationFailure.AnchoredLeafInvalid));
        (report["stream"]?["firstBrokenVersion"]?.GetValue<Int64>()).Should().Be(3);
        (report["anchorChain"]?["failure"]?.GetValue<String>()).Should().Be(chain.Failure?.ToString());
        (report["anchorChain"]?["firstBrokenSequence"]?.GetValue<Int64>()).Should().Be(chain.FirstBrokenSequence);
    }

    [Test]
    [TestCase("--trusted-head", "1:00")]
    [TestCase("--policy", "test-tsa=1.2.3.4")]
    [TestCase("--grace-hours", "48")]
    public async Task Check_AnchorOptionWithoutAnchors_IsAUsageError(String option, String value)
    {
        var (code, _, error) = await RunProcessAsync([
            "check", "--events", Path.Combine(_directory, "events.bson"), "--aggregate-type", nameof(LedgerAggregate),
            "--aggregate-id", Guid.NewGuid().ToString(), option, value
        ]);

        code.Should().Be(2);
        error.Should().Contain("need --anchors");
    }

    [Test]
    [TestCase("selectingArray", nameof(StreamVerificationFailure.VersionGap))]
    [TestCase("personalDataArray", null)]
    [TestCase("timestampsArray", null)]
    public async Task Check_ArrayElementNameNotValidUtf8_IsIgnoredLikeTheLibrary(String place, String? failure)
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        switch (place)
        {
            case "selectingArray":
                // A document without a version that MongoDB selects through the array, so it sorts first.
                var selected = new BsonDocument("AggregateId", new BsonArray
                {
                    new BsonBinaryData(aggregateId, GuidRepresentation.Standard)
                }).ToBson();
                selected[selected.AsSpan().IndexOf([(Byte)0x05, (Byte)'0', (Byte)0x00]) + 1] = 0xFF;
                await context.MongoHelper.Database.GetCollection<RawBsonDocument>(context.Options.EventsCollectionName).InsertOneAsync(new RawBsonDocument(selected));
                break;
            case "personalDataArray":
                var bytes = await context.ReadRawEventAsync(aggregateId, 1);
                bytes[bytes.AsSpan().IndexOf("PersonalData\0"u8) + "PersonalData\0"u8.Length + 4 + 1] = 0xFF;
                await ReplaceRawEventAsync(context, aggregateId, 1, bytes);
                break;
            default:
                var anchors = context.MongoHelper.Database.GetCollection<RawBsonDocument>(GetStore(context).Anchors.CollectionNamespace.CollectionName);
                var anchor = await anchors.Find(FilterDefinition<RawBsonDocument>.Empty).SingleAsync();
                var anchorBytes = anchor.ToBson();
                anchorBytes[anchorBytes.AsSpan().IndexOf("Timestamps\0"u8) + "Timestamps\0"u8.Length + 4 + 1] = 0xFF;
                await anchors.DeleteOneAsync(new BsonDocument("_id", anchor["_id"]));
                await anchors.InsertOneAsync(new RawBsonDocument(anchorBytes));
                break;
        }

        await DumpAsync(context);
        var (_, report) = await RunVerifierAsync(CheckArguments(aggregateId));
        var stream = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        stream.Failure?.ToString().Should().Be(failure);
        await AssertReportMatchesLibraryAsync(context, aggregateId, report);
    }

    [Test]
    [TestCase("eventAggregateId")]
    [TestCase("leafVersion")]
    [TestCase("leafAggregateType")]
    [TestCase("leafAnchorId")]
    public async Task Check_ArrayValuedSelectionElement_ReportsLikeTheLibrary(String change)
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        var leaves = GetStore(context).Leaves;
        var leaf = await leaves.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();

        BsonArray Wrap(String name) => new()
        {
            leaf[name]
        };

        if (change == "eventAggregateId")
        {
            var copy = (await context.Events.Find(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 3)).SingleAsync()).DeepClone().AsBsonDocument;
            copy["_id"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard);
            copy["AggregateId"] = new BsonArray
            {
                copy["AggregateId"]
            };
            copy["Version"] = 99L;
            await context.Events.InsertOneAsync(copy);
        }
        else
        {
            var name = change switch
            {
                "leafVersion" => "Version",
                "leafAggregateType" => "AggregateType",
                _ => AnchorStore.AnchorIdField
            };
            await leaves.UpdateOneAsync(new BsonDocument("_id", leaf["_id"]), Builders<BsonDocument>.Update.Set(name, Wrap(name)));
        }

        await DumpAsync(context);
        var (_, report) = await RunVerifierAsync(CheckArguments(aggregateId));
        var stream = await context.CreateVerifier().VerifyStreamAsync(aggregateId);
        var chain = await context.ServiceProvider.GetRequiredService<AnchorVerifier>().VerifyAnchorChainAsync();

        stream.IsIntact.Should().BeFalse();
        (report["stream"]?["firstBrokenVersion"]?.GetValue<Int64>(), report["stream"]?["failure"]?.GetValue<String>())
            .Should().Be((stream.FirstBrokenVersion, stream.Failure?.ToString()), report.ToJsonString());
        (report["anchorChain"]?["firstBrokenSequence"]?.GetValue<Int64?>(), report["anchorChain"]?["failure"]?.GetValue<String?>())
            .Should().Be((chain.FirstBrokenSequence, chain.Failure?.ToString()), report.ToJsonString());
    }

    [Test]
    public async Task Check_ChunkCountNotMatchingTheLength_IsInvalidLikeTheLibrary()
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        var store = new AnchorEvidenceStore(context.MongoHelper, context.ServiceProvider.GetRequiredService<IntegrityAnchoringOptions>());
        var hash = await store.StoreAsync(RandomNumberGenerator.GetBytes(AnchorEvidenceStore.ChunkSize + 1), CancellationToken.None);
        await store.Evidence.UpdateOneAsync(new BsonDocument("_id", new BsonBinaryData(hash)),
                                            Builders<BsonDocument>.Update.Set(AnchorEvidenceStore.ChunkCountField, 1_000_000));
        await GetStore(context).Anchors.UpdateOneAsync(FilterDefinition<BsonDocument>.Empty,
                                                       Builders<BsonDocument>.Update.Push("Timestamps.0.CertificateRevocationListHashes", new BsonBinaryData(hash)));

        await DumpAsync(context);
        var (code, report) = await RunVerifierAsync(CheckArguments(aggregateId));
        var library = await VerifyTimestampWithLibraryAsync(context);

        code.Should().Be(1);
        library.IsValid.Should().BeFalse();
        (report["anchors"]?[0]?["timestamps"]?[0]?["failure"]?.GetValue<String>()).Should().Contain("malformed");
    }

    [Test]
    public async Task Check_ChunkedStampingTimeEvidence_IsResolvedFromItsChunks()
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        var options = context.ServiceProvider.GetRequiredService<IntegrityAnchoringOptions>();
        var item = new Byte[(2 * AnchorEvidenceStore.ChunkSize) + 17];
        RandomNumberGenerator.Fill(item);
        var hash = await new AnchorEvidenceStore(context.MongoHelper, options).StoreAsync(item, CancellationToken.None);
        await GetStore(context).Anchors.UpdateOneAsync(FilterDefinition<BsonDocument>.Empty,
                                                       Builders<BsonDocument>.Update.Push("Timestamps.0.CertificateRevocationListHashes", new BsonBinaryData(hash)));

        await DumpAsync(context);
        var (resolvedCode, resolved) = await RunVerifierAsync(CheckArguments(aggregateId));
        await context.MongoHelper.Database.GetCollection<BsonDocument>(options.AnchorEvidenceChunksCollectionName)
                     .DeleteOneAsync(new BsonDocument("_id", new BsonDocument
                     {
                         { "Hash", new BsonBinaryData(hash) },
                         { "Index", 1 }
                     }));
        await DumpAsync(context);
        var (missingCode, missing) = await RunVerifierAsync(CheckArguments(aggregateId));

        resolvedCode.Should().Be(0, resolved.ToJsonString());
        missingCode.Should().Be(1);
        (missing["anchors"]?[0]?["timestamps"]?[0]?["failure"]?.GetValue<String>()).Should().Contain("chunk");
    }

    [Test]
    [TestCase("revokedBeforeTokenTime", false, nameof(TimestampRevocationStatus.Revoked))]
    [TestCase("revokedAfterTokenTime", true, nameof(TimestampRevocationStatus.Confirmed))]
    [TestCase("revokedIssuedWithinGracePeriod", false, nameof(TimestampRevocationStatus.Revoked))]
    [TestCase("invalidBeforeTokenTime", false, nameof(TimestampRevocationStatus.Revoked))]
    [TestCase("invalidityDateRepeated", true, nameof(TimestampRevocationStatus.Confirmed))]
    [TestCase("removedFromCrl", true, nameof(TimestampRevocationStatus.Confirmed))]
    [TestCase("otherCertificateIssuer", true, nameof(TimestampRevocationStatus.Confirmed))]
    [TestCase("certificateIssuerWithoutDirectoryName", false, nameof(TimestampRevocationStatus.Revoked))]
    [TestCase("unknownEntryExtensionWithEmptyValue", true, nameof(TimestampRevocationStatus.Confirmed))]
    [TestCase("issuingPointOfOtherLocation", true, nameof(TimestampRevocationStatus.Unconfirmed))]
    [TestCase("issuingPointOfEquivalentLocation", true, nameof(TimestampRevocationStatus.Confirmed))]
    [TestCase("onlyUserCertificates", true, nameof(TimestampRevocationStatus.Confirmed))]
    [TestCase("unknownCriticalExtension", true, nameof(TimestampRevocationStatus.Unconfirmed))]
    [TestCase("nonMinimalSerialNumber", true, nameof(TimestampRevocationStatus.Unconfirmed))]
    [TestCase("nonDerNextUpdate", true, nameof(TimestampRevocationStatus.Unconfirmed))]
    public async Task Check_ConfirmingCrlCase_MatchesTheLibrary(String crlCase, Boolean valid, String revocationStatus)
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        var anchor = await GetStore(context).Anchors.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        var tokenTime = new DateTimeOffset(anchor["Timestamps"][0]["TimestampUtc"].ToUniversalTime());
        var hash = await new AnchorEvidenceStore(context.MongoHelper, context.ServiceProvider.GetRequiredService<IntegrityAnchoringOptions>())
            .StoreAsync(BuildConfirmingCrl(_testAuthorities[0], crlCase, tokenTime), CancellationToken.None);
        await GetStore(context).Anchors.UpdateOneAsync(FilterDefinition<BsonDocument>.Empty,
                                                       Builders<BsonDocument>.Update.Set("Timestamps.0.ConfirmingRevocationListHashes",
                                                                                         new BsonArray
                                                                                         {
                                                                                             new BsonBinaryData(hash)
                                                                                         }));

        await DumpAsync(context);
        var (code, report) = await RunVerifierAsync(CheckArguments(aggregateId));
        var library = await VerifyTimestampWithLibraryAsync(context);
        var timestamp = report["anchors"]?[0]?["timestamps"]?[0];

        (library.IsValid, library.RevocationStatus.ToString()).Should().Be((valid, revocationStatus), library.Failure);
        (timestamp?["valid"]?.GetValue<Boolean>(), timestamp?["revocationStatus"]?.GetValue<String>()).Should().Be((valid, revocationStatus), report.ToJsonString());
        code.Should().Be(valid ? 0 : 1);
    }

    [Test]
    [TestCase(false)]
    [TestCase(true)]
    public async Task Check_ConfirmingCrl_CountsOnlyWhenComplete(Boolean deltaCrl)
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        var anchor = await GetStore(context).Anchors.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        var tokenTime = anchor["Timestamps"][0]["TimestampUtc"].ToUniversalTime();
        Byte[] crlNumber = [0x02, 0x01, 0x01];
        var crl = _testAuthorities[0].BuildCrlWithExtensions(tokenTime.AddHours(25), (deltaCrl ? "2.5.29.27" : "2.5.29.20", deltaCrl, crlNumber));
        var hash = await new AnchorEvidenceStore(context.MongoHelper, context.ServiceProvider.GetRequiredService<IntegrityAnchoringOptions>())
            .StoreAsync(crl, CancellationToken.None);
        await GetStore(context).Anchors.UpdateOneAsync(FilterDefinition<BsonDocument>.Empty,
                                                       Builders<BsonDocument>.Update.Set("Timestamps.0.ConfirmingRevocationListHashes",
                                                                                         new BsonArray
                                                                                         {
                                                                                             new BsonBinaryData(hash)
                                                                                         }));

        await DumpAsync(context);
        var (code, report) = await RunVerifierAsync(CheckArguments(aggregateId));

        code.Should().Be(0, report.ToJsonString());
        (report["anchors"]?[0]?["timestamps"]?[0]?["revocationStatus"]?.GetValue<String>()).Should().Be(deltaCrl ? "Unconfirmed" : "Confirmed");
    }

    [Test]
    public async Task Check_DeeplyNestedAnchorDocument_ReportsMalformedAnchorInsteadOfFailing()
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        await DumpAsync(context);
        var nested = new Byte[]
        {
            5,
            0,
            0,
            0,
            0
        };
        for (var depth = 0; depth < 2000; depth++)
        {
            // { "0": nested } as an array value.
            Byte[] body = [0x04, (Byte)'0', 0x00, .. nested, 0x00];
            nested = [.. BitConverter.GetBytes(body.Length + 4), .. body];
        }

        Byte[] fields = [0x12, .. "Sequence"u8, 0x00, .. BitConverter.GetBytes(2L), 0x04, .. "Deep"u8, 0x00, .. nested, 0x00];
        await using (var anchors = File.Open(Path.Combine(_directory, "anchors.bson"), FileMode.Append))
        {
            Byte[] document = [.. BitConverter.GetBytes(fields.Length + 4), .. fields];
            await anchors.WriteAsync(document);
        }

        var (code, report) = await RunVerifierAsync(CheckArguments(aggregateId));

        code.Should().Be(1);
        (report["anchorChain"]?["failure"]?.GetValue<String>()).Should().Be(nameof(AnchorChainFailure.MalformedAnchor));
        (report["anchorChain"]?["firstBrokenSequence"]?.GetValue<Int64>()).Should().Be(2);
    }

    [Test]
    [TestCase("topLevel")]
    [TestCase("nestedInPersonalDataPath")]
    public async Task Check_ElementNameNotValidUtf8_ReportsMalformedIntegrityLikeTheLibrary(String place)
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        var bytes = await context.ReadRawEventAsync(aggregateId, 2);
        Byte[] name = place == "topLevel" ? [0x03, .. "Counterparty"u8, 0x00] : [0x02, .. "Name"u8, 0x00];
        bytes[bytes.AsSpan().IndexOf(name) + 1] = 0xFF;
        await context.Events.DeleteOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 2));
        await context.MongoHelper.Database.GetCollection<RawBsonDocument>(context.Options.EventsCollectionName).InsertOneAsync(new RawBsonDocument(bytes));

        await DumpAsync(context);
        var (code, report) = await RunVerifierAsync(CheckArguments(aggregateId));
        var library = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        code.Should().Be(1);
        library.Should().Be(StreamVerificationResult.Broken(2, StreamVerificationFailure.MalformedIntegrity));
        (report["stream"]?["failure"]?.GetValue<String>()).Should().Be(nameof(StreamVerificationFailure.MalformedIntegrity));
        (report["stream"]?["firstBrokenVersion"]?.GetValue<Int64>()).Should().Be(2);
    }

    [Test]
    [TestCase("skippedEntryWithUndecodableName", null)]
    [TestCase("uuidOfFifteenBytes", nameof(StreamVerificationFailure.MalformedIntegrity))]
    [TestCase("versionNotValidUtf8", nameof(StreamVerificationFailure.MalformedIntegrity))]
    [TestCase("entriesNotValidUtf8", nameof(StreamVerificationFailure.MalformedIntegrity))]
    [TestCase("codeWithUndecodableScope", nameof(StreamVerificationFailure.MalformedIntegrity))]
    [TestCase("discriminatorFirstItemUndecodable", null)]
    [TestCase("dbPointerBeforeVersion", nameof(StreamVerificationFailure.MalformedIntegrity))]
    public async Task Check_ErasureEntry_IsReadLikeTheLibrary(String entry, String? failure)
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        await context.Eraser.ErasePersonalDataAsync(aggregateId, new ErasureRequest("TICKET-1", [1], ["Memo"]));
        var document = await context.FindEventAsync(aggregateId, 4);
        // An entry for a version at or above the erasure event's is skipped after its version was read, so its later
        // names are never decoded; the entries, every item of them and the versions read are values the library reads,
        // while of a discriminator array only the last item is read.
        var entries = document[PersonalDataErased<LedgerAggregate>.EntriesElement].AsBsonArray;
        switch (entry)
        {
            case "uuidOfFifteenBytes":
                entries.Add(new BsonBinaryData(new Byte[15]));
                break;
            case "skippedEntryWithUndecodableName":
                entries.Add(new BsonDocument
                {
                    { "Version", 5L },
                    { "x", 1 }
                });
                break;
            case "versionNotValidUtf8":
                entries.Add(new BsonDocument("Version", "x"));
                break;
            case "entriesNotValidUtf8":
                document[PersonalDataErased<LedgerAggregate>.EntriesElement] = "x";
                break;
            case "codeWithUndecodableScope":
                entries.Add(new BsonJavaScriptWithScope("c", new BsonDocument("s", "x")));
                break;
            case "dbPointerBeforeVersion":
                // A string of the size of a DBPointer, which the driver cannot write, is turned into one in place.
                entries.Add(new BsonDocument
                {
                    { "p", new String('p', 13) },
                    { "Version", 1L },
                    {
                        "Paths", new BsonArray
                        {
                            "Memo"
                        }
                    }
                });
                break;
            default:
                var discriminator = document["_t"];
                document["_t"] = new BsonArray
                {
                    "x"
                }.AddRange(discriminator as BsonArray ?? [discriminator]);
                break;
        }

        var bytes = document.ToBson();
        switch (entry)
        {
            case "uuidOfFifteenBytes":
                Byte[] item = [0x0F, 0x00, 0x00, 0x00, 0x00, .. new Byte[15]];
                bytes[bytes.AsSpan().IndexOf(item) + 4] = (Byte)BsonBinarySubType.UuidStandard;
                break;
            case "skippedEntryWithUndecodableName":
                bytes[bytes.AsSpan().IndexOf([(Byte)0x10, (Byte)'x', (Byte)0x00]) + 1] = 0xFF;
                break;
            case "dbPointerBeforeVersion":
                Byte[] placeholder = [0x02, (Byte)'p', 0x00, 0x0E, 0x00, 0x00, 0x00, .. Enumerable.Repeat((Byte)'p', 13), 0x00];
                Byte[] dbPointer = [0x0C, (Byte)'p', 0x00, 0x02, 0x00, 0x00, 0x00, (Byte)'x', 0x00, .. new Byte[12]];
                dbPointer.CopyTo(bytes, bytes.AsSpan().IndexOf(placeholder));
                break;
            default:
                bytes[bytes.AsSpan().IndexOf([(Byte)0x02, (Byte)0x00, (Byte)0x00, (Byte)0x00, (Byte)'x', (Byte)0x00]) + 4] = 0xFF;
                break;
        }

        var integrity = LedgerStoreContext.ReadIntegrity(document);
        EventIntegrityChain.ComputeHash(RawBsonElements.RemoveTopLevelElement(bytes, EventIntegrityChain.ElementName), integrity.PreviousHash, integrity.SealMode)
                           .CopyTo(bytes, bytes.AsSpan().IndexOf(integrity.Hash));
        await ReplaceRawEventAsync(context, aggregateId, 4, bytes);

        await DumpAsync(context);
        var (_, report) = await RunVerifierAsync(CheckArguments(aggregateId));
        var stream = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        stream.Failure?.ToString().Should().Be(failure);
        await AssertReportMatchesLibraryAsync(context, aggregateId, report);
    }

    [Test]
    public async Task Check_EventElementsRenamedByConvention_AreFoundWithElementNames()
    {
        ConventionRegistry.Register(nameof(Check_EventElementsRenamedByConvention_AreFoundWithElementNames),
                                    new ConventionPack
                                    {
                                        new CamelCaseElementNameConvention()
                                    },
                                    type => type == typeof(Event<RenamedElementsAggregate>));
        var services = new ServiceCollection();
        services.AddMongo(new MongoUrl(_container.GetConnectionString()),
                          configure: options =>
                          {
                              options.DefaultDatabase = $"RenamedElementsDb_{Guid.NewGuid():N}";
                              options.RunConfiguratorsOnStartup = false;
                          })
                .WithEventStore<RenamedElementsAggregate>(es => es.WithEvent<RenamedElementsEvent>("RenamedElementsEvent").WithIntegrityProtection());
        await using var provider = services.BuildServiceProvider();
        var aggregateId = Guid.NewGuid();
        await provider.GetRequiredService<IEventStore<RenamedElementsAggregate>>().AppendEventsAsync([
            new RenamedElementsEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = aggregateId,
                Version = 1
            },
            new RenamedElementsEvent
            {
                Id = Guid.NewGuid(),
                AggregateId = aggregateId,
                Version = 2
            }
        ]);
        var options = provider.GetRequiredService<MongoEventStoreOptions<RenamedElementsAggregate>>();
        var library = await new EventStreamVerifier<RenamedElementsAggregate>(provider.GetRequiredService<IMongoHelper>(), options).VerifyStreamAsync(aggregateId);
        var file = Path.Combine(_directory, "renamed-events.bson");
        await using (var output = File.Create(file))
        {
            foreach (var document in await provider.GetRequiredService<IMongoHelper>().Database.GetCollection<RawBsonDocument>(options.EventsCollectionName)
                                                   .Find(FilterDefinition<RawBsonDocument>.Empty).ToListAsync())
            {
                using (document)
                {
                    await output.WriteAsync(document.ToBson());
                }
            }
        }

        String[] arguments = ["check", "--events", file, "--aggregate-type", nameof(RenamedElementsAggregate), "--aggregate-id", aggregateId.ToString()];
        var (defaultCode, defaultReport) = await RunVerifierAsync(arguments);
        var (namedCode, namedReport) = await RunVerifierAsync([
            .. arguments, "--element-name", "AggregateId=aggregateId", "--element-name", "AggregateType=aggregateType", "--element-name", "Version=version"
        ]);

        library.IsIntact.Should().BeTrue();
        (defaultCode, defaultReport["eventCount"]?.GetValue<Int32>()).Should().Be((1, 0), defaultReport.ToJsonString());
        defaultReport["warnings"].Should().NotBeNull();
        (namedCode, namedReport["eventCount"]?.GetValue<Int32>(), namedReport["stream"]?["intact"]?.GetValue<Boolean>())
            .Should().Be((0, 2, true), namedReport.ToJsonString());
    }

    [Test]
    [TestCase("unset", 1)]
    [TestCase("string", 3)]
    public async Task Check_EventWithoutReadableVersion_BreaksWhereTheLibraryBreaks(String change, Int64 brokenVersion)
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        var version = change == "unset" ? 2 : 3;
        await context.Events.UpdateOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, version),
                                            change == "unset"
                                                ? Builders<BsonDocument>.Update.Unset("Version")
                                                : Builders<BsonDocument>.Update.Set("Version", version.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        await DumpAsync(context);
        var (code, report) = await RunVerifierAsync(CheckArguments(aggregateId));
        var library = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        code.Should().Be(1);
        library.Should().Be(StreamVerificationResult.Broken(brokenVersion, StreamVerificationFailure.VersionGap));
        (report["stream"]?["failure"]?.GetValue<String>()).Should().Be(nameof(StreamVerificationFailure.VersionGap));
        (report["stream"]?["firstBrokenVersion"]?.GetValue<Int64>()).Should().Be(brokenVersion);
    }

    [Test]
    public async Task Check_EvidenceDataStoredAsOldBinary_IsInvalidLikeTheLibrary()
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        var anchor = await GetStore(context).Anchors.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        var evidence = new AnchorEvidenceStore(context.MongoHelper, context.ServiceProvider.GetRequiredService<IntegrityAnchoringOptions>()).Evidence;
        var filter = new BsonDocument("_id", anchor["Timestamps"][0]["TokenHash"]);
        var document = await evidence.Find(filter).SingleAsync();
        document[AnchorEvidenceStore.DataField] = new BsonBinaryData(document[AnchorEvidenceStore.DataField].AsByteArray, BsonBinarySubType.OldBinary);
        await evidence.DeleteOneAsync(filter);
        await evidence.Database.GetCollection<RawBsonDocument>(evidence.CollectionNamespace.CollectionName)
                      .InsertOneAsync(new RawBsonDocument(ToBsonKeepingOldBinary(document)));

        await DumpAsync(context);
        var (code, report) = await RunVerifierAsync(CheckArguments(aggregateId));
        var library = await VerifyTimestampWithLibraryAsync(context);

        code.Should().Be(1);
        library.IsValid.Should().BeFalse();
        (report["anchors"]?[0]?["timestamps"]?[0]?["failure"]?.GetValue<String>()).Should().Contain("malformed");
    }

    [Test]
    [TestCase("string")]
    [TestCase("fraction")]
    public async Task Check_EvidenceLengthOfAnotherType_IsInvalidLikeTheLibrary(String type)
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        var anchor = await GetStore(context).Anchors.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        var evidence = new AnchorEvidenceStore(context.MongoHelper, context.ServiceProvider.GetRequiredService<IntegrityAnchoringOptions>()).Evidence;
        var filter = new BsonDocument("_id", anchor["Timestamps"][0]["TokenHash"]);
        var length = (await evidence.Find(filter).SingleAsync())[AnchorEvidenceStore.LengthField].AsInt32;
        await evidence.UpdateOneAsync(filter,
                                      Builders<BsonDocument>.Update.Set(AnchorEvidenceStore.LengthField,
                                                                        type == "string"
                                                                            ? (BsonValue)length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                                                            : length + 0.5));

        await DumpAsync(context);
        var (code, report) = await RunVerifierAsync(CheckArguments(aggregateId));
        var library = await VerifyTimestampWithLibraryAsync(context);

        code.Should().Be(1);
        library.IsValid.Should().BeFalse();
        (report["anchors"]?[0]?["timestamps"]?[0]?["failure"]?.GetValue<String>()).Should().Contain("malformed");
    }

    [Test]
    public async Task Check_EvidenceNotMatchingItsHash_IsInvalidLikeTheLibrary()
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        var anchor = await GetStore(context).Anchors.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        var options = context.ServiceProvider.GetRequiredService<IntegrityAnchoringOptions>();
        await new AnchorEvidenceStore(context.MongoHelper, options).Evidence.UpdateOneAsync(new BsonDocument("_id", anchor["Timestamps"][0]["TokenHash"]),
                                                                                            Builders<BsonDocument>.Update.Set(AnchorEvidenceStore.DataField,
                                                                                                new BsonBinaryData(new Byte[]
                                                                                                {
                                                                                                    1,
                                                                                                    2,
                                                                                                    3
                                                                                                })));

        await DumpAsync(context);
        var (code, report) = await RunVerifierAsync(CheckArguments(aggregateId));
        var library = await VerifyTimestampWithLibraryAsync(context);

        code.Should().Be(1);
        library.IsValid.Should().BeFalse();
        (report["anchors"]?[0]?["timestamps"]?[0]?["failure"]?.GetValue<String>()).Should().Contain("does not match its hash");
    }

    [Test]
    public async Task Check_GarbageToken_ReportsMalformedToken()
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        var garbage = new Byte[]
        {
            0x30,
            0x03,
            0x02,
            0x01,
            0x01
        };
        var hash = await new AnchorEvidenceStore(context.MongoHelper, context.ServiceProvider.GetRequiredService<IntegrityAnchoringOptions>())
            .StoreAsync(garbage, CancellationToken.None);
        await GetStore(context).Anchors.UpdateOneAsync(FilterDefinition<BsonDocument>.Empty,
                                                       Builders<BsonDocument>.Update.Set("Timestamps.0.TokenHash", new BsonBinaryData(hash)));

        await DumpAsync(context);
        var (code, report) = await RunVerifierAsync(CheckArguments(aggregateId));

        code.Should().Be(1);
        (report["anchors"]?[0]?["timestamps"]?[0]?["failure"]?.GetValue<String>()).Should().StartWith("malformed token");
    }

    [Test]
    [TestCase("0")]
    [TestCase("nan")]
    [TestCase("1e300")]
    public async Task Check_GraceHoursNotAPositiveDuration_IsAUsageError(String hours)
    {
        var (code, _, error) = await RunProcessAsync([.. CheckArguments(Guid.NewGuid()), "--grace-hours", hours]);

        code.Should().Be(2);
        error.Should().Contain("--grace-hours");
    }

    [Test]
    [TestCase(BsonBinarySubType.UserDefined)]
    [TestCase(BsonBinarySubType.OldBinary)]
    public async Task Check_IntegrityHashWithOtherBinarySubtype_ReportsMalformedIntegrityLikeTheLibrary(BsonBinarySubType subType)
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        var document = await context.FindEventAsync(aggregateId, 2);
        document[EventIntegrityChain.ElementName]["Hash"] = new BsonBinaryData(document[EventIntegrityChain.ElementName]["Hash"].AsByteArray, subType);
        await context.Events.DeleteOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 2));
        await context.MongoHelper.Database.GetCollection<RawBsonDocument>(context.Options.EventsCollectionName)
                     .InsertOneAsync(new RawBsonDocument(ToBsonKeepingOldBinary(document)));

        await DumpAsync(context);
        var (code, report) = await RunVerifierAsync(CheckArguments(aggregateId));
        var library = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        code.Should().Be(1);
        library.Should().Be(StreamVerificationResult.Broken(2, StreamVerificationFailure.MalformedIntegrity));
        (report["stream"]?["failure"]?.GetValue<String>()).Should().Be(nameof(StreamVerificationFailure.MalformedIntegrity));
        (report["stream"]?["firstBrokenVersion"]?.GetValue<Int64>()).Should().Be(2);
    }

    [Test]
    public async Task Check_LeafRewrittenWithItsStream_ReportsAnchoredLeafInvalidLikeTheLibrary()
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        await context.Events.DeleteManyAsync(EventDocumentFields<LedgerAggregate>.ForAggregate<BsonDocument>(aggregateId));
        await context.MongoHelper.Database.GetCollection<BsonDocument>(context.Options.ReadModelCollectionName)
                     .DeleteOneAsync(new BsonDocument("_id", new BsonBinaryData(aggregateId, GuidRepresentation.Standard)));
        await context.Store.AppendEventsAsync(Enumerable.Range(1, 3).Select(version => LedgerStoreContext.CreateEntry(aggregateId, version, memo: "rewritten")));
        var head = LedgerStoreContext.ReadIntegrity(await context.FindEventAsync(aggregateId, 3));
        await GetStore(context).Leaves.UpdateOneAsync(FilterDefinition<BsonDocument>.Empty, Builders<BsonDocument>.Update.Set("Hash", new BsonBinaryData(head.Hash)));

        await DumpAsync(context);
        var (code, report) = await RunVerifierAsync(CheckArguments(aggregateId));
        var library = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        code.Should().Be(1);
        library.Should().Be(StreamVerificationResult.Broken(3, StreamVerificationFailure.AnchoredLeafInvalid));
        (report["stream"]?["failure"]?.GetValue<String>()).Should().Be(nameof(StreamVerificationFailure.AnchoredLeafInvalid));
        (report["anchorChain"]?["failure"]?.GetValue<String>()).Should().Be(nameof(AnchorChainFailure.LeafSetMismatch));
    }

    [Test]
    public async Task Check_LeavesDeletedWithTheirStream_ReportsLeafSetMismatch()
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        await context.Events.DeleteManyAsync(EventDocumentFields<LedgerAggregate>.ForAggregate<BsonDocument>(aggregateId));
        await GetStore(context).Leaves.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);

        await DumpAsync(context);
        var (code, report) = await RunVerifierAsync(CheckArguments(aggregateId));

        code.Should().Be(1);
        (report["stream"]?["intact"]?.GetValue<Boolean>()).Should().BeTrue("the empty stream itself is consistent");
        (report["anchorChain"]?["failure"]?.GetValue<String>()).Should().Be(nameof(AnchorChainFailure.LeafSetMismatch));
    }

    [Test]
    [TestCase(new Byte[]
    {
        0,
        0,
        0,
        0
    })]
    [TestCase(new Byte[]
    {
        16,
        0,
        0,
        0,
        2,
        97,
        0
    })]
    public async Task Check_MalformedBsonFile_ReportsAnError(Byte[] content)
    {
        var events = Path.Combine(_directory, "events.bson");
        await File.WriteAllBytesAsync(events, content);

        var (code, report) = await RunVerifierAsync(["check", "--events", events, "--aggregate-type", nameof(LedgerAggregate), "--aggregate-id", Guid.NewGuid().ToString()]);

        code.Should().Be(2);
        (report["error"]?.GetValue<String>()).Should().Contain("malformed BSON file");
    }

    [Test]
    public async Task Check_StoredChainWithoutItsRoot_IsInvalidLikeTheLibrary()
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        await GetStore(context).Anchors.UpdateOneAsync(FilterDefinition<BsonDocument>.Empty,
                                                       Builders<BsonDocument>.Update.PopLast("Timestamps.0.CertificateChainHashes"));

        await DumpAsync(context);
        var (code, report) = await RunVerifierAsync(CheckArguments(aggregateId));
        var library = await VerifyTimestampWithLibraryAsync(context);

        code.Should().Be(1);
        library.IsValid.Should().BeFalse();
        (report["anchors"]?[0]?["timestamps"]?[0]?["failure"]?.GetValue<String>()).Should().Contain("trusted root");
    }

    [Test]
    [TestCase(false)]
    [TestCase(true)]
    public async Task Check_StoredStatusWithoutItsConfirmingCrls_IsInvalidLikeTheLibrary(Boolean substitutedByStampingTimeCrl)
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        var update = Builders<BsonDocument>.Update.Set("Timestamps.0.RevocationStatus", nameof(TimestampRevocationStatus.Revoked));
        if (substitutedByStampingTimeCrl)
        {
            var anchor = await GetStore(context).Anchors.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
            update = update.Set("Timestamps.0.ConfirmingRevocationListHashes", anchor["Timestamps"][0]["CertificateRevocationListHashes"]);
        }

        await GetStore(context).Anchors.UpdateOneAsync(FilterDefinition<BsonDocument>.Empty, update);

        await DumpAsync(context);
        var (code, report) = await RunVerifierAsync(CheckArguments(aggregateId));
        var library = await VerifyTimestampWithLibraryAsync(context);

        code.Should().Be(1);
        library.Failure.Should().Contain("inconsistent");
        (report["anchors"]?[0]?["timestamps"]?[0]?["failure"]?.GetValue<String>()).Should().Contain("inconsistent");
    }

    [Test]
    public async Task Check_StreamAndAnchorProducedByTheLibrary_PassesAndFailsAfterModification()
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();

        await DumpAsync(context);
        var (intactCode, intact) = await RunVerifierAsync(CheckArguments(aggregateId));
        await context.Events.UpdateOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 2),
                                            Builders<BsonDocument>.Update.Set("Amount", new Decimal128(1000m)));
        await DumpAsync(context);
        var (modifiedCode, modified) = await RunVerifierAsync(CheckArguments(aggregateId));

        intactCode.Should().Be(0, intact.ToJsonString());
        (intact["stream"]?["intact"]?.GetValue<Boolean>()).Should().BeTrue();
        (intact["anchors"] as JsonArray).Should().ContainSingle();
        (intact["anchors"]?[0]?["verified"]?.GetValue<Boolean>()).Should().BeTrue();
        (intact["anchors"]?[0]?["inclusionValid"]?.GetValue<Boolean>()).Should().BeTrue();
        modifiedCode.Should().Be(1);
        (modified["stream"]?["failure"]?.GetValue<String>()).Should().Be(nameof(StreamVerificationFailure.HashMismatch));
        (modified["stream"]?["firstBrokenVersion"]?.GetValue<Int64>()).Should().Be(2);
    }

    [Test]
    [TestCase("highTagNumber", true)]
    [TestCase("emptyCountersignature", false)]
    [TestCase("fiveByteTagNumber", true)]
    public async Task Check_TokenWithAddedUnsignedAttribute_IsJudgedLikeTheLibrary(String attribute, Boolean valid)
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        var evidence = new AnchorEvidenceStore(context.MongoHelper, context.ServiceProvider.GetRequiredService<IntegrityAnchoringOptions>());
        var anchor = await GetStore(context).Anchors.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        var token = await evidence.ReadAsync(anchor["Timestamps"][0]["TokenHash"].AsByteArray, CancellationToken.None);
        // Unsigned attributes are not covered by the signature, so anyone storing the token can add them: one holding a
        // value with tag number 31, which DER encodes in the high-tag-number form, or a countersignature without signers.
        Byte[] added = attribute switch
        {
            "highTagNumber" => [0x30, 0x0C, 0x06, 0x03, 0x2A, 0x03, 0x04, 0x31, 0x05, 0x30, 0x03, 0x9F, 0x1F, 0x00],
            // Tag number 2^28 needs five bytes in the high-tag-number form.
            "fiveByteTagNumber" => [0x30, 0x10, 0x06, 0x03, 0x2A, 0x03, 0x04, 0x31, 0x09, 0x30, 0x07, 0x9F, 0x81, 0x80, 0x80, 0x80, 0x00, 0x00],
            _ => [0x30, 0x0D, 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x09, 0x06, 0x31, 0x00]
        };
        var hash = await evidence.StoreAsync(WithUnsignedAttribute(token, added), CancellationToken.None);
        await GetStore(context).Anchors.UpdateOneAsync(FilterDefinition<BsonDocument>.Empty,
                                                       Builders<BsonDocument>.Update.Set("Timestamps.0.TokenHash", new BsonBinaryData(hash)));

        await DumpAsync(context);
        var (_, report) = await RunVerifierAsync(CheckArguments(aggregateId));
        var library = await VerifyTimestampWithLibraryAsync(context);

        library.IsValid.Should().Be(valid, library.Failure);
        (report["anchors"]?[0]?["timestamps"]?[0]?["valid"]?.GetValue<Boolean>()).Should().Be(valid, report.ToJsonString());
    }

    [Test]
    [TestCase("test-tsa", null, null)]
    [TestCase("other-tsa", null, "no trust settings")]
    [TestCase("test-tsa", "1.2.3.4", "policy")]
    [TestCase("test-tsa", TestTimestampAuthority.DefaultPolicyId, null)]
    public async Task Check_TrustPerAuthority_AppliesRootsAndPolicies(String rootAuthority, String? policy, String? failure)
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        var arguments = CheckArguments(aggregateId).ToList();
        var root = arguments.IndexOf("--trusted-root") + 1;
        arguments[root] = $"{rootAuthority}={arguments[root]}";
        if (policy is not null)
        {
            arguments.AddRange(["--policy", $"test-tsa={policy}"]);
        }

        await DumpAsync(context);
        var (code, report) = await RunVerifierAsync(arguments);

        code.Should().Be(failure is null ? 0 : 1, report.ToJsonString());
        if (failure is not null)
        {
            (report["anchors"]?[0]?["timestamps"]?[0]?["failure"]?.GetValue<String>()).Should().Contain(failure);
        }
    }

    [Test]
    public async Task Check_TrustedHeadOfAnchorAwaitingTimestamps_ReportsMalformedAnchorLikeTheLibrary()
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        var digest = (await GetStore(context).Anchors.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync())[AnchorStore.AnchorDigestField].AsByteArray;
        await GetStore(context).Anchors.UpdateOneAsync(FilterDefinition<BsonDocument>.Empty,
                                                       Builders<BsonDocument>.Update.Set(AnchorStore.StatusField, nameof(AnchorStatus.AwaitingTimestamp)));

        await DumpAsync(context);
        var (code, report) = await RunVerifierAsync([.. CheckArguments(aggregateId), "--trusted-head", $"1:{TamperEvidenceVectorTests.Hex(digest)}"]);
        var library = await context.ServiceProvider.GetRequiredService<AnchorVerifier>().VerifyAnchorChainAsync(new TrustedAnchorHead(1, digest));

        code.Should().Be(1);
        library.Should().Be(new AnchorChainVerificationResult(1, AnchorChainFailure.MalformedAnchor, 0));
        (report["anchorChain"]?["failure"]?.GetValue<String>()).Should().Be(nameof(AnchorChainFailure.MalformedAnchor));
        (report["anchorChain"]?["firstBrokenSequence"]?.GetValue<Int64>()).Should().Be(1);
    }

    [Test]
    public async Task Check_TrustedHead_IsRequiredUnchanged()
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        var digest = TamperEvidenceVectorTests.Hex((await GetStore(context).Anchors.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync())[AnchorStore.AnchorDigestField]
                                                   .AsByteArray);

        await DumpAsync(context);
        var (headCode, head) = await RunVerifierAsync([.. CheckArguments(aggregateId), "--trusted-head", $"1:{digest}"]);
        var (otherCode, other) = await RunVerifierAsync([.. CheckArguments(aggregateId), "--trusted-head", $"1:{new String('0', 64)}"]);
        var (laterCode, later) = await RunVerifierAsync([.. CheckArguments(aggregateId), "--trusted-head", $"2:{digest}"]);

        headCode.Should().Be(0, head.ToJsonString());
        otherCode.Should().Be(1);
        (other["anchorChain"]?["failure"]?.GetValue<String>()).Should().Be(nameof(AnchorChainFailure.TrustedHeadMismatch));
        laterCode.Should().Be(1);
        (later["anchorChain"]?["failure"]?.GetValue<String>()).Should().Be(nameof(AnchorChainFailure.TrustedHeadMissing));
    }

    [Test]
    [TestCase("integrityString", nameof(StreamVerificationFailure.MalformedIntegrity), null)]
    [TestCase("integrityRepeatedName", nameof(StreamVerificationFailure.MalformedIntegrity), null)]
    [TestCase("anchorString", nameof(StreamVerificationFailure.AnchoredLeafInvalid), nameof(AnchorChainFailure.MalformedAnchor))]
    [TestCase("eventDbPointer", nameof(StreamVerificationFailure.MalformedIntegrity), null)]
    [TestCase("pathDbPointer", nameof(StreamVerificationFailure.MalformedIntegrity), null)]
    [TestCase("anchorDbPointer", nameof(StreamVerificationFailure.AnchoredLeafInvalid), nameof(AnchorChainFailure.MalformedAnchor))]
    public async Task Check_UnknownElementThatCannotBeRead_MakesTheDocumentMalformedLikeTheLibrary(String place, String streamFailure, String? chainFailure)
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        // Unknown elements are ignored, but the documents read as a whole must be readable at any depth.
        BsonValue unknown = place == "integrityRepeatedName"
            ? new BsonDocument
            {
                AllowDuplicateNames = true
            }.Add("a", 1).Add("a", 2)
            : "x";
        if (place is "anchorString" or "anchorDbPointer")
        {
            var anchors = context.MongoHelper.Database.GetCollection<RawBsonDocument>(GetStore(context).Anchors.CollectionNamespace.CollectionName);
            var anchor = await GetStore(context).Anchors.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
            await anchors.DeleteOneAsync(new BsonDocument("_id", anchor["_id"]));
            await anchors.InsertOneAsync(new RawBsonDocument(place == "anchorDbPointer"
                                                                 ? WithDbPointer(anchor.ToBson())
                                                                 : WithUndecodableNote(anchor.Add("Note", unknown).ToBson())));
        }
        else if (place is "eventDbPointer" or "pathDbPointer")
        {
            // A DBPointer cannot even be skipped over, so it fails the walk over the names or over the embedded
            // document a personal-data path descends into before any hash is compared.
            var bytes = await context.ReadRawEventAsync(aggregateId, 2);
            Byte[] counterparty = [0x03, .. "Counterparty"u8, 0x00];
            await ReplaceRawEventAsync(context,
                                       aggregateId,
                                       2,
                                       place == "eventDbPointer" ? WithDbPointer(bytes) : WithDbPointer(bytes, bytes.AsSpan().IndexOf(counterparty) + counterparty.Length));
        }
        else
        {
            var document = await context.FindEventAsync(aggregateId, 2);
            document[EventIntegrityChain.ElementName].AsBsonDocument.Add("Note", unknown);
            var bytes = document.ToBson();
            await ReplaceRawEventAsync(context, aggregateId, 2, place == "integrityString" ? WithUndecodableNote(bytes) : bytes);
        }

        await DumpAsync(context);
        var (_, report) = await RunVerifierAsync(CheckArguments(aggregateId));
        var stream = await context.CreateVerifier().VerifyStreamAsync(aggregateId);
        var chain = await context.ServiceProvider.GetRequiredService<AnchorVerifier>().VerifyAnchorChainAsync();

        (stream.Failure?.ToString(), chain.Failure?.ToString()).Should().Be((streamFailure, chainFailure));
        await AssertReportMatchesLibraryAsync(context, aggregateId, report);

        static Byte[] WithUndecodableNote(Byte[] bytes)
        {
            Byte[] note = [0x02, .. "Note"u8, 0x00, 0x02, 0x00, 0x00, 0x00, (Byte)'x', 0x00];
            bytes[bytes.AsSpan().IndexOf(note) + note.Length - 2] = 0xFF;
            return bytes;
        }
    }

    [Test]
    public async Task Check_UnreadableConfirmingCrlWithoutSources_IsUnconfirmedLikeTheLibrary()
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync(new TestTimestampAuthority([]));
        var hash = await new AnchorEvidenceStore(context.MongoHelper, context.ServiceProvider.GetRequiredService<IntegrityAnchoringOptions>())
            .StoreAsync("not a CRL"u8.ToArray(), CancellationToken.None);
        await GetStore(context).Anchors.UpdateOneAsync(FilterDefinition<BsonDocument>.Empty,
                                                       Builders<BsonDocument>.Update.Set("Timestamps.0.ConfirmingRevocationListHashes",
                                                                                         new BsonArray
                                                                                         {
                                                                                             new BsonBinaryData(hash)
                                                                                         }));

        await DumpAsync(context);
        var (code, report) = await RunVerifierAsync(CheckArguments(aggregateId));
        var library = await VerifyTimestampWithLibraryAsync(context);

        code.Should().Be(0, report.ToJsonString());
        library.RevocationStatus.Should().Be(TimestampRevocationStatus.Unconfirmed);
        (report["anchors"]?[0]?["timestamps"]?[0]?["revocationStatus"]?.GetValue<String>()).Should().Be(nameof(TimestampRevocationStatus.Unconfirmed));
    }

    [Test]
    public async Task Check_UnreadableStreamLeaf_ReportsAnchoredLeafInvalidLikeTheLibrary()
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        await GetStore(context).Leaves.UpdateOneAsync(FilterDefinition<BsonDocument>.Empty, Builders<BsonDocument>.Update.Set("Hash", "not binary data"));

        await DumpAsync(context);
        var (code, report) = await RunVerifierAsync(CheckArguments(aggregateId));
        var library = await context.CreateVerifier().VerifyStreamAsync(aggregateId);

        code.Should().Be(1);
        library.Should().Be(StreamVerificationResult.Broken(3, StreamVerificationFailure.AnchoredLeafInvalid));
        (report["stream"]?["failure"]?.GetValue<String>()).Should().Be(nameof(StreamVerificationFailure.AnchoredLeafInvalid));
        (report["stream"]?["firstBrokenVersion"]?.GetValue<Int64>()).Should().Be(3);
        (report["anchorChain"]?["failure"]?.GetValue<String>()).Should().Be(nameof(AnchorChainFailure.LeafSetMismatch));
    }

    [Test]
    public async Task Check_VectorStreamsTamperedFurther_ReportLikeTheLibrary()
    {
        var context = await LedgerStoreContext.CreateAsync(_container);
        _contexts.Add(context);
        await TamperEvidenceVectorTests.LoadAsync(context, context.Options.EventsCollectionName, "streams.bson");
        foreach (var (aggregateId, version) in new[]
                 {
                     (TamperEvidenceVectorGenerator.RetroactiveStream, 4),
                     (TamperEvidenceVectorGenerator.PersonalDataStream, 3)
                 })
        {
            await context.Events.UpdateOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, version),
                                                Builders<BsonDocument>.Update.Set("Amount", new Decimal128(1000m)));
        }

        await DumpCollectionAsync(context, context.Options.EventsCollectionName, "events.bson");
        var verifier = context.CreateVerifier();
        foreach (var stream in TamperEvidenceVectorTests.ReadJson("streams.json")["streams"]?.AsArray() ?? [])
        {
            var aggregateId = Guid.Parse(stream?["aggregateId"]?.GetValue<String>() ?? String.Empty);
            var (_, report) = await RunVerifierAsync([
                "check", "--events", Path.Combine(_directory, "events.bson"), "--aggregate-type", nameof(LedgerAggregate),
                "--aggregate-id", aggregateId.ToString()
            ]);
            var library = TamperEvidenceVectorTests.ToJson(await verifier.VerifyStreamAsync(aggregateId));

            JsonNode.DeepEquals(report["stream"], library).Should().BeTrue($"{stream?["name"]}: {report["stream"]?.ToJsonString()} vs {library.ToJsonString()}");
        }
    }

    [SetUp]
    public void CreateDirectory()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"reference-verifier-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [OneTimeSetUp]
    public async Task GetMongoDbContainer()
    {
        RequireTool("python3", "--version");
        RequireTool("openssl", "version");
        _container = await MongoDbTestContainer.StartContainerAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        foreach (var context in _contexts)
        {
            await context.DisposeAsync();
        }

        foreach (var testAuthority in _testAuthorities)
        {
            testAuthority.Dispose();
        }

        _contexts.Clear();
        _testAuthorities.Clear();
        Directory.Delete(_directory, true);
    }

    [Test]
    public async Task Vectors_CommittedVectors_PassTheReferenceVerifier()
    {
        var (code, report) = await RunVerifierAsync(["vectors", TamperEvidenceVectorTests.VectorDirectory]);

        code.Should().Be(0, report.ToJsonString());
        (report["failures"] as JsonArray).Should().BeEmpty();
    }

    /// <summary>
    /// Requires the reference verifier's report to state the library's stream and anchor-chain verdicts.
    /// </summary>
    private static async Task AssertReportMatchesLibraryAsync(LedgerStoreContext context, Guid aggregateId, JsonNode report)
    {
        var stream = await context.CreateVerifier().VerifyStreamAsync(aggregateId);
        var chain = await context.ServiceProvider.GetRequiredService<AnchorVerifier>().VerifyAnchorChainAsync();
        (report["stream"]?["firstBrokenVersion"]?.GetValue<Int64?>(), report["stream"]?["failure"]?.GetValue<String?>())
            .Should().Be((stream.FirstBrokenVersion, stream.Failure?.ToString()), report.ToJsonString());
        (report["anchorChain"]?["firstBrokenSequence"]?.GetValue<Int64?>(), report["anchorChain"]?["failure"]?.GetValue<String?>())
            .Should().Be((chain.FirstBrokenSequence, chain.Failure?.ToString()), report.ToJsonString());
    }

    /// <summary>
    /// Builds a confirming CRL of the test authority's CA, issued after the grace period, for a named case.
    /// </summary>
    private static Byte[] BuildConfirmingCrl(TestTimestampAuthority authority, String crlCase, DateTimeOffset tokenTime)
    {
        var thisUpdate = tokenTime.AddHours(25);
        return crlCase switch
        {
            "revokedBeforeTokenTime" => Entry(tokenTime.AddHours(-1)),
            "revokedAfterTokenTime" => Entry(tokenTime.AddHours(1)),
            "revokedIssuedWithinGracePeriod" => authority.BuildCrlWithEntries(tokenTime.AddHours(1),
                                                                              [(authority.SigningCertificate.SerialNumberBytes.ToArray(), tokenTime.AddHours(-1), [])]),
            "invalidBeforeTokenTime" => Entry(tokenTime.AddHours(1), (InvalidityDateOid, EncodeGeneralizedTime(tokenTime.AddHours(-1)))),
            "invalidityDateRepeated" => Entry(tokenTime.AddHours(1),
                                              (InvalidityDateOid, EncodeGeneralizedTime(tokenTime.AddHours(-1))),
                                              (InvalidityDateOid, EncodeGeneralizedTime(tokenTime.AddHours(2)))),
            "removedFromCrl" => Entry(tokenTime.AddHours(-1), (ReasonCodeOid, [0x0A, 0x01, 0x08])),
            "otherCertificateIssuer" => Entry(tokenTime.AddHours(-1), (CertificateIssuerOid, EncodeCertificateIssuer(TestTimestampAuthority.OtherIssuerName))),
            "certificateIssuerWithoutDirectoryName" => Entry(tokenTime.AddHours(-1), (CertificateIssuerOid, EncodeCertificateIssuer(null))),
            "unknownEntryExtensionWithEmptyValue" => Entry(tokenTime.AddHours(1), ("1.2.3.4", [])),
            "issuingPointOfOtherLocation" => authority.BuildCrlWithExtensions(
                thisUpdate,
                (IssuingDistributionPointOid, true, TestTimestampAuthority.EncodeIssuingDistributionPoint(new Uri("http://tsa.test/other.crl")))),
            "issuingPointOfEquivalentLocation" => authority.BuildCrlWithExtensions(
                thisUpdate,
                (IssuingDistributionPointOid, true, TestTimestampAuthority.EncodeIssuingDistributionPoint(new Uri("HTTP://TSA.test:80/mirror/./ca.crl")))),
            "onlyUserCertificates" => authority.BuildCrlWithExtensions(thisUpdate, (IssuingDistributionPointOid, true, [0x30, 0x03, 0x81, 0x01, 0xFF])),
            "unknownCriticalExtension" => authority.BuildCrlWithExtensions(thisUpdate, ("1.2.3.4", true, [0x05, 0x00])),
            "nonMinimalSerialNumber" => authority.BuildCrlWithEncodedSerialNumber(thisUpdate, [0x02, 0x02, 0x00, 0x01]),
            // A GeneralizedTime whose fraction ends in a zero is not DER.
            "nonDerNextUpdate" => authority.BuildCrlWithEncodedNextUpdate(thisUpdate, [0x18, 0x11, .. "20991231235959.0Z"u8]),
            _ => throw new ArgumentOutOfRangeException(nameof(crlCase), crlCase, null)
        };

        Byte[] Entry(DateTimeOffset revokedAt, params (String Oid, Byte[] Value)[] extensions)
            => authority.BuildCrlWithEntries(thisUpdate, [(authority.SigningCertificate.SerialNumberBytes.ToArray(), revokedAt, extensions)]);
    }

    /// <summary>
    /// Encodes the GeneralNames of a certificateIssuer entry extension: a directory name, or a DNS name without one.
    /// </summary>
    private static Byte[] EncodeCertificateIssuer(String? directoryName)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            if (directoryName is null)
            {
                writer.WriteCharacterString(UniversalTagNumber.IA5String, "ca.test", new Asn1Tag(TagClass.ContextSpecific, 2));
            }
            else
            {
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4, true)))
                {
                    writer.WriteEncodedValue(new X500DistinguishedName(directoryName).RawData);
                }
            }
        }

        return writer.Encode();
    }

    private static Byte[] EncodeGeneralizedTime(DateTimeOffset time)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.WriteGeneralizedTime(time, true);
        return writer.Encode();
    }

    private static AnchorStore GetStore(LedgerStoreContext context) => context.ServiceProvider.GetRequiredService<AnchorVerifier>().Store;

    /// <summary>
    /// Replaces a stored event by the given raw bytes.
    /// </summary>
    private static async Task ReplaceRawEventAsync(LedgerStoreContext context, Guid aggregateId, Int64 version, Byte[] bytes)
    {
        await context.Events.DeleteOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, version));
        await context.MongoHelper.Database.GetCollection<RawBsonDocument>(context.Options.EventsCollectionName).InsertOneAsync(new RawBsonDocument(bytes));
    }

    private static void RequireTool(String tool, String argument)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(tool, argument)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process?.WaitForExit();
            if (process is { ExitCode: 0 })
            {
                return;
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The tool is not installed.
        }

        var message = $"The reference verifier needs '{tool}' on the PATH.";
        if (String.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Fail(message);
        }

        Assert.Ignore(message);
    }

    private static async Task<(Int32 ExitCode, String Output, String Error)> RunProcessAsync(IEnumerable<String> arguments)
    {
        var startInfo = new ProcessStartInfo("python3")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-I");
        startInfo.ArgumentList.Add(Path.Combine(TamperEvidenceVectorTests.RepositoryRoot, "tools", "tamper-evidence-verifier", "verify.py"));
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("python3 could not be started.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }

    private static async Task<(Int32 ExitCode, JsonNode Report)> RunVerifierAsync(IEnumerable<String> arguments)
    {
        var (exitCode, output, error) = await RunProcessAsync(arguments);
        JsonNode? report;
        try
        {
            report = JsonNode.Parse(output);
        }
        catch (JsonException)
        {
            report = null;
        }

        return report is null
            ? throw new InvalidDataException($"The reference verifier printed no JSON report (exit code {exitCode}): {error}")
            : (exitCode, report);
    }

    /// <summary>
    /// Serializes a document as stored, keeping binary data of the old subtype <c>0x02</c>, which the driver otherwise
    /// writes as generic binary data.
    /// </summary>
    private static Byte[] ToBsonKeepingOldBinary(BsonDocument document)
    {
        using var stream = new MemoryStream();
        using (var writer = new BsonBinaryWriter(stream, new BsonBinaryWriterSettings
               {
                   FixOldBinarySubTypeOnOutput = false
               }))
        {
            BsonDocumentSerializer.Instance.Serialize(BsonSerializationContext.CreateRoot(writer), document);
        }

        return stream.ToArray();
    }

    private static async Task<TimestampVerificationResult> VerifyTimestampWithLibraryAsync(LedgerStoreContext context)
    {
        var verifier = context.ServiceProvider.GetRequiredService<AnchorVerifier>();
        var anchor = AnchorStore.ReadAnchor(await verifier.Store.Anchors.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync());
        return (await verifier.VerifyAnchorAsync(anchor)).Timestamps[0];
    }

    /// <summary>
    /// Appends a DBPointer element, a type MongoDB stores but the driver cannot read, to a document or to the embedded
    /// document that starts at a position of it, correcting the length prefixes.
    /// </summary>
    private static Byte[] WithDbPointer(Byte[] document, Int32 embeddedStart = 0)
    {
        Byte[] element = [0x0C, (Byte)'p', 0x00, 0x02, 0x00, 0x00, 0x00, (Byte)'x', 0x00, .. new Byte[12]];
        var end = embeddedStart + BitConverter.ToInt32(document, embeddedStart) - 1;
        Byte[] result = [.. document.AsSpan(0, end), .. element, .. document.AsSpan(end)];
        foreach (var start in embeddedStart == 0
                     ? new[] { 0 }
                     : new[]
                     {
                         0,
                         embeddedStart
                     })
        {
            BitConverter.TryWriteBytes(result.AsSpan(start, 4), BitConverter.ToInt32(result, start) + element.Length);
        }

        return result;
    }

    /// <summary>
    /// Adds an encoded attribute to the unsigned attributes of a token's signer, keeping everything else as stored.
    /// </summary>
    private static Byte[] WithUnsignedAttribute(Byte[] token, Byte[] attribute)
    {
        var contentInfo = new AsnReader(token, AsnEncodingRules.DER).ReadSequence();
        var contentType = contentInfo.ReadEncodedValue();
        var signedData = contentInfo.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)).ReadSequence();
        var signedDataParts = new List<ReadOnlyMemory<Byte>>();
        while (signedData.HasData)
        {
            signedDataParts.Add(signedData.ReadEncodedValue());
        }

        var signerInfo = new AsnReader(signedDataParts[^1], AsnEncodingRules.DER).ReadSetOf().ReadSequence();
        var signerParts = new List<ReadOnlyMemory<Byte>>();
        while (signerInfo.HasData)
        {
            signerParts.Add(signerInfo.ReadEncodedValue());
        }

        var unsignedTag = new Asn1Tag(TagClass.ContextSpecific, 1, true);
        var attributes = new List<ReadOnlyMemory<Byte>>();
        if (Asn1Tag.Decode(signerParts[^1].Span, out _).HasSameClassAndValue(unsignedTag))
        {
            var existing = new AsnReader(signerParts[^1], AsnEncodingRules.DER).ReadSetOf(unsignedTag);
            while (existing.HasData)
            {
                attributes.Add(existing.ReadEncodedValue());
            }

            signerParts.RemoveAt(signerParts.Count - 1);
        }

        attributes.Add(attribute);
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteEncodedValue(contentType.Span);
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
            using (writer.PushSequence())
            {
                foreach (var part in signedDataParts.Take(signedDataParts.Count - 1))
                {
                    writer.WriteEncodedValue(part.Span);
                }

                using (writer.PushSetOf())
                using (writer.PushSequence())
                {
                    foreach (var part in signerParts)
                    {
                        writer.WriteEncodedValue(part.Span);
                    }

                    using (writer.PushSetOf(unsignedTag))
                    {
                        foreach (var encoded in attributes)
                        {
                            writer.WriteEncodedValue(encoded.Span);
                        }
                    }
                }
            }
        }

        return writer.Encode();
    }

    private String[] CheckArguments(Guid aggregateId)
        =>
        [
            "check",
            "--events", Path.Combine(_directory, "events.bson"),
            "--aggregate-type", nameof(LedgerAggregate),
            "--aggregate-id", aggregateId.ToString(),
            "--anchors", Path.Combine(_directory, "anchors.bson"),
            "--leaves", Path.Combine(_directory, "leaves.bson"),
            "--evidence", Path.Combine(_directory, "evidence.bson"),
            "--evidence-chunks", Path.Combine(_directory, "evidence-chunks.bson"),
            "--trusted-root", Path.Combine(_directory, "root.pem")
        ];

    /// <summary>
    /// Creates a protected store that anchors with a test authority and trusts its root for verification, writes the
    /// root to <c>root.pem</c>, and anchors a stream of three events with personal data.
    /// </summary>
    private async Task<(LedgerStoreContext Context, Guid AggregateId)> CreateAnchoredStreamAsync(TestTimestampAuthority? authority = null)
    {
        var testAuthority = authority ?? new TestTimestampAuthority();
        _testAuthorities.Add(testAuthority);
        var context = await LedgerStoreContext.CreateAsync(
            _container,
            personalData: true,
            configureMongo: mongo => mongo.WithIntegrityAnchoring(anchoring => anchoring.WithTimestampAuthority(_ => testAuthority.CreateAuthority()))
                                          .WithIntegrityVerification(verification => verification.WithTimestampAuthorityTrust(
                                                                         "test-tsa",
                                                                         trust => trust.TrustedRoots.Add(testAuthority.Root))));
        _contexts.Add(context);
        var aggregateId = await context.AppendStreamAsync(3);
        await context.RunUntilAnchoredAsync(aggregateId, 3);
        await File.WriteAllTextAsync(Path.Combine(_directory, "root.pem"), testAuthority.Root.ExportCertificatePem());
        return (context, aggregateId);
    }

    /// <summary>
    /// Writes the collections as concatenated raw BSON documents, the format <c>mongodump</c> writes.
    /// </summary>
    private async Task DumpAsync(LedgerStoreContext context)
    {
        var options = context.ServiceProvider.GetRequiredService<IntegrityAnchoringOptions>();
        foreach (var (collection, file) in new[]
                 {
                     (context.Options.EventsCollectionName, "events.bson"),
                     (options.AnchorsCollectionName, "anchors.bson"),
                     (options.AnchorLeavesCollectionName, "leaves.bson"),
                     (options.AnchorEvidenceCollectionName, "evidence.bson"),
                     (options.AnchorEvidenceChunksCollectionName, "evidence-chunks.bson")
                 })
        {
            await DumpCollectionAsync(context, collection, file);
        }
    }

    private async Task DumpCollectionAsync(LedgerStoreContext context, String collection, String file)
    {
        await using var output = File.Create(Path.Combine(_directory, file));
        foreach (var document in await context.MongoHelper.Database.GetCollection<RawBsonDocument>(collection).Find(FilterDefinition<RawBsonDocument>.Empty).ToListAsync())
        {
            using (document)
            {
                await output.WriteAsync(document.ToBson());
            }
        }
    }
}

public sealed class RenamedElementsAggregate : Aggregate;

public sealed class RenamedElementsEvent : Event<RenamedElementsAggregate>
{
    public override void Execute(RenamedElementsAggregate aggregate) { }
}
