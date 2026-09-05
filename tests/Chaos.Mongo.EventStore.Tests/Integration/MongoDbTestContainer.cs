// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using Docker.DotNet.Models;
using DotNet.Testcontainers.Containers;
using Testcontainers.MongoDb;

public static class MongoDbTestContainer
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static MongoDbContainer? Container;

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
                        .WithImage("mongo:8")
                        .WithReplicaSet()
                        // MongoDB 8.x images crash on Linux kernels newer than 6.19 unless rseq is
                        // pinned. See https://jira.mongodb.org/browse/SERVER-121912
                        .WithEnvironment("GLIBC_TUNABLES", "glibc.pthread.rseq=1")
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

            await Container.StartAsync();
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
