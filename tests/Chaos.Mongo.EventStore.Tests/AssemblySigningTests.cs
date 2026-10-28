// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests;

using FluentAssertions;
using NUnit.Framework;
using System.Reflection;

public class AssemblySigningTests
{
    private const String ReleasedPublicKeyToken = "258e5594a5dc8a36";

    [Test]
    public void GetPublicKeyToken_ProductAssembly_ReturnsReleasedToken()
        => GetPublicKeyToken(typeof(MongoEventStoreOptions<>).Assembly).Should().Be(ReleasedPublicKeyToken);

    [Test]
    public void GetPublicKeyToken_TestAssembly_ReturnsReleasedToken()
        => GetPublicKeyToken(typeof(AssemblySigningTests).Assembly).Should().Be(ReleasedPublicKeyToken);

    private static String GetPublicKeyToken(Assembly assembly)
        => Convert.ToHexString(assembly.GetName().GetPublicKeyToken() ?? []).ToLowerInvariant();
}
