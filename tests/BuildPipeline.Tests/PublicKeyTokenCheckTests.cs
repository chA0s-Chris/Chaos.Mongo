// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace BuildPipeline.Tests;

using FluentAssertions;
using NUnit.Framework;

public class PublicKeyTokenCheckTests
{
    private static readonly Byte[] OwnAssemblyImage = File.ReadAllBytes(typeof(PublicKeyTokenCheckTests).Assembly.Location);

    // This test assembly is itself public-signed with the released key, like every project in the solution.
    private const String OwnToken = "258e5594a5dc8a36";

    [Test]
    public void ComputeToken_ReleasedPublicKey_MatchesReleasedToken()
    {
        // The <PublicKey> hex blob from the root Directory.Build.props, the contents of Chaos.Mongo.Public.snk.
        var publicKey = Convert.FromHexString(
            "0024000004800000940000000602000000240000525341310004000001000100192c28f3d57aaf38a7f22e8e2ec4e395d9" +
            "9ee1fccc6f55a0f5c3754c660921616f537cb42141d919319ec395e333d38993ee0cd62084f582d6de1632d7465ae5757e7" +
            "bba992e10ffd1c3b5d0fcf730899e7cb0e2b5ee380be79e687f1062c2a08c7c17400f202c3e66b01a4eb3efed7d1c7afb09" +
            "0df61e9cc0bc2aee97441ce6");

        var token = PublicKeyTokenCheck.ComputeToken(publicKey);

        token.Should().Be(OwnToken);
    }

    [Test]
    public void GetMismatch_AssemblySignedWithDifferentKey_ReturnsFailureNamingBothTokens()
    {
        // A real assembly signed with a different key, not a hand-picked wrong expected token: NUnit ships
        // strong-named with its own key, unrelated to Chaos.Mongo.Public.snk. The actual token is derived
        // independently via AssemblyName.GetPublicKeyToken(), not via the ComputeToken logic under test.
        var otherAssembly = typeof(TestAttribute).Assembly;
        var otherAssemblyImage = File.ReadAllBytes(otherAssembly.Location);
        var actualToken = Convert.ToHexStringLower(otherAssembly.GetName().GetPublicKeyToken()!);

        var result = PublicKeyTokenCheck.GetMismatch(otherAssemblyImage, OwnToken);

        actualToken.Should().NotBe(OwnToken);
        result.Should().Contain(actualToken).And.Contain(OwnToken);
    }

    [Test]
    public void GetMismatch_DifferentExpectedToken_ReturnsFailureNamingBothTokens()
    {
        var result = PublicKeyTokenCheck.GetMismatch(OwnAssemblyImage, "0000000000000000");

        result.Should().Contain(OwnToken).And.Contain("0000000000000000");
    }

    [Test]
    public void GetMismatch_MatchingExpectedToken_ReturnsNull()
    {
        var result = PublicKeyTokenCheck.GetMismatch(OwnAssemblyImage, OwnToken);

        result.Should().BeNull();
    }

    [Test]
    public void GetMismatch_NotAnAssembly_ReturnsFailure()
    {
        var result = PublicKeyTokenCheck.GetMismatch([1, 2, 3, 4], OwnToken);

        result.Should().Be("it is not a .NET assembly");
    }
}
