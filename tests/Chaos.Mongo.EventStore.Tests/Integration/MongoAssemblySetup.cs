// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

using NUnit.Framework;

[SetUpFixture]
public sealed class MongoAssemblySetup
{
    [OneTimeSetUp]
    public async Task StartContainer() => _ = await MongoDbTestContainer.StartContainerAsync();

    [OneTimeTearDown]
    public async Task StopContainer() => await MongoDbTestContainer.StopContainerAsync();
}
