using System.Xml.Linq;

namespace Architecture.Tests;

public sealed class TestDiscoveryGuardReachabilityTests
{
    [Fact]
    public void RepositoryRunSettingsTreatZeroExecutedTestsAsFailure()
    {
        var root = FindRepositoryRoot();
        var runSettingsPath = Path.Combine(root, "Repository.runsettings");
        Assert.True(File.Exists(runSettingsPath), "Repository.runsettings is missing.");

        var document = XDocument.Load(runSettingsPath, LoadOptions.PreserveWhitespace);
        var values = document
            .Descendants("TreatNoTestsAsError")
            .Select(element => element.Value.Trim())
            .ToArray();

        Assert.Equal(["true"], values, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryProjectTestRunUsesTheRepositoryRunSettings()
    {
        var props = Read("Directory.Build.props");

        Assert.Contains(
            "<RunSettingsFilePath>$(MSBuildThisFileDirectory)Repository.runsettings</RunSettingsFilePath>",
            props,
            StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(props, "<RunSettingsFilePath>"));

        var workflow = Read(".github/workflows/ci.yml");
        Assert.Contains("dotnet test AggregatorBackend.slnx", workflow, StringComparison.Ordinal);
        if (workflow.Contains("--settings", StringComparison.Ordinal))
        {
            Assert.Contains("Repository.runsettings", workflow, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryCanonicalDotNetTestProjectDeclaresTheTestSdkOwner()
    {
        var root = FindRepositoryRoot();
        var solution = XDocument.Load(
            Path.Combine(root, "AggregatorBackend.slnx"),
            LoadOptions.PreserveWhitespace);
        var projectPaths = solution
            .Descendants("Project")
            .Select(element => element.Attribute("Path")?.Value)
            .Where(path => path is not null)
            .Select(path => path!)
            .Where(path =>
                path.StartsWith("tests/", StringComparison.Ordinal) &&
                Path.GetFileNameWithoutExtension(path).EndsWith(".Tests", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(projectPaths);
        foreach (var relativePath in projectPaths)
        {
            var projectPath = Path.Combine(
                root,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            var project = XDocument.Load(projectPath, LoadOptions.PreserveWhitespace);
            Assert.Contains(
                project.Descendants("IsTestProject"),
                element => string.Equals(
                    element.Value.Trim(),
                    "true",
                    StringComparison.OrdinalIgnoreCase));
            Assert.Contains(
                project.Descendants("PackageReference"),
                element => string.Equals(
                    element.Attribute("Include")?.Value,
                    "Microsoft.NET.Test.Sdk",
                    StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ExplicitTrxGuardRunsEveryTestProjectAndRejectsEmptyResults()
    {
        var script = Read("tools/run-tests-with-discovery-guard.py");

        Assert.Contains("discover_test_projects", script, StringComparison.Ordinal);
        Assert.Contains("for project in projects:", script, StringComparison.Ordinal);
        Assert.Contains("\"test\",", script, StringComparison.Ordinal);
        Assert.Contains("\"--no-build\",", script, StringComparison.Ordinal);
        Assert.Contains("\"--no-restore\",", script, StringComparison.Ordinal);
        Assert.Contains("ResultSummary", script, StringComparison.Ordinal);
        Assert.Contains("if counters.total < 1:", script, StringComparison.Ordinal);
        Assert.Contains("if counters.executed < 1:", script, StringComparison.Ordinal);
        Assert.Contains("if counters.unsuccessful > 0:", script, StringComparison.Ordinal);
        Assert.Contains("--self-test", script, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "dotnet test AggregatorBackend.slnx",
            script,
            StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(token, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += token.Length;
        }

        return count;
    }

    private static string Read(string relativePath)
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Repository file '{relativePath}' was not found.");
        return File.ReadAllText(path);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "AggregatorBackend.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
