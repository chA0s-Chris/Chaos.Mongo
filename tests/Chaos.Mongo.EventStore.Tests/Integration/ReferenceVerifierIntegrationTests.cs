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
    public async Task Check_StreamAndAnchorProducedByTheLibrary_PassesAndFailsAfterModification()
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
        var report = JsonNode.Parse(await output) ?? throw new InvalidDataException($"The reference verifier printed no report: {await error}");
        return (process.ExitCode, report);
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
