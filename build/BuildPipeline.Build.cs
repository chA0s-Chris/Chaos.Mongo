// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace BuildPipeline;

using Fallout.Common;
using Fallout.Common.CI.GitLab;
using Fallout.Common.IO;
using Fallout.Common.Tooling;
using Fallout.Common.Tools.DotNet;
using Fallout.Common.Utilities.Collections;
using static Fallout.Common.Tools.DotNet.DotNetTasks;

internal partial class BuildPipeline
{
    private Target Build => target =>
        target.DependsOn(Restore)
              .After(Clean, RestoreTools)
              .Before(Test)
              .Requires(() => !UseLatestTestedMongoDriver)
              .Executes(() =>
              {
                  DotNetBuild(s => s
                                   .SetProjectFile(Solution)
                                   .SetConfiguration(TargetBuildConfiguration)
                                   .SetProcessAdditionalArguments("/nowarn:CS1591;CS1998;CS4014")
                                   .EnableNoRestore()
                                   .SetAssemblyVersion(AssemblyVersion)
                                   .SetFileVersion(AssemblyVersion)
                                   .SetInformationalVersion(SemanticVersion)
                                   .EnableContinuousIntegrationBuild());

                  ReportSummary(c => c.AddPair("Version", SemanticVersion));
              });

    private Target Restore => target =>
        target.After(Clean)
              .Executes(() =>
              {
                  var settings = new DotNetRestoreSettings().SetProjectFile(Solution);

                  if (UseLatestTestedMongoDriver)
                      settings = settings.AddProperty(UseLatestTestedMongoDriverProperty, "true");

                  if (GitLab.Instance?.Ci == true)
                      settings = settings.SetPackageDirectory(PackagesDirectory);

                  DotNetRestore(settings);
              });

    private AbsolutePath SourceDirectory => RootDirectory / "src";
}
