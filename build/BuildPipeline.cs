// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace BuildPipeline;

using Nuke.Common;
using Nuke.Common.CI.GitLab;
using Nuke.Common.IO;
using Serilog;

internal partial class BuildPipeline : NukeBuild
{
    public static Int32 Main() => Execute<BuildPipeline>(x => x.Build);

    protected override void OnBuildCreated()
    {
        if (!SemanticVersioning.Version.TryParse(ReleaseVersion, out var version))
            Assert.Fail($"Not a valid semantic version: {ReleaseVersion}");

        SemanticVersion = version.ToString();
        AssemblyVersion = $"{version.Major}.{version.Minor}.{version.Patch}.0";

        if (ReleaseNotesFile.FileExists())
        {
            ReleaseNotes = ReleaseNotesFile.ReadAllText();
        }
    }

    protected override void OnBuildInitialized()
    {
        Log.Information("Configuration   : {Configuration}", TargetBuildConfiguration);
        Log.Information("Version         : {SemVer}", SemanticVersion);
        Log.Information("Assembly version: {SemVer}", AssemblyVersion);

        if (GitLab.Instance?.Ci == true)
        {
            Log.Information("GitLab Runner   : {RunnerDescription}", GitLab.Instance.RunnerDescription);
            Log.Information("Building Tag    : {CommitTag}", GitLab.Instance.CommitTag);
        }
    }
}
