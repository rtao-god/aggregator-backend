using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Architecture.Tests;

public sealed partial class DeploymentTopologyRulesTests
{
    private static readonly string[] CanonicalDockerfiles =
    [
        "deploy/Dockerfile.catalog-media-worker",
        "deploy/Dockerfile.dotnet-service",
    ];

    private static readonly string[] OwnedApiRoutes =
    [
        "/api/catalog-query*",
        "/api/catalog*",
        "/api/ingestion*",
        "/api/analytics*",
        "/api/promotion*",
    ];

    [Fact]
    public void RepositoryHasOneCanonicalComposeAndTwoImageOwners()
    {
        var repository = RepositoryModel.Load();
        var composeFiles = Directory
            .EnumerateFiles(repository.Root, "compose*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".yml" or ".yaml")
            .Where(path => !HasBuildOutputSegment(repository.Relative(path)))
            .Select(repository.Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Single(composeFiles);
        Assert.Equal("compose.yaml", composeFiles[0]);

        var dockerfiles = Directory
            .EnumerateFiles(repository.Root, "Dockerfile*", SearchOption.AllDirectories)
            .Where(path => !HasBuildOutputSegment(repository.Relative(path)))
            .Select(repository.Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(CanonicalDockerfiles, dockerfiles);

        Assert.True(File.Exists(Path.Combine(repository.Root, ".env.example")));
        Assert.False(File.Exists(Path.Combine(repository.Root, "deploy", ".env.example")));
        var dockerIgnore = File.ReadAllText(Path.Combine(repository.Root, ".dockerignore"));
        Assert.True(dockerIgnore.Contains(".env\n", StringComparison.Ordinal));
        Assert.True(dockerIgnore.Contains(".env.*", StringComparison.Ordinal));
        Assert.True(dockerIgnore.Contains("!.env.example", StringComparison.Ordinal));
    }

    [Fact]
    public void ComposeBuildsEveryApprovedDeployableExactlyOnce()
    {
        var repository = RepositoryModel.Load();
        var compose = File.ReadAllText(Path.Combine(repository.Root, "compose.yaml"));
        using var topology = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repository.Root, "docs", "architecture", "project-topology.json")));

        var approved = topology.RootElement
            .GetProperty("contexts")
            .EnumerateArray()
            .SelectMany(context => context.GetProperty("production").EnumerateArray())
            .Where(row => row[2].ValueKind == JsonValueKind.String)
            .Select(row => row[0].GetString() ?? throw new InvalidDataException("Deployable project path is empty."))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var composed = ProjectPathPattern()
            .Matches(compose)
            .Cast<Match>()
            .Select(match => match.Groups["path"].Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(approved, composed);
        Assert.Equal(composed.Length, composed.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ComposeEnforcesLocalExposureIsolationAndBoundedRuntime()
    {
        var repository = RepositoryModel.Load();
        var compose = File.ReadAllText(Path.Combine(repository.Root, "compose.yaml"));
        var environment = File.ReadAllText(Path.Combine(repository.Root, ".env.example"));

        Assert.False(compose.Contains("minio", StringComparison.OrdinalIgnoreCase));
        Assert.False(environment.Contains(":latest", StringComparison.OrdinalIgnoreCase));
        Assert.False(compose.Contains("DATABASE_URL", StringComparison.Ordinal));
        Assert.False(compose.Contains("AnalyticsWorker__", StringComparison.Ordinal));
        Assert.True(compose.Contains("Analytics__Aggregation__", StringComparison.Ordinal));
        Assert.True(compose.Contains("Query__PromotionWorker__", StringComparison.Ordinal));
        Assert.True(compose.Contains("PGPASSWORD:", StringComparison.Ordinal));
        Assert.True(compose.Contains("postgres-data:/var/lib/postgresql", StringComparison.Ordinal));
        Assert.True(compose.Contains("internal: true", StringComparison.Ordinal));
        Assert.True(compose.Contains("127.0.0.1:${BACKEND_HTTP_PORT:-8080}:8080", StringComparison.Ordinal));
        Assert.Equal(1, PortsPattern().Count(compose));
        Assert.True(compose.Contains("read_only: true", StringComparison.Ordinal));
        Assert.True(compose.Contains("cap_drop: [ALL]", StringComparison.Ordinal));
        Assert.True(compose.Contains("no-new-privileges:true", StringComparison.Ordinal));
        Assert.True(compose.Contains("pids_limit:", StringComparison.Ordinal));
        Assert.True(compose.Contains("mem_limit:", StringComparison.Ordinal));
        Assert.True(compose.Contains("cpus:", StringComparison.Ordinal));
        Assert.True(compose.Contains("/health/HealthProbe.dll", StringComparison.Ordinal));
        Assert.True(compose.Contains("service_completed_successfully", StringComparison.Ordinal));
        Assert.True(compose.Contains("service_healthy", StringComparison.Ordinal));
        Assert.False(compose.Contains("catalog-media-api", StringComparison.Ordinal));
        Assert.False(compose.Contains("catalog-media-migrations", StringComparison.Ordinal));
    }

    [Fact]
    public void RepositoryCommandsUseOnlyCanonicalCompose()
    {
        var repository = RepositoryModel.Load();
        var tooling = File.ReadAllText(Path.Combine(repository.Root, "tools", "repo.ps1"));

        Assert.True(tooling.Contains("compose.yaml", StringComparison.Ordinal));
        Assert.False(tooling.Contains("compose.runtime", StringComparison.OrdinalIgnoreCase));
        Assert.False(tooling.Contains("compose.workers", StringComparison.OrdinalIgnoreCase));
        Assert.True(tooling.Contains("tools/verify-contracts.py", StringComparison.Ordinal));
        Assert.True(tooling.Contains("config', '--quiet'", StringComparison.Ordinal));
    }

    [Fact]
    public void ReverseProxyRoutesOnlyOwnedApiPrefixes()
    {
        var repository = RepositoryModel.Load();
        var caddy = File.ReadAllText(Path.Combine(repository.Root, "deploy", "Caddyfile"));
        foreach (var route in OwnedApiRoutes)
        {
            Assert.True(caddy.Contains(route, StringComparison.Ordinal));
        }

        Assert.False(caddy.Contains("catalog-event-worker", StringComparison.OrdinalIgnoreCase));
        Assert.False(caddy.Contains("/api/catalog-media", StringComparison.Ordinal));
        Assert.False(caddy.Contains("catalog-media-api", StringComparison.Ordinal));
        Assert.True(caddy.Contains("respond 404", StringComparison.Ordinal));
    }

    private static bool HasBuildOutputSegment(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "bin" or "obj" or "artifacts");

    [GeneratedRegex(@"(?m)^\s+ports:", RegexOptions.CultureInvariant)]
    private static partial Regex PortsPattern();

    [GeneratedRegex(@"PROJECT_PATH:\s+(?<path>[^,}\s]+)", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectPathPattern();
}
