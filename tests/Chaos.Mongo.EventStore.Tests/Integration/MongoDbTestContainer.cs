// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using DotNet.Testcontainers.Containers;
using Testcontainers.MongoDb;

public static class MongoDbTestContainer
{
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static MongoDbContainer? _container;

    public static async Task<MongoDbContainer> StartContainerAsync()
    {
        if (_container is { State: TestcontainersStates.Running })
            return _container;

        await _gate.WaitAsync();
        try
        {
            if (_container is { State: TestcontainersStates.Running })
                return _container;

            _container = new MongoDbBuilder()
                         .WithImage("mongo:8")
                         .WithReplicaSet()
                         .Build();

            await _container.StartAsync();
            return _container;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static async Task StopContainerAsync()
    {
        if (_container is null)
            return;

        var container = _container;
        _container = null;
        await container.DisposeAsync();
    }
}
