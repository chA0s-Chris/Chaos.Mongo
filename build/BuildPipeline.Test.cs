// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace BuildPipeline;

using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Utilities.Collections;
using Serilog;
using System.Diagnostics;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

internal partial class BuildPipeline
{
    private Target BuildTests => target =>
        target.DependsOn(Restore)
              .Executes(() =>
              {
                  // One invocation over all test projects: per-project builds in sequence would re-evaluate the
                  // shared library projects each time, and the solution would also build the benchmarks.
                  DotNetBuild(s => s
                                   .SetProjectFile(TestsSolutionFilter)
                                   .SetConfiguration(TargetBuildConfiguration)
                                   .SetFramework(TestFramework)
                                   .SetProcessAdditionalArguments("/nowarn:CS1591;CS1998;CS4014")
                                   .When(_ => UseLatestTestedMongoDriver,
                                         t => t.AddProperty(UseLatestTestedMongoDriverProperty, "true"))
                                   .EnableNoRestore());
              });

    private Target Test => target =>
        target.DependsOn(BuildTests, RestoreTools)
              .Executes(() =>
              {
                  // A stale per-project coverage.cobertura.xml from an earlier run would otherwise be merged in
                  // and skew the gate below. The same goes for result files of an earlier run, including those
                  // under the former one-file-per-project naming, which the report would count as well.
                  CoverageDirectory.CreateOrCleanDirectory();
                  ArtifactsDirectory.GlobFiles(TestResultFiles.Pattern).ForEach(file => file.DeleteFile());

                  // One run per test project and target framework. The projects of one framework run concurrently,
                  // the frameworks one after the other, so two test hosts of one project never start their MongoDB
                  // containers at the same time: a trial that ran both frameworks of a project at once failed tests
                  // with Docker's "container ... is not running".
                  var runs = (from project in TestsDirectory.GlobFiles("**/*.Tests.csproj")
                              let projectName = Path.GetFileNameWithoutExtension(project)
                              from framework in TestFrameworkSelection.Select(projectName, GetTargetFrameworks(project), TestFramework)
                              let resultFile = ArtifactsDirectory / TestResultFiles.GetFileName(projectName, framework)
                              select (Project: project, Framework: framework, ResultFile: resultFile)).ToList();

                  if (UseLatestTestedMongoDriver)
                  {
                      // BuildPipeline.Tests has no reference to the driver; see tests/Directory.Build.props.
                      VerifyTestedDriver(runs.Where(run => Path.GetFileNameWithoutExtension(run.Project) != "BuildPipeline.Tests")
                                             .Select(run => (run.Project, run.Framework))
                                             .ToList());
                  }

                  foreach (var frameworkRuns in runs.GroupBy(run => run.Framework))
                  {
                      var batch = frameworkRuns.ToList();

                      DotNetTest(s => s
                                      .SetConfiguration(TargetBuildConfiguration)
                                      .EnableNoBuild()
                                      .EnableNoRestore()
                                      .SetDataCollector("XPlat Code Coverage")
                                      .SetSettingsFile(RootDirectory / "coverlet.xml")
                                      .SetResultsDirectory(CoverageDirectory)
                                      .When(_ => UseLatestTestedMongoDriver,
                                            t => t.AddProperty(UseLatestTestedMongoDriverProperty, "true"))
                                      .CombineWith(
                                          batch,
                                          (settings, run) => settings.SetProjectFile(run.Project)
                                                                     .SetFramework(run.Framework)
                                                                     .SetLoggers($"junit;LogFilePath={run.ResultFile}")),
                                 degreeOfParallelism: batch.Count,
                                 completeOnFailure: true);
                  }

                  DotNet($"coverage merge {CoverageDirectory}/**/coverage.cobertura.xml -f cobertura -o {MergedCoverageResultsFile}");

                  ReportTestCountAndCoverage(runs.Select(run => run.ResultFile).ToList());
              });

    private static String GetMsBuildProperty(AbsolutePath project, String property)
        => JoinStandardOutput(DotNet($"msbuild \"{project}\" -nologo -getProperty:{property}", logOutput: false, logInvocation: false));

    /// <summary>
    /// Reads the target frameworks of a project from MSBuild, so the test runs follow the project files rather than a
    /// second list of frameworks.
    /// </summary>
    private static IReadOnlyList<String> GetTargetFrameworks(AbsolutePath project)
    {
        var frameworks = TestResultFiles.ParseFrameworks(GetMsBuildProperty(project, "TargetFrameworks"));

        if (frameworks.Count == 0)
            Assert.Fail($"{project} does not declare TargetFrameworks, so no test run can be derived from it.");

        return frameworks;
    }

    private static String JoinStandardOutput(IEnumerable<Output> output)
        => String.Join(String.Empty, output.Where(o => o.Type == OutputType.Std).Select(o => o.Text)).Trim();

