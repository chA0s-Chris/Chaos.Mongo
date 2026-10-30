// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.LoadTests;

using Chaos.Mongo.TestSupport;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using System.Globalization;
using Testcontainers.MongoDb;

/// <summary>
/// The MongoDB deployment a load test runs against.
/// </summary>
internal sealed class MongoTopology : IAsyncDisposable
{
    private const Int32 MongoPort = 27017;
    private const String ReplicaSetName = "rs0";

    private readonly List<IContainer> _containers;
    private readonly INetwork? _network;

    private MongoTopology(List<IContainer> containers, INetwork? network, String connectionString, String description)
    {
        _containers = containers;
        _network = network;
        ConnectionString = connectionString;
        Description = description;
    }

    public String ConnectionString { get; }

    public String Description { get; }

    public static async Task<MongoTopology> StartSingleNodeAsync()
    {
        var container = new MongoDbBuilder(MongoDbTestImages.Primary)
                        .WithReplicaSet()
                        // MongoDB 8.x images crash on Linux kernels newer than 6.19 unless rseq is
                        // pinned. See https://jira.mongodb.org/browse/SERVER-121912
                        .WithEnvironment("GLIBC_TUNABLES", "glibc.pthread.rseq=1")
                        .WithCreateParameterModifier(parameters => ConfigureHost(parameters))
                        .Build();

        await container.StartAsync();
        return new MongoTopology([container], null, container.GetConnectionString(), "single-node replica set (Testcontainers) on localhost, no injected latency");
    }

    /// <summary>
    /// Starts three members in one Docker network, delays every packet a member sends by
    /// <paramref name="delayPerDirection"/> and connects directly to the primary.
    /// </summary>
    public static async Task<MongoTopology> StartThreeNodeAsync(TimeSpan delayPerDirection)
    {
        var network = new NetworkBuilder().WithName($"chaos-mongo-loadtest-{Guid.NewGuid():N}").Build();
        await network.CreateAsync();

        var image = new ImageFromDockerfileBuilder()
                    .WithDockerfileDirectory(Path.Combine(AppContext.BaseDirectory, "netem"))
                    .WithDockerfile("Dockerfile")
                    .WithName("chaos-mongo-loadtest-netem:8")
                    .WithBuildArgument("MONGO_IMAGE", MongoDbTestImages.Primary)
                    .WithCleanUp(false)
                    .Build();
        await image.CreateAsync();

        var containers = Enumerable.Range(0, 3)
                                   .Select(index => new ContainerBuilder(image)
                                                    .WithNetwork(network)
                                                    .WithNetworkAliases(MemberHost(index))
                                                    .WithCommand("--replSet", ReplicaSetName, "--bind_ip_all")
                                                    .WithEnvironment("GLIBC_TUNABLES", "glibc.pthread.rseq=1")
                                                    .WithPortBinding(MongoPort, true)
                                                    .WithCreateParameterModifier(parameters
                                                                                     => ConfigureHost(parameters).CapAdd = ["NET_ADMIN"])
                                                    .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Waiting for connections"))
                                                    .Build())
                                   .ToList();

        await Task.WhenAll(containers.Select(container => container.StartAsync()));

        var delay = $"{delayPerDirection.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)}ms";
        foreach (var container in containers)
        {
            await ExecAsync(container, "tc", "qdisc", "add", "dev", "eth0", "root", "netem", "delay", delay);
        }

        var members = String.Join(",", Enumerable.Range(0, 3).Select(index =>
                                                                         $"{{_id: {index}, host: '{MemberHost(index)}:{MongoPort}', priority: {(index == 0 ? 2 : 1)}}}"));
        await ExecAsync(containers[0], "mongosh", "--quiet", "--eval", $"rs.initiate({{_id: '{ReplicaSetName}', members: [{members}]}})");
        await WaitForPrimaryAsync(containers[0]);

        var primary = containers[0];
        var connectionString = $"mongodb://{primary.Hostname}:{primary.GetMappedPublicPort(MongoPort)}/?directConnection=true&w=majority";
        var roundTrip = 2 * delayPerDirection.TotalMilliseconds;
        return new MongoTopology(containers,
                                 network,
                                 connectionString,
                                 $"three-node replica set (Docker network, w: majority, tc netem {delay} per direction): " +
                                 $"{roundTrip} ms round trip between members, {delayPerDirection.TotalMilliseconds} ms between client and primary");
    }

    private static HostConfig ConfigureHost(CreateContainerParameters parameters)
    {
        var hostConfig = parameters.HostConfig ??= new HostConfig();
        hostConfig.Ulimits =
        [
            new Ulimit
            {
                Name = "nofile",
                Soft = 65536,
                Hard = 65536
            }
        ];
        return hostConfig;
    }

    private static async Task<String> ExecAsync(IContainer container, params String[] command)
    {
        var result = await container.ExecAsync(command);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"'{String.Join(" ", command)}' failed with exit code {result.ExitCode}: {result.Stderr}{result.Stdout}");
        }

        return result.Stdout;
    }

    private static String MemberHost(Int32 index) => $"mongo{index}";

    private static async Task WaitForPrimaryAsync(IContainer container)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            var states = await ExecAsync(container, "mongosh", "--quiet", "--eval", "rs.status().members.map(m => m.stateStr).join(',')");
            if (states.Trim() == "PRIMARY,SECONDARY,SECONDARY")
            {
                return;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("The replica set did not elect the first member as primary in time.");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var container in _containers)
        {
            await container.DisposeAsync();
        }

        if (_network is not null)
        {
            await _network.DisposeAsync();
        }
    }
}
