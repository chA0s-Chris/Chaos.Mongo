// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace BuildPipeline;

using Fallout.Common;
using Fallout.Common.IO;
using Fallout.Common.Tooling;
using Fallout.Common.Tools.DotNet;
using Fallout.Solutions;

internal partial class BuildPipeline
{
    /// <summary>
    /// The MSBuild property that makes Directory.Packages.props resolve <c>LatestTestedMongoDriverVersion</c>.
    /// </summary>
    private const String UseLatestTestedMongoDriverProperty = "UseLatestTestedMongoDriver";

    [Parameter]
    public String ReleaseVersion { get; set; } = "0.1.0-dev";

    private static AbsolutePath ReleaseNotesFile => RootDirectory / "ReleaseNotes.md";

    private AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    private String AssemblyVersion { get; set; } = String.Empty;

    private Target Clean => target =>
        target.Before(Restore, RestoreTools, Build, /*Pack,*/ BuildTests, Test)
              .Executes(() =>
              {
                  DotNetTasks.DotNetClean(x => x.SetConfiguration(TargetBuildConfiguration)
                                                .EnableContinuousIntegrationBuild()
                                                .DisableProcessOutputLogging());

                  ArtifactsDirectory.CreateOrCleanDirectory();
                  PublishDirectory.CreateOrCleanDirectory();
              });

    private AbsolutePath CoverageDirectory => ArtifactsDirectory / "test-coverage";

    private AbsolutePath MergedCoverageResultsFile => CoverageDirectory / "coverage.cobertura.merged.xml";

    [Parameter(Name = "NUGET_PACKAGES_DIRECTORY")]
    private AbsolutePath PackagesDirectory { get; set; } = RootDirectory / ".nuget";

    private AbsolutePath PublishDirectory => ArtifactsDirectory / "packages";

    private String ReleaseNotes { get; set; } = String.Empty;

    private String SemanticVersion { get; set; } = String.Empty;

    [Solution]
    private Solution Solution { get; set; } = null!;

    [Parameter]
    private String TargetBuildConfiguration { get; set; } = "Release";

    /// <summary>
    /// Restricts BuildTests and Test to one target framework, for example <c>net10.0</c>. The CI matrix runs one
    /// framework per job; without it, both targets cover every framework the test projects declare.
    /// </summary>
    [Parameter("Restricts BuildTests and Test to one target framework; all declared frameworks when omitted")]
    private String? TestFramework { get; set; }

    private AbsolutePath TestsDirectory => RootDirectory / "tests";

    private AbsolutePath TestsSolutionFilter => TestsDirectory / "Tests.slnf";

    /// <summary>
    /// Restores, builds and tests against the <c>LatestTestedMongoDriverVersion</c> pinned in Directory.Packages.props
    /// instead of the packages' minimum <c>MongoDB.Driver</c>. Only for the test targets, which then compile the src/
    /// projects against the newer driver as well. Build, Pack and PackCheck refuse to run with it, so no package depends
    /// on a driver newer than the declared minimum.
    /// </summary>
    [Parameter("Restores, builds and tests against the latest tested MongoDB.Driver instead of the minimum")]
    private Boolean UseLatestTestedMongoDriver { get; set; }
}
