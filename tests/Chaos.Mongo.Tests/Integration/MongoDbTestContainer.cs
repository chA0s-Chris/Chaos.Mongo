// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests.Integration;

using Chaos.Mongo.TestSupport;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Containers;
using Testcontainers.MongoDb;

public static class MongoDbTestContainer
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static MongoDbContainer? Container;

    /// <summary>
    /// Whether this process started the MongoDB server it uses, rather than sharing its parent's.
    /// </summary>
    public static Boolean OwnsServer => Container is not null;

    /// <summary>
    /// Returns the connection string of the MongoDB server for this test process: the parent's server when this is an
    /// isolated child process (see <see cref="IsolatedConventionTestProcess"/>), otherwise the container this process
    /// starts on first use.
    /// </summary>
    public static async Task<String> GetConnectionStringAsync()
    {
        var inherited = Environment.GetEnvironmentVariable(IsolatedConventionTestProcess.MongoConnectionStringVariable);
        if (!String.IsNullOrEmpty(inherited))
            return inherited;

        return (await StartContainerAsync()).GetConnectionString();
    }

    public static async Task<MongoDbContainer> StartContainerAsync()
    {
        if (Container is { State: TestcontainersStates.Running })
            return Container;

        await Gate.WaitAsync();
        try
        {
            if (Container is { State: TestcontainersStates.Running })
                return Container;

            Container = new MongoDbBuilder()
                        .WithImage(MongoDbTestImages.Selected)
                        .WithReplicaSet()
                        // MongoDB 8.x images crash on Linux kernels newer than 6.19 unless rseq is
                        // pinned. See https://jira.mongodb.org/browse/SERVER-121912
                        .WithEnvironment("GLIBC_TUNABLES", "glibc.pthread.rseq=1")
                        // Keeps the data files in memory: every write on the replica set is journaled and synced to
                        // disk, which made the EventStore suite almost three times slower on a volume.
                        .WithTmpfsMount("/data/db")
                        // Retention tests wait for TTL deletions; the default 60 s monitor interval would make them
                        // wait that long. The parameter is server-wide, so it is set once at startup instead of by a
                        // test while others run.
                        .WithCommand("--setParameter", "ttlMonitorSleepSecs=1")
                        .WithCreateParameterModifier(parameters =>
                        {
                            parameters.HostConfig ??= new HostConfig();
                            parameters.HostConfig.Ulimits =
                            [
                                new Ulimit
                                {
                                    Name = "nofile",
                                    Soft = 65536,
                                    Hard = 65536
                                }
                            ];
                        })
                        .Build();

            await Container.StartWithDiagnosticsAsync();
            return Container;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task StopContainerAsync()
    {
        if (Container is null)
            return;

        var container = Container;
        Container = null;
        await container.DisposeAsync();
    }
}
