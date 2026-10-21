// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.EventStore.Integrity;
using Chaos.Mongo.EventStore.Tests.Integrity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using System.Diagnostics;
using System.Security.Cryptography;
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
    private readonly List<LedgerStoreContext> _contexts = [];
    private readonly List<TestTimestampAuthority> _testAuthorities = [];
    private MongoDbContainer _container;
    private String _directory;

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
    public async Task Check_IntegrityHashWithOtherBinarySubtype_ReportsMalformedIntegrityLikeTheLibrary()
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        var stored = LedgerStoreContext.ReadIntegrity(await context.FindEventAsync(aggregateId, 2));
        await context.Events.UpdateOneAsync(EventDocumentFields<LedgerAggregate>.ForVersion<BsonDocument>(aggregateId, 2),
                                            Builders<BsonDocument>.Update.Set($"{EventIntegrityChain.ElementName}.Hash",
                                                                              new BsonBinaryData(stored.Hash, BsonBinarySubType.UserDefined)));

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
    public async Task Check_StoredStatusWithoutConfirmingCrls_IsInvalid()
    {
        var (context, aggregateId) = await CreateAnchoredStreamAsync();
        await GetStore(context).Anchors.UpdateOneAsync(FilterDefinition<BsonDocument>.Empty,
                                                       Builders<BsonDocument>.Update.Set("Timestamps.0.RevocationStatus", nameof(TimestampRevocationStatus.Revoked)));

        await DumpAsync(context);
        var (code, report) = await RunVerifierAsync(CheckArguments(aggregateId));

        code.Should().Be(1);
        (report["anchors"]?[0]?["timestamps"]?[0]?["failure"]?.GetValue<String>()).Should().Contain("confirming CRLs are missing");
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
    public async Task Vectors_PassTheReferenceVerifier()
    {
        var (code, report) = await RunVerifierAsync(["vectors", TamperEvidenceVectorTests.VectorDirectory]);

        code.Should().Be(0, report.ToJsonString());
        (report["failures"] as JsonArray).Should().BeEmpty();
    }

    private static AnchorStore GetStore(LedgerStoreContext context) => context.ServiceProvider.GetRequiredService<AnchorVerifier>().Store;

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

    private static async Task RunUntilAnchoredAsync(LedgerStoreContext context, Guid aggregateId, Int64 version)
    {
        var run = context.ServiceProvider.GetRequiredService<AnchoringRun>();
        var store = context.ServiceProvider.GetRequiredService<AnchorVerifier>().Store;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while ((await store.FindCompletedStreamLeavesAsync(nameof(LedgerAggregate), aggregateId, version, CancellationToken.None)).Count == 0)
        {
            Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "The stream was not anchored within the timeout.");
            await run.RunAsync();
        }
    }

    private static async Task<(Int32 ExitCode, JsonNode Report)> RunVerifierAsync(IEnumerable<String> arguments)
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
        JsonNode? report;
        try
        {
            report = JsonNode.Parse(await output);
        }
        catch (JsonException)
        {
            report = null;
        }

        return report is null
            ? throw new InvalidDataException($"The reference verifier printed no JSON report (exit code {process.ExitCode}): {await error}")
            : (process.ExitCode, report);
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
    /// Creates a protected store that anchors with a test authority, writes the authority's root to <c>root.pem</c>,
    /// and anchors a stream of three events with personal data.
    /// </summary>
    private async Task<(LedgerStoreContext Context, Guid AggregateId)> CreateAnchoredStreamAsync()
    {
        var testAuthority = new TestTimestampAuthority();
        _testAuthorities.Add(testAuthority);
        var context = await LedgerStoreContext.CreateAsync(
            _container,
            personalData: true,
            configureMongo: mongo => mongo.WithIntegrityAnchoring(anchoring => anchoring.WithTimestampAuthority(_ => testAuthority.CreateAuthority())));
        _contexts.Add(context);
        var aggregateId = await context.AppendStreamAsync(3);
        await RunUntilAnchoredAsync(context, aggregateId, 3);
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
}
