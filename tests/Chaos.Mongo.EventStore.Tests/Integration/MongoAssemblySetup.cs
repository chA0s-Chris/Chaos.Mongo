// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
[assembly: Chaos.Mongo.TestSupport.DropTestDatabases]

namespace Chaos.Mongo.EventStore.Tests.Integration;

using Chaos.Mongo.TestSupport;
using NUnit.Framework;

[SetUpFixture]
public sealed class MongoAssemblySetup
{
    [OneTimeSetUp]
    public async Task StartContainer() => TestDatabases.UseServer((await MongoDbTestContainer.StartContainerAsync()).GetConnectionString());

    [OneTimeTearDown]
    public async Task StopContainer()
    {
        try
        {
            var cleanup = await TestDatabases.DropRemainingAndFindLeftoversAsync();
            var problems = new List<String>();
            if (cleanup.FailedDrops.Count > 0)
            {
                problems.Add($"These databases could not be dropped: {String.Join(", ", cleanup.FailedDrops)}.");
            }

            // Only the process that owns the server checks it for leftovers: an isolated child process shares the
            // server of its parent, whose tests may still use their databases.
            if (MongoDbTestContainer.OwnsServer && (cleanup.Leftovers.Count > 0))
            {
                problems.Add($"Tests left these databases behind; name them through {nameof(TestDatabases)} so they are dropped: {String.Join(", ", cleanup.Leftovers)}");
            }

            if (problems.Count > 0)
            {
                Assert.Fail(String.Join(" ", problems));
            }
        }
        finally
        {
            await MongoDbTestContainer.StopContainerAsync();
        }
    }
}
