// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace BuildPipeline;

using JetBrains.Annotations;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

internal partial class BuildPipeline
{
    [Parameter("NuGet API key", Name = "NUGET_APIKEY")]
    public String NuGetApiKey { get; init; } = String.Empty;

    [Parameter("URI of the NuGet feed to publish to", Name = "NUGET_FEED_URI")]
    public String NuGetFeedUri { get; init; } = String.Empty;

    private static Dictionary<Char, String> EscapeCharacters { get; } = new()
    {
        ['%'] = "%25",
        ['$'] = "%24",
        ['@'] = "%40",
        ['\''] = "%27",
        ['('] = "%28",
        [')'] = "%29",
        [';'] = "%3B",
        ['?'] = "%3F",
        ['*'] = "%2A",
        [','] = "%2C"
    };

    private Target Pack => target =>
        target.DependsOn(Restore)
              .Executes(() =>
              {
                  var packageCount = 0;

                  foreach (var project in SourceDirectory.GlobFiles("**/*.csproj"))
                  {
                      DotNetPack(x => x.SetConfiguration(TargetBuildConfiguration)
                                       .SetProject(project)
                                       .SetOutputDirectory(PublishDirectory)
                                       .DisablePackageRequireLicenseAcceptance()
                                       .EnableContinuousIntegrationBuild()
                                       .AddProperty("AdditionalConstants", "NUGET_RELEASE")
                                       .AddProperty("SignAssembly", "true")
                                       .AddProperty("AssemblyOriginatorKeyFile", "../../Chaos.Mongo.snk")
                                       .EnableIncludeSymbols()
                                       .SetSymbolPackageFormat(DotNetSymbolPackageFormat.snupkg)
                                       .SetVersion(SemanticVersion)
                                       .When(!String.IsNullOrEmpty(ReleaseNotes),
                                             t => t.SetPackageReleaseNotes(EscapeStringForMsBuild(ReleaseNotes))));

                      packageCount++;
                  }

                  ReportSummary(c => c.AddPair("Packages", packageCount)
                                      .AddPair("Version", SemanticVersion));
              });

    [UsedImplicitly]
    private Target Publish => target =>
        target.DependsOn(Pack)
              .OnlyWhenDynamic(() => !NuGetApiKey.IsNullOrWhiteSpace() && !NuGetFeedUri.IsNullOrWhiteSpace())
              .Executes(() =>
              {
                  foreach (var package in PublishDirectory.GlobFiles("*.nupkg"))
                  {
                      DotNetNuGetPush(c => c.SetTargetPath(package)
                                            .SetSource(NuGetFeedUri)
                                            .SetApiKey(NuGetApiKey));
                  }
              });

    private static String EscapeStringForMsBuild(String text)
        => String.Concat(text.Select(c => EscapeCharacters.TryGetValue(c, out var replacement) ? replacement : c.ToString()));
}