    /// <summary>
    /// Reads the directory the test host runs from, with the same properties as the test invocation, so that a changed
    /// output path is followed.
    /// </summary>
    private AbsolutePath GetTestOutputDirectory(AbsolutePath project, String framework)
    {
        var properties = $"-property:Configuration={TargetBuildConfiguration} " +
                         $"-property:TargetFramework={framework} " +
                         $"-property:{UseLatestTestedMongoDriverProperty}=true";

        return JoinStandardOutput(DotNet($"msbuild \"{project}\" -nologo -getProperty:TargetDir {properties:nq}", logOutput: false, logInvocation: false));
    }

    private void ReportTestCountAndCoverage(IReadOnlyCollection<AbsolutePath> expectedResultFiles)
    {
        var totalTestCount = 0;
        var totalFailureCount = 0;

        var xmlReaderSettings = new XmlReaderSettings
        {
            IgnoreComments = true,
            IgnoreWhitespace = true
        };

        // The expected files, not a glob, decide what is counted: a run that wrote no result must fail the report
        // instead of silently lowering the total.
        foreach (var testResult in expectedResultFiles)
        {
            if (!testResult.FileExists())
                throw new FileNotFoundException($"Missing test result file: {testResult}", testResult);

            using var reader = XmlReader.Create(testResult, xmlReaderSettings);
            reader.Read();
            reader.Read();

            if (reader is not { NodeType: XmlNodeType.Element, Name: "testsuites" } ||
                !Int32.TryParse(reader.GetAttribute("tests"), out var testCount) ||
                !Int32.TryParse(reader.GetAttribute("failures"), out var failureCount))
            {
                throw new InvalidDataException($"Malformed test result file: {testResult}");
            }

            totalTestCount += testCount;
            totalFailureCount += failureCount;
        }

        var coverageDocument = XDocument.Load(MergedCoverageResultsFile);
        var coverageRoot = coverageDocument.Root;

        if (coverageRoot is not { Name.LocalName: "coverage" } ||
            !Double.TryParse((String?)coverageRoot.Attribute("line-rate"), NumberStyles.Number, CultureInfo.InvariantCulture, out var lineRate))
        {
            throw new InvalidDataException($"Malformed test coverage result file: {MergedCoverageResultsFile}");
        }

        String RequireAttribute(XElement element, String attributeName)
            => (String?)element.Attribute(attributeName) ??
               throw new InvalidDataException($"Malformed test coverage result file: {MergedCoverageResultsFile}");

        var packageLineRates = coverageRoot.Descendants("package")
                                           .ToDictionary(
                                               p => RequireAttribute(p, "name"),
                                               p => Double.Parse(RequireAttribute(p, "line-rate"), NumberStyles.Number, CultureInfo.InvariantCulture));

        var coverage = (lineRate * 100).ToString("N2", CultureInfo.InvariantCulture);
        Console.WriteLine($"CODE_COVERAGE={coverage}");

        ReportSummary(c =>
        {
            c.AddPair("Total", totalTestCount)
             .AddPair("Failed", totalFailureCount)
             .AddPair("Coverage", $"{coverage}%");

            foreach (var (package, rate) in packageLineRates)
                c.AddPair($"Coverage ({package})", $"{(rate * 100):N2}%");

            return c;
        });

        // Only the version the issue designates as primary enforces the gate; MinimumSupported and Latest still
        // compute and print the numbers above so their legs stay informative without failing on MongoDB-version
        // coverage differences.
        var mongoImage = Environment.GetEnvironmentVariable("CHAOSMONGO_MONGODB_IMAGE");

        if (CoverageThresholds.ShouldEnforceGate(mongoImage))
        {
            var violations = CoverageThresholds.GetViolations(lineRate, packageLineRates);

            if (violations.Count > 0)
                Assert.Fail(String.Join(Environment.NewLine, violations));
        }
    }

    /// <summary>
    /// Fails unless the test outputs contain the pinned latest tested <c>MongoDB.Driver</c>. Reads the assemblies the test
    /// hosts load rather than the restore result, which another process can change without affecting the run.
    /// </summary>
    private void VerifyTestedDriver(IReadOnlyCollection<(AbsolutePath Project, String Framework)> runs)
    {
        var expectedVersion = GetMsBuildProperty(RootDirectory / "Directory.Packages.props", "LatestTestedMongoDriverVersion");

        if (String.IsNullOrEmpty(expectedVersion))
            Assert.Fail("Directory.Packages.props does not declare LatestTestedMongoDriverVersion.");

        var outputs = runs.Select(run =>
        {
            var driver = GetTestOutputDirectory(run.Project, run.Framework) / "MongoDB.Driver.dll";
            var version = driver.FileExists() ? FileVersionInfo.GetVersionInfo(driver).ProductVersion ?? String.Empty : null;

            return new TestedDriver($"{Path.GetFileNameWithoutExtension(run.Project)} ({run.Framework})", version);
        }).ToList();

        var failures = TestedDriverCheck.GetFailures(expectedVersion, outputs);

        if (failures.Count > 0)
        {
            Assert.Fail($"The tests would not run against MongoDB.Driver {expectedVersion}." +
                        Environment.NewLine + String.Join(Environment.NewLine, failures));
        }

        Log.Information("Testing against MongoDB.Driver {Version}", expectedVersion);
    }
}
