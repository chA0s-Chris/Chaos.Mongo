// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace BuildPipeline.Tests;

using FluentAssertions;
using NUnit.Framework;

public class TestFrameworkSelectionTests
{
    private static readonly String[] Declared = ["net10.0", "net11.0"];

    [TestCase(null)]
    [TestCase("")]
    [TestCase("  ")]
    public void Select_WithoutRequestedFramework_ReturnsEveryDeclaredFramework(String? requested)
    {
        TestFrameworkSelection.Select("Chaos.Mongo.Tests", Declared, requested).Should().Equal("net10.0", "net11.0");
    }

    [TestCase("net10.0")]
    [TestCase("net11.0")]
    [TestCase(" net11.0 ")]
    public void Select_WithDeclaredFramework_ReturnsOnlyThatFramework(String requested)
    {
        TestFrameworkSelection.Select("Chaos.Mongo.Tests", Declared, requested).Should().Equal(requested.Trim());
    }

    [TestCase("net9.0")]
    [TestCase("NET10.0")]
    [TestCase("net10")]
    public void Select_WithUndeclaredFramework_ThrowsNamingProjectAndFramework(String requested)
    {
        var act = () => TestFrameworkSelection.Select("Chaos.Mongo.Tests", Declared, requested);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage($"*Chaos.Mongo.Tests*{requested}*net10.0, net11.0*");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    public void Select_WithoutProjectName_Throws(String? project)
    {
        var act = () => TestFrameworkSelection.Select(project!, Declared, null);

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Select_WithoutDeclaredFrameworks_Throws()
    {
        var act = () => TestFrameworkSelection.Select("Chaos.Mongo.Tests", null!, null);

        act.Should().Throw<ArgumentNullException>();
    }
}
