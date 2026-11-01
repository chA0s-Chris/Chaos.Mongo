// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests;

using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using NUnit.Framework;
using System.Net;

/// <summary>
/// Covers branches of <see cref="MongoIndexManagerExtensions"/>'s <c>CreateOneOrUpdateAsync</c> that cannot be
/// reliably forced through a real server via Testcontainers: tolerating a concurrent caller resolving the same
/// index conflict (depends on exact server timing between this call's own list/drop/create steps), the
/// collation probe's post-cancellation cleanup (depends on exact timing of a caller-initiated cancellation),
/// and the server-version-dependent silent acceptance of a same-name request that only changes <c>Hidden</c>
/// (depends on which MongoDB version is actually running). All are covered with a hand-crafted fake
/// <see cref="IMongoIndexManager{TDocument}"/> that scripts the exact sequence of responses instead.
/// </summary>
public class MongoIndexManagerExtensionsTests
{
    [Test]
    public async Task CreateOneOrUpdateAsync_WhenCollationProbeCreationIsCanceledAfterServerSuccess_ShouldStillCleanUpProbeOnIndependentContextAndRethrowCancellation()
    {
        // Arrange - an explicit non-simple Collation always requires the server probe (RequiresCollationProbe),
        // regardless of existing indexes, so this does not need any pre-existing collation index to trigger it.
        var keys = (IndexKeysDefinition<BsonDocument>)new BsonDocument("Field", 1);
        var model = new CreateIndexModel<BsonDocument>(keys, new CreateIndexOptions
        {
            Name = "new_name",
            Collation = new Collation("en")
        });

        var conflict = CreateConflictException("new_name");

        var createOneResults = new Queue<Func<Task<String>>>(
        [
            () => throw conflict,
            // Simulates the server having already created the probe index before the client observed a
            // cancellation (e.g. a caller-initiated shutdown racing the createIndexes command's success).
            () => throw new OperationCanceledException()
        ]);
        var listResults = new Queue<Func<Task<IAsyncCursor<BsonDocument>>>>(
        [
            () => Task.FromResult<IAsyncCursor<BsonDocument>>(new SingleBatchAsyncCursor<BsonDocument>([]))
        ]);

        var indexManager = new FakeIndexManager(createOneResults, listResults);

        // Act
        var act = async () => await indexManager.CreateOneOrUpdateAsync(model);

        // Assert - the cancellation is observed, not swallowed, but the throwaway probe is still cleaned up on an
        // independent, never-already-canceled context rather than being silently left behind.
        await act.Should().ThrowAsync<OperationCanceledException>();
        indexManager.DropOneAsyncCalls.Should().HaveCount(1);
        indexManager.DropOneAsyncCalls[0].Name.Should().StartWith("__chaosMongoCollationProbe_");
        indexManager.DropOneAsyncCalls[0].CancellationToken.IsCancellationRequested.Should().BeFalse();
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenCollationProbeIsRequired_ShouldForwardCallerOptionsToProbe()
    {
        // Arrange
        var options = new CreateOneIndexOptions
        {
            MaxTime = TimeSpan.FromSeconds(5)
        };
        var conflict = CreateConflictException("new_name");

        var indexManager = new FakeIndexManager(
            new Queue<Func<Task<String>>>([
                () => throw conflict,
                () => Task.FromResult("__chaosMongoCollationProbe_ignored"),
                () => Task.FromResult("new_name")
            ]),
            new Queue<Func<Task<IAsyncCursor<BsonDocument>>>>([EmptyListing, EmptyListing]));

        // Act
        await indexManager.CreateOneOrUpdateAsync(CreateModelRequiringCollationProbe(), options);

        // Assert
        indexManager.CreateOneAsyncCalls.Should().ContainSingle(call => IsCollationProbe(call.Model))
                    .Which.Options.Should().BeSameAs(options);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenConcurrentCallerAlreadyDroppedOldIndexButNotYetRecreated_ShouldReturnWithoutThrowing()
    {
        // Arrange
        var keys = (IndexKeysDefinition<BsonDocument>)new BsonDocument("Field", 1);
        var model = new CreateIndexModel<BsonDocument>(keys, new CreateIndexOptions
        {
            Name = "new_name"
        });

        var conflict = CreateConflictException("new_name");

        var createOneResults = new Queue<Func<Task<String>>>(
        [
            () => throw conflict,
            () => Task.FromResult("new_name")
        ]);
        var listResults = new Queue<Func<Task<IAsyncCursor<BsonDocument>>>>(
        [
            () => Task.FromResult<IAsyncCursor<BsonDocument>>(new SingleBatchAsyncCursor<BsonDocument>([]))
        ]);

        var indexManager = new FakeIndexManager(createOneResults, listResults);

        // Act
        var result = await indexManager.CreateOneOrUpdateAsync(model);

        // Assert
        result.Should().Be("new_name");
        indexManager.DropOneAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenConcurrentCallerDroppedOldIndexAndConflictsAgainOnRetry_ShouldConfirmObservedIndexByRetryingCreation()
    {
        // Arrange
        var keys = (IndexKeysDefinition<BsonDocument>)new BsonDocument("Field", 1);
        var model = new CreateIndexModel<BsonDocument>(keys, new CreateIndexOptions
        {
            Name = "new_name"
        });

        var firstConflict = CreateConflictException("new_name");
        var retryConflict = CreateConflictException("new_name");

        var desiredIndexDocument = new BsonDocument
        {
            ["name"] = "new_name",
            ["key"] = new BsonDocument("Field", 1)
        };

        var createOneResults = new Queue<Func<Task<String>>>(
        [
            () => throw firstConflict,
            () => throw retryConflict,
            () => Task.FromResult("new_name")
        ]);
        var listResults = new Queue<Func<Task<IAsyncCursor<BsonDocument>>>>(
        [
            () => Task.FromResult<IAsyncCursor<BsonDocument>>(new SingleBatchAsyncCursor<BsonDocument>([])),
            () => Task.FromResult<IAsyncCursor<BsonDocument>>(new SingleBatchAsyncCursor<BsonDocument>([desiredIndexDocument]))
        ]);

        var indexManager = new FakeIndexManager(createOneResults, listResults);

        // Act
        var result = await indexManager.CreateOneOrUpdateAsync(model);

        // Assert
        result.Should().Be("new_name");
        indexManager.DropOneAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenConcurrentCallerRecreatesIndexAfterOwnDrop_ShouldConfirmObservedIndexByRetryingCreation()
    {
        // Arrange
        var keys = (IndexKeysDefinition<BsonDocument>)new BsonDocument("Field", 1);
        var model = new CreateIndexModel<BsonDocument>(keys, new CreateIndexOptions
        {
            Name = "new_name"
        });

        var conflict = CreateConflictException("new_name");
        var secondConflict = CreateConflictException("new_name");

        var oldIndexDocument = new BsonDocument
        {
            ["name"] = "old_name",
            ["key"] = new BsonDocument("Field", 1)
        };
        var desiredIndexDocument = new BsonDocument
        {
            ["name"] = "new_name",
            ["key"] = new BsonDocument("Field", 1)
        };

        var createOneResults = new Queue<Func<Task<String>>>(
        [
            () => throw conflict,
            () => throw secondConflict,
            () => Task.FromResult("new_name")
        ]);
        var listResults = new Queue<Func<Task<IAsyncCursor<BsonDocument>>>>(
        [
            () => Task.FromResult<IAsyncCursor<BsonDocument>>(new SingleBatchAsyncCursor<BsonDocument>([oldIndexDocument])),
            () => Task.FromResult<IAsyncCursor<BsonDocument>>(new SingleBatchAsyncCursor<BsonDocument>([desiredIndexDocument]))
        ]);

        var indexManager = new FakeIndexManager(createOneResults, listResults);

        // Act
        var result = await indexManager.CreateOneOrUpdateAsync(model);

        // Assert
        result.Should().Be("new_name");
        indexManager.DropOneAsyncCallCount.Should().Be(1);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenHiddenMismatchFallsBackToConflictResolution_ShouldForwardCallerOptionsToProbe()
    {
        // Arrange
        var options = new CreateOneIndexOptions
        {
            MaxTime = TimeSpan.FromSeconds(5)
        };
        var conflict = CreateConflictException("new_name");

        var indexManager = new FakeIndexManager(
            new Queue<Func<Task<String>>>([
                () => Task.FromResult("new_name"),
                () => throw conflict,
                () => Task.FromResult("__chaosMongoCollationProbe_ignored"),
                () => Task.FromResult("new_name")
            ]),
            new Queue<Func<Task<IAsyncCursor<BsonDocument>>>>([() => Listing(CreateIndexDocument("new_name")), EmptyListing, EmptyListing]));

        // Act
        await indexManager.CreateOneOrUpdateAsync(CreateModelRequiringCollationProbe(), options);

        // Assert
        indexManager.CreateOneAsyncCalls.Should().ContainSingle(call => IsCollationProbe(call.Model))
                    .Which.Options.Should().BeSameAs(options);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenHiddenNotRequested_ShouldNotPerformExtraListCheck()
    {
        // Arrange - a caller that never sets Hidden must not pay the extra listIndexes round-trip at all; an
        // empty listResults queue means the test fails with a Dequeue error if ResolveHiddenMismatchAsync calls
        // ListAsync when it should have returned immediately.
        var keys = (IndexKeysDefinition<BsonDocument>)new BsonDocument("Field", 1);
        var model = new CreateIndexModel<BsonDocument>(keys, new CreateIndexOptions
        {
            Name = "same_name"
        });

        var createOneResults = new Queue<Func<Task<String>>>([() => Task.FromResult("same_name")]);
        var listResults = new Queue<Func<Task<IAsyncCursor<BsonDocument>>>>();

        var indexManager = new FakeIndexManager(createOneResults, listResults);

        // Act
        var result = await indexManager.CreateOneOrUpdateAsync(model);

        // Assert
        result.Should().Be("same_name");
        indexManager.DropOneAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenHiddenReadBackListsLegacyNaNTtlIndex_ShouldNotThrow()
    {
        // Arrange
        var keys = (IndexKeysDefinition<BsonDocument>)new BsonDocument("Field", 1);
        var model = new CreateIndexModel<BsonDocument>(keys, new CreateIndexOptions
        {
            Name = "new_name",
            Hidden = true
        });

        var matchingIndexDocument = CreateIndexDocument("new_name");
        matchingIndexDocument["hidden"] = true;

        var indexManager = new FakeIndexManager(
            new Queue<Func<Task<String>>>([() => Task.FromResult("new_name")]),
            new Queue<Func<Task<IAsyncCursor<BsonDocument>>>>([() => Listing(matchingIndexDocument, CreateLegacyNaNTtlIndexDocument("legacy_ttl"))]));

        // Act
        var result = await indexManager.CreateOneOrUpdateAsync(model);

        // Assert
        result.Should().Be("new_name");
        indexManager.DropOneAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenHiddenRequestedAndServerAlreadyMatches_ShouldNotDropOrRecreate()
    {
        // Arrange - the common case for a caller that sets Hidden: the server already honored it (either a
        // brand-new index, or a server version that does raise the conflict and already resolved it above), so
        // the read-back must not falsely trigger a drop/recreate.
        var keys = (IndexKeysDefinition<BsonDocument>)new BsonDocument("Field", 1);
        var model = new CreateIndexModel<BsonDocument>(keys, new CreateIndexOptions
        {
            Name = "same_name",
            Hidden = true
        });

        var matchingIndexDocument = new BsonDocument
        {
            ["name"] = "same_name",
            ["key"] = new BsonDocument("Field", 1),
            ["hidden"] = true
        };

        var createOneResults = new Queue<Func<Task<String>>>([() => Task.FromResult("same_name")]);
        var listResults = new Queue<Func<Task<IAsyncCursor<BsonDocument>>>>(
        [
            () => Task.FromResult<IAsyncCursor<BsonDocument>>(new SingleBatchAsyncCursor<BsonDocument>([matchingIndexDocument]))
        ]);

        var indexManager = new FakeIndexManager(createOneResults, listResults);

        // Act
        var result = await indexManager.CreateOneOrUpdateAsync(model);

        // Assert
        result.Should().Be("same_name");
        indexManager.DropOneAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenListedIndexBuildFails_ShouldPropagateBuildFailure()
    {
        // Arrange - listIndexes also reports an index a concurrent caller is still building, so the listed match
        // is not proof of a finished index; the retried creation waits for that build and reports its failure.
        var keys = (IndexKeysDefinition<BsonDocument>)new BsonDocument("Field", 1);
        var model = new CreateIndexModel<BsonDocument>(keys, new CreateIndexOptions
        {
            Name = "new_name",
            Unique = true
        });

        var conflict = CreateConflictException("new_name");
        var buildFailure = CreateCommandException("new_name", 11000, "DuplicateKey");
        var buildingIndexDocument = CreateIndexDocument("new_name");
        buildingIndexDocument["unique"] = true;

        var indexManager = new FakeIndexManager(
            new Queue<Func<Task<String>>>([
                () => throw conflict,
                () => Task.FromResult("__chaosMongoCollationProbe_ignored"),
                () => throw buildFailure
            ]),
            new Queue<Func<Task<IAsyncCursor<BsonDocument>>>>([() => Listing(buildingIndexDocument), () => Listing(buildingIndexDocument)]));

        // Act
        var act = async () => await indexManager.CreateOneOrUpdateAsync(model);

        // Assert
        var thrown = await act.Should().ThrowAsync<MongoCommandException>();
        thrown.Which.Should().BeSameAs(buildFailure);
        indexManager.DropOneAsyncCalls.Should().ContainSingle()
                    .Which.Name.Should().StartWith("__chaosMongoCollationProbe_");
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenListedMatchKeepsConflicting_ShouldRethrowOriginalExceptionAfterBoundedAttempts()
    {
        // Arrange - an explicit simple collation needs no probe, so every create below is the real model.
        var keys = (IndexKeysDefinition<BsonDocument>)new BsonDocument("Field", 1);
        var model = new CreateIndexModel<BsonDocument>(keys, new CreateIndexOptions
        {
            Name = "new_name",
            Collation = Collation.Simple
        });

        var originalConflict = CreateConflictException("new_name");
        var matchingListing = () => Listing(CreateIndexDocument("new_name"));
        var retryConflict = CreateConflictException("new_name");

        var indexManager = new FakeIndexManager(
            new Queue<Func<Task<String>>>([
                () => throw originalConflict,
                () => throw retryConflict,
                () => throw retryConflict,
                () => throw retryConflict
            ]),
            new Queue<Func<Task<IAsyncCursor<BsonDocument>>>>([matchingListing, matchingListing, matchingListing]));

        // Act
        var act = async () => await indexManager.CreateOneOrUpdateAsync(model);

        // Assert
        var thrown = await act.Should().ThrowAsync<MongoCommandException>();
        thrown.Which.Should().BeSameAs(originalConflict);
        indexManager.CreateOneAsyncCalls.Should().HaveCount(4);
        indexManager.DropOneAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenListingBackCollationProbeFailsWithCancellation_ShouldStillCleanUpProbeOnIndependentContextAndRethrowCancellation()
    {
        // Arrange
        var keys = (IndexKeysDefinition<BsonDocument>)new BsonDocument("Field", 1);
        var model = new CreateIndexModel<BsonDocument>(keys, new CreateIndexOptions
        {
            Name = "new_name",
            Collation = new Collation("en")
        });

        var conflict = CreateConflictException("new_name");

        var createOneResults = new Queue<Func<Task<String>>>(
        [
            () => throw conflict,
            // The probe index is actually created successfully this time ...
            () => Task.FromResult("__chaosMongoCollationProbe_ignored")
        ]);
        var listResults = new Queue<Func<Task<IAsyncCursor<BsonDocument>>>>(
        [
            () => Task.FromResult<IAsyncCursor<BsonDocument>>(new SingleBatchAsyncCursor<BsonDocument>([])),
            // ... but reading its resolved collation back via listIndexes fails with a cancellation.
            () => throw new OperationCanceledException()
        ]);

        var indexManager = new FakeIndexManager(createOneResults, listResults);

        // Act
        var act = async () => await indexManager.CreateOneOrUpdateAsync(model);

        // Assert - cleanup still runs on its own independent context even though the failure happened after
        // creation succeeded, while reading the probe back.
        await act.Should().ThrowAsync<OperationCanceledException>();
        indexManager.DropOneAsyncCalls.Should().HaveCount(1);
        indexManager.DropOneAsyncCalls[0].Name.Should().StartWith("__chaosMongoCollationProbe_");
        indexManager.DropOneAsyncCalls[0].CancellationToken.IsCancellationRequested.Should().BeFalse();
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenNoCandidateIsIdentifiedAndRetryStillConflictsWithNoMatch_ShouldRethrowOriginalException()
    {
        // Arrange
        var keys = (IndexKeysDefinition<BsonDocument>)new BsonDocument("Field", 1);
        var model = new CreateIndexModel<BsonDocument>(keys, new CreateIndexOptions
        {
            Name = "new_name"
        });

        var originalConflict = CreateConflictException("new_name");
        var retryConflict = CreateConflictException("new_name");

        var createOneResults = new Queue<Func<Task<String>>>(
        [
            () => throw originalConflict,
            () => throw retryConflict
        ]);
        var listResults = new Queue<Func<Task<IAsyncCursor<BsonDocument>>>>(
        [
            () => Task.FromResult<IAsyncCursor<BsonDocument>>(new SingleBatchAsyncCursor<BsonDocument>([])),
            () => Task.FromResult<IAsyncCursor<BsonDocument>>(new SingleBatchAsyncCursor<BsonDocument>([]))
        ]);

        var indexManager = new FakeIndexManager(createOneResults, listResults);

        // Act
        var act = async () => await indexManager.CreateOneOrUpdateAsync(model);

        // Assert
        var thrown = await act.Should().ThrowAsync<MongoCommandException>();
        thrown.Which.Should().BeSameAs(originalConflict);
        indexManager.DropOneAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenOmittedCollationLooksSatisfiedAndCollectionHasNoDefault_ShouldKeepIndexAndConfirmByRetryingCreation()
    {
        // Arrange - a concurrent caller already created the desired index on a collection with no custom default
        // collation: the probe resolves to no collation, so the listed index really matches and is left in place.
        var keys = (IndexKeysDefinition<BsonDocument>)new BsonDocument("Field", 1);
        var model = new CreateIndexModel<BsonDocument>(keys, new CreateIndexOptions
        {
            Name = "new_name"
        });

        var conflict = CreateConflictException("new_name");
        var matchingListing = () => Listing(CreateIndexDocument("new_name"));

        var indexManager = new FakeIndexManager(
            new Queue<Func<Task<String>>>([
                () => throw conflict,
                () => Task.FromResult("__chaosMongoCollationProbe_ignored"),
                () => Task.FromResult("new_name")
            ]),
            new Queue<Func<Task<IAsyncCursor<BsonDocument>>>>([matchingListing, matchingListing]));

        // Act
        var result = await indexManager.CreateOneOrUpdateAsync(model);

        // Assert
        result.Should().Be("new_name");
        indexManager.DropOneAsyncCalls.Should().ContainSingle()
                    .Which.Name.Should().StartWith("__chaosMongoCollationProbe_");
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenProbeWithoutSparseIsAlsoRejected_ShouldPropagateRejection()
    {
        // Arrange
        var conflict = CreateConflictException("new_name");
        var sparseRejection = CreateCommandException("probe", 72, "InvalidOptions");
        var retryRejection = CreateCommandException("probe", 72, "InvalidOptions");

        var indexManager = new FakeIndexManager(
            new Queue<Func<Task<String>>>([
                () => throw conflict,
                () => throw sparseRejection,
                () => throw retryRejection
            ]),
            new Queue<Func<Task<IAsyncCursor<BsonDocument>>>>([EmptyListing]));

        // Act
        var act = async () => await indexManager.CreateOneOrUpdateAsync(CreateModelRequiringCollationProbe());

        // Assert
        var thrown = await act.Should().ThrowAsync<MongoCommandException>();
        thrown.Which.Should().BeSameAs(retryRejection);
        indexManager.DropOneAsyncCallCount.Should().Be(2);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenRenamedWildcardIndexIsGoneAndOnlySiblingWithOtherProjectionIsListed_ShouldNotDropSibling()
    {
        // Arrange - a concurrent caller already dropped the index being renamed but has not recreated it yet.
        var keys = (IndexKeysDefinition<BsonDocument>)new BsonDocument("$**", 1);
        var model = new CreateIndexModel<BsonDocument>(keys, new CreateIndexOptions<BsonDocument>
        {
            Name = "new_name",
            WildcardProjection = new BsonDocument("a", 1)
        });

        var conflict = CreateConflictException("new_name");
        var siblingIndexDocument = new BsonDocument
        {
            ["name"] = "sibling",
            ["key"] = new BsonDocument("$**", 1),
            ["wildcardProjection"] = new BsonDocument("b", 1)
        };

        var indexManager = new FakeIndexManager(
            new Queue<Func<Task<String>>>([
                () => throw conflict,
                () => Task.FromResult("new_name")
            ]),
            new Queue<Func<Task<IAsyncCursor<BsonDocument>>>>([() => Listing(siblingIndexDocument)]));

        // Act
        var result = await indexManager.CreateOneOrUpdateAsync(model);

        // Assert
        result.Should().Be("new_name");
        indexManager.DropOneAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenSameNameIndexHasLegacyNaNTtl_ShouldDropAndRecreate()
    {
        // Arrange - NaN never equals a requested value, so the same-name index is never reported as satisfied.
        var keys = (IndexKeysDefinition<BsonDocument>)new BsonDocument("Field", 1);
        var model = new CreateIndexModel<BsonDocument>(keys, new CreateIndexOptions
        {
            Name = "new_name",
            ExpireAfter = TimeSpan.FromHours(1)
        });

        var conflict = CreateConflictException("new_name");
        var legacyIndexDocument = CreateLegacyNaNTtlIndexDocument("new_name");
        legacyIndexDocument["key"] = new BsonDocument("Field", 1);

        var indexManager = new FakeIndexManager(
            new Queue<Func<Task<String>>>([
                () => throw conflict,
                () => Task.FromResult("new_name")
            ]),
            new Queue<Func<Task<IAsyncCursor<BsonDocument>>>>([() => Listing(legacyIndexDocument)]));

        // Act
        var result = await indexManager.CreateOneOrUpdateAsync(model);

        // Assert
        result.Should().Be("new_name");
        indexManager.DropOneAsyncCalls.Should().ContainSingle()
                    .Which.Name.Should().Be("new_name");
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenServerSilentlyAcceptsHiddenMismatch_ShouldDropAndRecreate()
    {
        // Arrange - simulates a MongoDB server version (observed: 7.0, fixed by 8.0) that accepts a
        // createIndexes request differing from an existing same-name index only in Hidden without raising
        // IndexOptionsConflict, leaving the existing index's hidden value unchanged. CreateOneAsync therefore
        // succeeds on its first call, so the exception-driven path in ResolveConflictAsync never runs; only the
        // post-success read-back below can observe and correct this.
        var keys = (IndexKeysDefinition<BsonDocument>)new BsonDocument("Field", 1);
        var model = new CreateIndexModel<BsonDocument>(keys, new CreateIndexOptions
        {
            Name = "same_name",
            Hidden = true
        });

        var staleIndexDocument = new BsonDocument
        {
            ["name"] = "same_name",
            ["key"] = new BsonDocument("Field", 1)
            // No "hidden" field: the existing index's stored value is still the server default (false),
            // because the server silently left it untouched instead of raising a conflict.
        };

        var createOneResults = new Queue<Func<Task<String>>>(
        [
            () => Task.FromResult("same_name"),
            () => Task.FromResult("same_name")
        ]);
        var listResults = new Queue<Func<Task<IAsyncCursor<BsonDocument>>>>(
        [
            () => Task.FromResult<IAsyncCursor<BsonDocument>>(new SingleBatchAsyncCursor<BsonDocument>([staleIndexDocument]))
        ]);

        var indexManager = new FakeIndexManager(createOneResults, listResults);

        // Act
        var result = await indexManager.CreateOneOrUpdateAsync(model);

        // Assert
        result.Should().Be("same_name");
        indexManager.DropOneAsyncCallCount.Should().Be(1);
        indexManager.DropOneAsyncCalls[0].Name.Should().Be("same_name");
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenSiblingIndexHasLegacyNaNTtl_ShouldReplaceSameNameIndexOnly()
    {
        // Arrange - the supported server versions refuse to create this legacy state, so it needs a scripted listing.
        var keys = (IndexKeysDefinition<BsonDocument>)new BsonDocument("Field", 1);
        var model = new CreateIndexModel<BsonDocument>(keys, new CreateIndexOptions
        {
            Name = "new_name",
            Unique = true
        });

        var conflict = CreateConflictException("new_name");

        var indexManager = new FakeIndexManager(
            new Queue<Func<Task<String>>>([
                () => throw conflict,
                () => Task.FromResult("new_name")
            ]),
            new Queue<Func<Task<IAsyncCursor<BsonDocument>>>>([() => Listing(CreateIndexDocument("new_name"), CreateLegacyNaNTtlIndexDocument("legacy_ttl"))]));

        // Act
        var result = await indexManager.CreateOneOrUpdateAsync(model);

        // Assert
        result.Should().Be("new_name");
        indexManager.DropOneAsyncCalls.Should().ContainSingle()
                    .Which.Name.Should().Be("new_name");
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WhenSparseProbeIsRejectedWithInvalidOptions_ShouldRetryProbeWithoutSparse()
    {
        // Arrange
        var conflict = CreateConflictException("new_name");
        var rejection = CreateCommandException("probe", 72, "InvalidOptions");

        var indexManager = new FakeIndexManager(
            new Queue<Func<Task<String>>>([
                () => throw conflict,
                () => throw rejection,
                () => Task.FromResult("__chaosMongoCollationProbe_ignored"),
                () => Task.FromResult("new_name")
            ]),
            new Queue<Func<Task<IAsyncCursor<BsonDocument>>>>([EmptyListing, EmptyListing]));

        // Act
        var result = await indexManager.CreateOneOrUpdateAsync(CreateModelRequiringCollationProbe());

        // Assert
        result.Should().Be("new_name");
        var probes = indexManager.CreateOneAsyncCalls.Where(call => IsCollationProbe(call.Model)).ToList();
        probes.Should().HaveCount(2);
        probes[0].Model.Options.Sparse.Should().BeTrue();
        probes[1].Model.Options.Sparse.Should().BeNull();
        indexManager.DropOneAsyncCalls.Select(call => call.Name).Should()
                    .BeEquivalentTo(probes.Select(call => call.Model.Options.Name));
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenCollationProbeIsRequired_ShouldForwardCallerOptionsToProbe()
    {
        // Arrange
        var options = new CreateOneIndexOptions
        {
            MaxTime = TimeSpan.FromSeconds(5)
        };
        var conflict = CreateConflictException("new_name");

        var indexManager = new FakeIndexManager(
            new Queue<Func<Task<String>>>([
                () => throw conflict,
                () => Task.FromResult("__chaosMongoCollationProbe_ignored"),
                () => Task.FromResult("new_name")
            ]),
            new Queue<Func<Task<IAsyncCursor<BsonDocument>>>>([EmptyListing, EmptyListing]));

        // Act
        await indexManager.CreateOneOrUpdateAsync(null!, CreateModelRequiringCollationProbe(), options);

        // Assert
        indexManager.CreateOneAsyncCalls.Should().ContainSingle(call => IsCollationProbe(call.Model))
                    .Which.Options.Should().BeSameAs(options);
    }

    [Test]
    public async Task CreateOneOrUpdateAsync_WithSession_WhenHiddenMismatchFallsBackToConflictResolution_ShouldForwardCallerOptionsToProbe()
    {
        // Arrange
        var options = new CreateOneIndexOptions
        {
            MaxTime = TimeSpan.FromSeconds(5)
        };
        var conflict = CreateConflictException("new_name");

        var indexManager = new FakeIndexManager(
            new Queue<Func<Task<String>>>([
                () => Task.FromResult("new_name"),
                () => throw conflict,
                () => Task.FromResult("__chaosMongoCollationProbe_ignored"),
                () => Task.FromResult("new_name")
            ]),
            new Queue<Func<Task<IAsyncCursor<BsonDocument>>>>([() => Listing(CreateIndexDocument("new_name")), EmptyListing, EmptyListing]));

        // Act
        await indexManager.CreateOneOrUpdateAsync(null!, CreateModelRequiringCollationProbe(), options);

        // Assert
        indexManager.CreateOneAsyncCalls.Should().ContainSingle(call => IsCollationProbe(call.Model))
                    .Which.Options.Should().BeSameAs(options);
    }

    private static MongoCommandException CreateCommandException(String requestedName, Int32 code, String codeName)
    {
        var connectionId = new ConnectionId(new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017)));
        var command = new BsonDocument
        {
            ["createIndexes"] = "test",
            ["indexes"] = new BsonArray
            {
                new BsonDocument
                {
                    ["name"] = requestedName,
                    ["key"] = new BsonDocument("Field", 1)
                }
            }
        };
        var result = new BsonDocument
        {
            ["ok"] = 0.0,
            ["errmsg"] = "Index already exists with a different name",
            ["code"] = code,
            ["codeName"] = codeName
        };

        return new MongoCommandException(connectionId, "Index options conflict", command, result);
    }

    private static MongoCommandException CreateConflictException(String requestedName)
        => CreateCommandException(requestedName, 85, "IndexOptionsConflict");

    private static BsonDocument CreateIndexDocument(String name)
        => new()
        {
            ["name"] = name,
            ["key"] = new BsonDocument("Field", 1)
        };

    private static BsonDocument CreateLegacyNaNTtlIndexDocument(String name)
        => new()
        {
            ["name"] = name,
            ["key"] = new BsonDocument("Timestamp", 1),
            ["expireAfterSeconds"] = Double.NaN
        };

    private static CreateIndexModel<BsonDocument> CreateModelRequiringCollationProbe()
        => new((IndexKeysDefinition<BsonDocument>)new BsonDocument("Field", 1), new CreateIndexOptions
        {
            Name = "new_name",
            Collation = new Collation("en"),
            Hidden = true
        });

    private static Task<IAsyncCursor<BsonDocument>> EmptyListing()
        => Listing();

    private static Boolean IsCollationProbe(CreateIndexModel<BsonDocument> model)
        => model.Options.Name.StartsWith("__chaosMongoCollationProbe_", StringComparison.Ordinal);

    private static Task<IAsyncCursor<BsonDocument>> Listing(params BsonDocument[] documents)
        => Task.FromResult<IAsyncCursor<BsonDocument>>(new SingleBatchAsyncCursor<BsonDocument>(documents));

    /// <summary>
    /// A hand-crafted <see cref="IMongoIndexManager{TDocument}"/> that scripts the exact sequence of
    /// <c>CreateOneAsync</c> and <c>ListAsync</c> responses a test needs, instead of a dynamically generated mock
    /// proxy. Only the members <see cref="MongoIndexManagerExtensions"/>'s <c>CreateOneOrUpdateAsync</c> actually
    /// calls are implemented, with the session overloads sharing the same script as the sessionless ones; every
    /// other member throws <see cref="NotSupportedException"/>.
    /// </summary>
    private sealed class FakeIndexManager : IMongoIndexManager<BsonDocument>
    {
        private readonly Queue<Func<Task<String>>> _createOneResults;
        private readonly Queue<Func<Task<IAsyncCursor<BsonDocument>>>> _listResults;

        public FakeIndexManager(Queue<Func<Task<String>>> createOneResults, Queue<Func<Task<IAsyncCursor<BsonDocument>>>> listResults)
        {
            _createOneResults = createOneResults;
            _listResults = listResults;
        }

        public List<(CreateIndexModel<BsonDocument> Model, CreateOneIndexOptions? Options)> CreateOneAsyncCalls { get; } = [];

        public Int32 DropOneAsyncCallCount { get; private set; }

        public List<(String Name, CancellationToken CancellationToken)> DropOneAsyncCalls { get; } = [];

        public CollectionNamespace CollectionNamespace => throw Unsupported(nameof(CollectionNamespace));

        public IBsonSerializer<BsonDocument> DocumentSerializer { get; } = BsonSerializer.LookupSerializer<BsonDocument>();

        public MongoCollectionSettings Settings { get; } = new();

        private static NotSupportedException Unsupported(String member)
            => new($"Member '{member}' is not supported by the fake index manager.");

        public IEnumerable<String> CreateMany(IEnumerable<CreateIndexModel<BsonDocument>> models, CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(CreateMany));

        public IEnumerable<String> CreateMany(IEnumerable<CreateIndexModel<BsonDocument>> models, CreateManyIndexesOptions options, CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(CreateMany));

        public IEnumerable<String> CreateMany(IClientSessionHandle session, IEnumerable<CreateIndexModel<BsonDocument>> models, CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(CreateMany));

        public IEnumerable<String> CreateMany(IClientSessionHandle session, IEnumerable<CreateIndexModel<BsonDocument>> models, CreateManyIndexesOptions options,
                                              CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(CreateMany));

        public Task<IEnumerable<String>> CreateManyAsync(IEnumerable<CreateIndexModel<BsonDocument>> models, CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(CreateManyAsync));

        public Task<IEnumerable<String>> CreateManyAsync(IEnumerable<CreateIndexModel<BsonDocument>> models, CreateManyIndexesOptions options,
                                                         CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(CreateManyAsync));

        public Task<IEnumerable<String>> CreateManyAsync(IClientSessionHandle session, IEnumerable<CreateIndexModel<BsonDocument>> models,
                                                         CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(CreateManyAsync));

        public Task<IEnumerable<String>> CreateManyAsync(IClientSessionHandle session, IEnumerable<CreateIndexModel<BsonDocument>> models, CreateManyIndexesOptions options,
                                                         CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(CreateManyAsync));

        public String CreateOne(CreateIndexModel<BsonDocument> model, CreateOneIndexOptions options, CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(CreateOne));

        public String CreateOne(IndexKeysDefinition<BsonDocument> keys, CreateIndexOptions options, CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(CreateOne));

        public String CreateOne(IClientSessionHandle session, IndexKeysDefinition<BsonDocument> keys, CreateIndexOptions options, CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(CreateOne));

        public String CreateOne(IClientSessionHandle session, CreateIndexModel<BsonDocument> model, CreateOneIndexOptions options, CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(CreateOne));

        public Task<String> CreateOneAsync(CreateIndexModel<BsonDocument> model, CreateOneIndexOptions? options, CancellationToken cancellationToken = default)
        {
            CreateOneAsyncCalls.Add((model, options));
            return _createOneResults.Dequeue().Invoke();
        }

        public Task<String> CreateOneAsync(IndexKeysDefinition<BsonDocument> keys, CreateIndexOptions options, CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(CreateOneAsync));

        public Task<String> CreateOneAsync(IClientSessionHandle session, IndexKeysDefinition<BsonDocument> keys, CreateIndexOptions options,
                                           CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(CreateOneAsync));

        public Task<String> CreateOneAsync(IClientSessionHandle session, CreateIndexModel<BsonDocument> model, CreateOneIndexOptions? options,
                                           CancellationToken cancellationToken = default)
            => CreateOneAsync(model, options, cancellationToken);

        public void DropAll(DropIndexOptions options, CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(DropAll));

        public void DropAll(CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(DropAll));

        public void DropAll(IClientSessionHandle session, CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(DropAll));

        public void DropAll(IClientSessionHandle session, DropIndexOptions options, CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(DropAll));

        public Task DropAllAsync(DropIndexOptions options, CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(DropAllAsync));

        public Task DropAllAsync(CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(DropAllAsync));

        public Task DropAllAsync(IClientSessionHandle session, CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(DropAllAsync));

        public Task DropAllAsync(IClientSessionHandle session, DropIndexOptions options, CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(DropAllAsync));

        public void DropOne(String name, CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(DropOne));

        public void DropOne(String name, DropIndexOptions options, CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(DropOne));

        public void DropOne(IClientSessionHandle session, String name, CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(DropOne));

        public void DropOne(IClientSessionHandle session, String name, DropIndexOptions options, CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(DropOne));

        public Task DropOneAsync(String name, CancellationToken cancellationToken = default)
        {
            DropOneAsyncCallCount++;
            DropOneAsyncCalls.Add((name, cancellationToken));
            return Task.CompletedTask;
        }

        public Task DropOneAsync(String name, DropIndexOptions options, CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(DropOneAsync));

        public Task DropOneAsync(IClientSessionHandle session, String name, CancellationToken cancellationToken = default)
            => DropOneAsync(name, cancellationToken);

        public Task DropOneAsync(IClientSessionHandle session, String name, DropIndexOptions options, CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(DropOneAsync));

        public IAsyncCursor<BsonDocument> List(CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(List));

        public IAsyncCursor<BsonDocument> List(ListIndexesOptions options, CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(List));

        public IAsyncCursor<BsonDocument> List(IClientSessionHandle session, CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(List));

        public IAsyncCursor<BsonDocument> List(IClientSessionHandle session, ListIndexesOptions options, CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(List));

        public Task<IAsyncCursor<BsonDocument>> ListAsync(CancellationToken cancellationToken = default)
            => _listResults.Dequeue().Invoke();

        public Task<IAsyncCursor<BsonDocument>> ListAsync(ListIndexesOptions options, CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(ListAsync));

        public Task<IAsyncCursor<BsonDocument>> ListAsync(IClientSessionHandle session, CancellationToken cancellationToken = default)
            => ListAsync(cancellationToken);

        public Task<IAsyncCursor<BsonDocument>> ListAsync(IClientSessionHandle session, ListIndexesOptions options, CancellationToken cancellationToken = default)
            => throw Unsupported(nameof(ListAsync));
    }

    /// <summary>
    /// A minimal <see cref="IAsyncCursor{TDocument}"/> that returns its documents in a single batch, hand-written
    /// instead of mocked, because Moq's dynamically generated proxy assembly cannot access an internal
    /// <typeparamref name="TDocument"/>.
    /// </summary>
    private sealed class SingleBatchAsyncCursor<TDocument> : IAsyncCursor<TDocument>
    {
        private readonly TDocument[] _documents;
        private Boolean _returned;

        public SingleBatchAsyncCursor(TDocument[] documents) => _documents = documents;

        public IEnumerable<TDocument> Current => _documents;

        public Boolean MoveNext(CancellationToken cancellationToken = default)
        {
            if (_returned)
            {
                return false;
            }

            _returned = true;
            return _documents.Length > 0;
        }

        public Task<Boolean> MoveNextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(MoveNext(cancellationToken));

        public void Dispose() { }
    }
}
