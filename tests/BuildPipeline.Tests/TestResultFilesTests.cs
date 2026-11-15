// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace BuildPipeline.Tests;

using FluentAssertions;
using NUnit.Framework;
using System.IO.Enumeration;

public class TestResultFilesTests
{
    [Test]
    public void GetFileName_ForDifferentFrameworksOfOneProject_ReturnsDifferentNames()
    {
        var net10 = TestResultFiles.GetFileName("Chaos.Mongo.Tests", "net10.0");
        var net11 = TestResultFiles.GetFileName("Chaos.Mongo.Tests", "net11.0");

        net10.Should().NotBe(net11);
    }

    [Test]
    public void GetFileName_ForDifferentProjectsOnOneFramework_ReturnsDifferentNames()
    {
        var core = TestResultFiles.GetFileName("Chaos.Mongo.Tests", "net10.0");
        var outbox = TestResultFiles.GetFileName("Chaos.Mongo.Outbox.Tests", "net10.0");

        core.Should().NotBe(outbox);
    }

    [TestCase("Chaos.Mongo.Tests", "net10.0", "Chaos.Mongo.net10.0.Tests.xml")]
    [TestCase("Chaos.Mongo.Outbox.Tests", "net11.0", "Chaos.Mongo.Outbox.net11.0.Tests.xml")]
    [TestCase("BuildPipeline.Tests", "net10.0", "BuildPipeline.net10.0.Tests.xml")]
    [TestCase("Standalone", "net10.0", "Standalone.net10.0.Tests.xml")]
    [TestCase(".Tests", "net10.0", ".Tests.net10.0.Tests.xml")]
    public void GetFileName_ReturnsProjectFrameworkAndSuffix(String project, String framework, String expected)
    {
        TestResultFiles.GetFileName(project, framework).Should().Be(expected);
    }

    [TestCase("Chaos.Mongo.Tests", "net10.0")]
    [TestCase("Chaos.Mongo.EventStore.Tests", "net11.0")]
    [TestCase("Standalone", "net10.0")]
    public void GetFileName_AlwaysMatchesThePatternTheReportReads(String project, String framework)
    {
        var fileName = TestResultFiles.GetFileName(project, framework);

        FileSystemName.MatchesSimpleExpression(TestResultFiles.Pattern, fileName).Should().BeTrue();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    public void GetFileName_WithoutProjectName_Throws(String? project)
    {
        var act = () => TestResultFiles.GetFileName(project!, "net10.0");

        act.Should().Throw<ArgumentException>();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    public void GetFileName_WithoutFramework_Throws(String? framework)
    {
        var act = () => TestResultFiles.GetFileName("Chaos.Mongo.Tests", framework!);

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void ParseFrameworks_WithSeveralFrameworks_ReturnsEachOne()
    {
        TestResultFiles.ParseFrameworks("net10.0;net11.0").Should().Equal("net10.0", "net11.0");
    }

    [Test]
    public void ParseFrameworks_WithWhitespaceAndEmptyEntries_ReturnsOnlyFrameworks()
    {
        TestResultFiles.ParseFrameworks(" net10.0 ;; net11.0;\n").Should().Equal("net10.0", "net11.0");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("  ")]
    public void ParseFrameworks_WithoutValue_ReturnsEmpty(String? value)
    {
        TestResultFiles.ParseFrameworks(value).Should().BeEmpty();
    }
}
