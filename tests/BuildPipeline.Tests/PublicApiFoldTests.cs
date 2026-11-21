// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace BuildPipeline.Tests;

using FluentAssertions;
using NUnit.Framework;

public class PublicApiFoldTests
{
    private const String Project = "Chaos.Mongo";

    [Test]
    public void Fold_WithAdditionsAndRemovals_ShipsAdditionsAndDropsRemovedEntries()
    {
        const String shipped = "#nullable enable\nB.Kept() -> void\nA.Removed() -> void\n";
        const String unshipped = "#nullable enable\n\n*REMOVED*A.Removed() -> void\nC.Added() -> void\nA.Added() -> void\n";

        var result = PublicApiFold.Fold(Project, shipped, unshipped);

        result.Shipped.Should().Be("#nullable enable\nA.Added() -> void\nB.Kept() -> void\nC.Added() -> void\n");
        result.Unshipped.Should().Be("#nullable enable\n");
    }

    [Test]
    public void Fold_WithEmptyUnshippedFile_SortsShippedEntriesOrdinally()
    {
        const String shipped = "#nullable enable\nb.Lower() -> void\nB.Upper() -> void\nA.Upper() -> void\n";

        var result = PublicApiFold.Fold(Project, shipped, "#nullable enable\n");

        result.Shipped.Should().Be("#nullable enable\nA.Upper() -> void\nB.Upper() -> void\nb.Lower() -> void\n");
        result.Unshipped.Should().Be("#nullable enable\n");
    }

    [Test]
    public void Fold_WithCrLfLineEndings_WritesLfLineEndings()
    {
        const String shipped = "#nullable enable\r\nB.Kept() -> void\r\n";
        const String unshipped = "#nullable enable\r\nA.Added() -> void\r\n";

        var result = PublicApiFold.Fold(Project, shipped, unshipped);

        result.Shipped.Should().Be("#nullable enable\nA.Added() -> void\nB.Kept() -> void\n");
    }

    [Test]
    public void Fold_WithFoldedResult_IsIdempotent()
    {
        var first = PublicApiFold.Fold(Project, "#nullable enable\nB.Kept() -> void\n", "#nullable enable\nA.Added() -> void\n");

        var second = PublicApiFold.Fold(Project, first.Shipped, first.Unshipped);

        second.Should().Be(first);
    }

    [Test]
    public void Fold_WithRemovedEntryNotShipped_ThrowsNamingProjectAndEveryMissingEntry()
    {
        const String shipped = "#nullable enable\nA.Kept() -> void\n";
        const String unshipped = "#nullable enable\n*REMOVED*B.Missing() -> void\n*REMOVED*C.Missing() -> void\n*REMOVED*A.Kept() -> void\n";

        var act = () => PublicApiFold.Fold(Project, shipped, unshipped);

        act.Should().Throw<InvalidOperationException>()
           .Which.Message.Should().Be("Chaos.Mongo: PublicAPI.Unshipped.txt removes entries that are not shipped:\n" +
                                      "  B.Missing() -> void\n" +
                                      "  C.Missing() -> void");
    }

    [Test]
    public void Fold_WithAddedEntryAlreadyShipped_Throws()
    {
        const String shipped = "#nullable enable\nA.Kept() -> void\n";
        const String unshipped = "#nullable enable\nA.Kept() -> void\n";

        var act = () => PublicApiFold.Fold(Project, shipped, unshipped);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("Chaos.Mongo: PublicAPI.Unshipped.txt adds entries that are already shipped:*A.Kept() -> void");
    }

    [Test]
    public void Fold_WithEntryListedTwice_FoldsItOnce()
    {
        const String shipped = "#nullable enable\nA.Removed() -> void\nB.Kept() -> void\n";
        const String unshipped = "#nullable enable\n*REMOVED*A.Removed() -> void\nC.Added() -> void\n" +
                                 "*REMOVED*A.Removed() -> void\nC.Added() -> void\n";

        var result = PublicApiFold.Fold(Project, shipped, unshipped);

        result.Shipped.Should().Be("#nullable enable\nB.Kept() -> void\nC.Added() -> void\n");
    }

    [Test]
    public void Fold_WithEntryRemovedAndAddedAgain_KeepsIt()
    {
        const String shipped = "#nullable enable\nA.Changed() -> void\n";
        const String unshipped = "#nullable enable\n*REMOVED*A.Changed() -> void\nA.Changed() -> void\n";

        var result = PublicApiFold.Fold(Project, shipped, unshipped);

        result.Shipped.Should().Be("#nullable enable\nA.Changed() -> void\n");
    }

    [TestCase("A.Kept() -> void\n", "#nullable enable\n", "PublicAPI.Shipped.txt")]
    [TestCase("#nullable enable\n", "", "PublicAPI.Unshipped.txt")]
    [TestCase("#nullable enable\n", "A.Added() -> void\n#nullable enable\n", "PublicAPI.Unshipped.txt")]
    public void Fold_WithoutHeader_ThrowsNamingTheFile(String shipped, String unshipped, String file)
    {
        var act = () => PublicApiFold.Fold(Project, shipped, unshipped);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage($"Chaos.Mongo: {file} does not start with \"#nullable enable\".");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    public void Fold_WithoutProjectName_Throws(String? project)
    {
        var act = () => PublicApiFold.Fold(project!, "#nullable enable\n", "#nullable enable\n");

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Fold_WithoutContent_Throws()
    {
        var withoutShipped = () => PublicApiFold.Fold(Project, null!, "#nullable enable\n");
        var withoutUnshipped = () => PublicApiFold.Fold(Project, "#nullable enable\n", null!);

        withoutShipped.Should().Throw<ArgumentNullException>();
        withoutUnshipped.Should().Throw<ArgumentNullException>();
    }
}
