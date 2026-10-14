// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

/// <summary>
/// Aborts an anchoring run after a phase, standing in for a process crash.
/// </summary>
internal sealed class SimulatedCrashException : Exception
{
    public SimulatedCrashException()
        : base("Simulated crash.") { }
}
