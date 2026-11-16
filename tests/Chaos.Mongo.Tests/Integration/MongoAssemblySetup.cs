// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
[assembly: Chaos.Mongo.TestSupport.DropTestDatabases]

namespace Chaos.Mongo.Tests.Integration;

using Chaos.Mongo.TestSupport;
using NUnit.Framework;

[SetUpFixture]
public sealed class MongoAssemblySetup
{
    [OneTimeSetUp]
    public async Task StartContainer() => TestDatabases.UseServer(await MongoDbTestContainer.GetConnectionStringAsync());

    [OneTimeTearDown]
    public async Task StopContainer()
    {
        try
        {
            var leftovers = await TestDatabases.DropRemainingAndFindLeftoversAsync();

            // Only the process that owns the server checks it: an isolated child process shares the server of its
            // parent, whose tests may still use their databases.
            if (MongoDbTestContainer.OwnsServer && (leftovers.Count > 0))
            {
                Assert.Fail($"Tests left these databases behind; name them through {nameof(TestDatabases)} so they are dropped: {String.Join(", ", leftovers)}");
            }
        }
        finally
        {
            await MongoDbTestContainer.StopContainerAsync();
        }
    }
}
