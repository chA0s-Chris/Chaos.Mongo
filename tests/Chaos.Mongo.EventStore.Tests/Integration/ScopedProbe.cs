// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integration;

/// <summary>
/// A scoped service that records its disposal, showing which dependency-injection scope resolved it.
/// </summary>
internal sealed class ScopedProbe : IDisposable
{
    public Boolean IsDisposed { get; private set; }

    public void Dispose() => IsDisposed = true;
}
