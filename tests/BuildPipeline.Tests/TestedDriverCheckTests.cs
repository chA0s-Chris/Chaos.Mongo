// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace BuildPipeline.Tests;

using FluentAssertions;
using NUnit.Framework;

public class TestedDriverCheckTests
{
    // An arbitrary version, unrelated to the pin in Directory.Packages.props: the check only compares what it is given.
    private const String Expected = "1.2.3";

    [TestCase(Expected)]
    [TestCase($"{Expected}+abc1234")]
    public void GetFailures_WithExpectedVersion_ReturnsNoFailures(String productVersion)
    {
        TestedDriver[] outputs = [new("Chaos.Mongo.Tests (net10.0)", productVersion), new("BuildPipeline.Tests (net10.0)", null)];

        TestedDriverCheck.GetFailures(Expected, outputs).Should().BeEmpty();
    }

    [TestCase("1.0.0+abc1234")]
    [TestCase($"{Expected}-beta1+abc1234")]
    [TestCase($"{Expected}4")]
    [TestCase("")]
    public void GetFailures_WithAnotherVersion_ReturnsFailureNamingRunAndBothVersions(String productVersion)
    {
        TestedDriver[] outputs = [new("Chaos.Mongo.Tests (net10.0)", productVersion)];

        TestedDriverCheck.GetFailures(Expected, outputs).Should().Equal(
            $"Chaos.Mongo.Tests (net10.0): found '{productVersion}' instead of '{Expected}'.");
    }

    [Test]
    public void GetFailures_WithSomeOutputsOnAnotherVersion_ReturnsOnlyThose()
    {
        TestedDriver[] outputs =
        [
            new("Chaos.Mongo.Tests (net10.0)", "1.0.0+abc1234"),
            new("Chaos.Mongo.EventStore.Tests (net10.0)", $"{Expected}+abc1234"),
            new("Chaos.Mongo.Outbox.Tests (net10.0)", "1.0.0+abc1234")
        ];

        TestedDriverCheck.GetFailures(Expected, outputs).Should().Equal(
            $"Chaos.Mongo.Tests (net10.0): found '1.0.0+abc1234' instead of '{Expected}'.",
            $"Chaos.Mongo.Outbox.Tests (net10.0): found '1.0.0+abc1234' instead of '{Expected}'.");
    }

    [Test]
    public void GetFailures_WithoutDriverInAnyOutput_ReturnsFailure()
    {
        TestedDriver[] outputs = [new("BuildPipeline.Tests (net10.0)", null)];

        TestedDriverCheck.GetFailures(Expected, outputs).Should().Equal("No test output contains the driver.");
        TestedDriverCheck.GetFailures(Expected, []).Should().Equal("No test output contains the driver.");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    public void GetFailures_WithoutExpectedVersion_Throws(String? expected)
    {
        var act = () => TestedDriverCheck.GetFailures(expected!, []);

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void GetFailures_WithoutOutputs_Throws()
    {
        var act = () => TestedDriverCheck.GetFailures(Expected, null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
