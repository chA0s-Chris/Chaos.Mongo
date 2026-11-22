// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace BuildPipeline;

using Fallout.Common;
using Fallout.Common.IO;
using JetBrains.Annotations;
using Serilog;

internal partial class BuildPipeline
{
    /// <summary>
    /// Folds every src/ project's PublicAPI.Unshipped.txt into its PublicAPI.Shipped.txt. Run it before a release that
    /// ships new API; RELEASING.md says when. Every project is folded in memory first, so invalid API files in one project
    /// leave all files untouched.
    /// </summary>
    [UsedImplicitly]
    private Target FoldPublicApi => target =>
        target.Executes(() =>
        {
            var folds = (from shippedFile in SourceDirectory.GlobFiles("**/PublicAPI.Shipped.txt")
                         let projectDirectory = shippedFile.Parent
                         let unshippedFile = projectDirectory / "PublicAPI.Unshipped.txt"
                         let result = PublicApiFold.Fold(projectDirectory.Name, File.ReadAllText(shippedFile), File.ReadAllText(unshippedFile))
                         select (Project: projectDirectory.Name, ShippedFile: shippedFile, UnshippedFile: unshippedFile, Result: result)).ToList();

            Assert.NotEmpty(folds, $"No PublicAPI.Shipped.txt found under {SourceDirectory}.");

            // File.WriteAllText writes UTF-8 without a byte order mark and the content as is, matching the existing files.
            foreach (var fold in folds)
            {
                File.WriteAllText(fold.ShippedFile, fold.Result.Shipped);
                File.WriteAllText(fold.UnshippedFile, fold.Result.Unshipped);
                Log.Information("Folded the public API of {Project}", fold.Project);
            }
        });
}
