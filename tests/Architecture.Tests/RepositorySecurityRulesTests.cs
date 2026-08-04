using System.Text.RegularExpressions;
using Xunit;

namespace Architecture.Tests;

public sealed partial class RepositorySecurityRulesTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);

    [Fact]
    public void MutatingEndpointsRequireExplicitAccessDeclaration()
    {
        var failures = new List<string>();
        foreach (var file in EnumerateSourceFiles("src", "*.Controller.cs", "*Controller.cs"))
        {
            failures.AddRange(FindMutatingEndpointsWithoutAccessDeclaration(
                File.ReadAllText(file),
                Relative(file)));
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void AccessScannerAcceptsExplicitPolicyAndAnonymousContracts()
    {
        const string source = """
        [ApiController]
        public sealed class PublicController : ControllerBase
        {
            [HttpPost]
            [AllowAnonymous]
            public void Submit() { }
        }

        [ApiController]
        [Authorize("owner.policy")]
        public sealed class ProtectedController : ControllerBase
        {
            [HttpPost]
            public void Create() { }
        }

        [ApiController]
        public sealed class MethodProtectedController : ControllerBase
        {
            [Authorize(Policy = "owner.policy")]
            [HttpPost]
            public void Update() { }
        }
        """;

        Assert.Empty(FindMutatingEndpointsWithoutAccessDeclaration(source, "fixture.cs"));
    }

    [Fact]
    public void AccessScannerDoesNotBorrowAuthorizationFromAnotherController()
    {
        const string source = """
        [ApiController]
        [Authorize("owner.policy")]
        public sealed class ProtectedController : ControllerBase
        {
            [HttpPost]
            public void Create() { }
        }

        [ApiController]
        public sealed class UnprotectedController : ControllerBase
        {
            [HttpPost]
            public void Create() { }
        }
        """;

        var failure = Assert.Single(
            FindMutatingEndpointsWithoutAccessDeclaration(source, "fixture.cs"));
        Assert.Contains("UnprotectedController", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void AccessScannerRejectsBareAuthorizeAttribute()
    {
        const string source = """
        [ApiController]
        [Authorize]
        public sealed class BareAuthorizeController : ControllerBase
        {
            [HttpPost]
            public void Create() { }
        }
        """;

        Assert.Single(FindMutatingEndpointsWithoutAccessDeclaration(source, "fixture.cs"));
    }


    [Fact]
    public void ApiAndWorkerStartupNeverOwnBusinessMigrations()
    {
        var forbidden = new[]
        {
            ".Database.Migrate(",
            ".Database.MigrateAsync(",
            ".Database.EnsureCreated(",
            ".Database.EnsureCreatedAsync(",
            "RelationalMigrationRunner.Run",
            "RelationalMigrationRunner.RunAsync",
        };
        var failures = new List<string>();
        foreach (var file in EnumerateSourceFiles("src", "*.cs"))
        {
            var relative = Relative(file);
            if (!relative.Contains(".Api/", StringComparison.Ordinal) &&
                !relative.Contains(".Worker/", StringComparison.Ordinal))
            {
                continue;
            }

            var source = File.ReadAllText(file);
            foreach (var token in forbidden)
            {
                if (source.Contains(token, StringComparison.Ordinal))
                {
                    failures.Add($"{relative} invokes business migration token '{token}'.");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void RepositoryDoesNotCommitCredentialValues()
    {
        var failures = new List<string>();
        foreach (var file in Directory.EnumerateFiles(RepositoryRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Relative(file);
            if (IsIgnoredPath(relative) || !IsTextConfiguration(relative))
            {
                continue;
            }

            var source = File.ReadAllText(file);
            foreach (Match match in CredentialAssignmentRegex().Matches(source))
            {
                var value = match.Groups["value"].Value.Trim().Trim('"', '\'', ',', ';');
                if (IsSafeCredentialPlaceholder(value))
                {
                    continue;
                }

                failures.Add($"{relative} appears to commit a credential value for '{match.Groups["name"].Value}'.");
            }

            foreach (Match match in PrivateKeyRegex().Matches(source))
            {
                failures.Add($"{relative} contains a private-key material marker at offset {match.Index}.");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void CrossContextProjectReferencesTargetOnlyProducerContracts()
    {
        var contextNames = new HashSet<string>(
            ["Catalog", "Query", "Ingestion", "Analytics", "Promotion"],
            StringComparer.Ordinal);
        var failures = new List<string>();
        foreach (var project in Directory.EnumerateFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var relativeProject = Relative(project);
            var owner = ContextFromPath(relativeProject, contextNames);
            if (owner is null)
            {
                continue;
            }

            var document = System.Xml.Linq.XDocument.Load(project);
            foreach (var reference in document.Descendants("ProjectReference"))
            {
                var include = reference.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include))
                {
                    continue;
                }

                var targetPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, include));
                var targetRelative = Relative(targetPath);
                var targetOwner = ContextFromPath(targetRelative, contextNames);
                if (targetOwner is null || string.Equals(owner, targetOwner, StringComparison.Ordinal))
                {
                    continue;
                }

                var targetProjectName = Path.GetFileNameWithoutExtension(targetPath);
                if (!targetProjectName.EndsWith(".Contracts", StringComparison.Ordinal))
                {
                    failures.Add(
                        $"{relativeProject} directly references cross-context implementation {targetRelative}; only producer Contracts are allowed.");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void ProductionConfigurationDoesNotEnablePermissiveCors()
    {
        var failures = new List<string>();
        foreach (var file in EnumerateSourceFiles("src", "*.cs"))
        {
            var relative = Relative(file);
            if (!relative.Contains(".Api/", StringComparison.Ordinal))
            {
                continue;
            }

            var source = File.ReadAllText(file);
            if (source.Contains("AllowAnyOrigin()", StringComparison.Ordinal) ||
                source.Contains("SetIsOriginAllowed(_ => true)", StringComparison.Ordinal) ||
                source.Contains("SetIsOriginAllowed(origin => true)", StringComparison.Ordinal))
            {
                failures.Add($"{relative} enables permissive cross-origin access.");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static List<string> FindMutatingEndpointsWithoutAccessDeclaration(
    string source,
    string sourceIdentity)
    {
        if (!source.Contains("[ApiController]", StringComparison.Ordinal) ||
            !source.Contains("ControllerBase", StringComparison.Ordinal))
        {
            return [];
        }

        var failures = new List<string>();
        var controllerDeclarations = ControllerDeclarationRegex().Matches(source);
        foreach (Match endpoint in EndpointDeclarationRegex().Matches(source))
        {
            var attributes = endpoint.Groups["attributes"].Value;
            if (!MutationAttributeRegex().IsMatch(attributes))
            {
                continue;
            }

            Match? controllerDeclaration = null;
            foreach (Match candidate in controllerDeclarations)
            {
                if (candidate.Index >= endpoint.Index)
                {
                    break;
                }

                controllerDeclaration = candidate;
            }

            if (controllerDeclaration is null)
            {
                failures.Add(
                    $"{sourceIdentity} contains a mutating endpoint outside an explicit controller declaration near offset {endpoint.Index}.");
                continue;
            }

            if (!HasExplicitAccessDeclarationBeforeController(source, controllerDeclaration) &&
                !HasExplicitAccessDeclaration(attributes))
            {
                failures.Add(
                    $"{sourceIdentity} controller '{controllerDeclaration.Value.Trim()}' contains a mutating endpoint without an explicit [Authorize(...)] or [AllowAnonymous] declaration near offset {endpoint.Index}.");
            }
        }

        return failures;
    }

    private static bool HasExplicitAccessDeclarationBeforeController(
        string source,
        Match declaration)
    {
        var prefixStart = Math.Max(0, declaration.Index - 2000);
        var prefix = source[prefixStart..declaration.Index];
        var apiControllerIndex = prefix.LastIndexOf(
            "[ApiController]",
            StringComparison.Ordinal);
        return apiControllerIndex >= 0 &&
            HasExplicitAccessDeclaration(prefix[apiControllerIndex..]);
    }

    private static bool HasExplicitAccessDeclaration(string source) =>
        AuthorizationAttributeRegex().IsMatch(source) ||
        AllowAnonymousAttributeRegex().IsMatch(source);


    private static IEnumerable<string> EnumerateSourceFiles(
        string relativeRoot,
        params string[] patterns)
    {
        var root = Path.Combine(RepositoryRoot, relativeRoot);
        if (!Directory.Exists(root))
        {
            return [];
        }

        return patterns
            .SelectMany(pattern => Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal);
    }

    private static string? ContextFromPath(string relativePath, IReadOnlySet<string> contexts)
    {
        var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.FirstOrDefault(contexts.Contains);
    }

    private static bool IsIgnoredPath(string relativePath) =>
        relativePath.StartsWith(".git/", StringComparison.Ordinal) ||
        relativePath.Contains("/bin/", StringComparison.Ordinal) ||
        relativePath.Contains("/obj/", StringComparison.Ordinal) ||
        relativePath.Contains("/TestResults/", StringComparison.Ordinal) ||
        relativePath.StartsWith("artifacts/", StringComparison.Ordinal);

    private static bool IsTextConfiguration(string relativePath)
    {
        var extension = Path.GetExtension(relativePath);
        return extension is ".json" or ".yml" or ".yaml" or ".xml" or ".props" or ".targets" or ".cs" or ".md" or ".env" or ".sql";
    }

    private static bool IsSafeCredentialPlaceholder(string value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.Contains("${", StringComparison.Ordinal) ||
        value.Contains("{{", StringComparison.Ordinal) ||
        value.Contains('<') ||
        value.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("REQUIRED", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("test", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("example", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("Environment.GetEnvironmentVariable", StringComparison.Ordinal) ||
        value.StartsWith("configuration[", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("RequireSetting(", StringComparison.Ordinal) ||
        value.Contains("GetConnectionString(", StringComparison.Ordinal) ||
        value.Contains("GetEnvironmentVariable(", StringComparison.Ordinal) ||
        value.Contains("GetRequiredSection(", StringComparison.Ordinal) ||
        value.Contains("GetValue<", StringComparison.Ordinal) ||
        value.Contains("nameof(", StringComparison.Ordinal) ||
        value.Contains("Options.", StringComparison.Ordinal) ||
        value.Contains("options.", StringComparison.Ordinal) ||
        value.Contains("configuration", StringComparison.OrdinalIgnoreCase);

    private static string Relative(string path) =>
        Path.GetRelativePath(RepositoryRoot, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string FindRepositoryRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AggregatorBackend.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("AggregatorBackend.slnx was not found above the test output directory.");
    }

    [GeneratedRegex(@"\[(?:HttpPost|HttpPut|HttpPatch|HttpDelete)(?:Attribute)?(?:\([^\]]*\))?\]", RegexOptions.CultureInvariant)]
    private static partial Regex MutationAttributeRegex();

    [GeneratedRegex(@"(?<attributes>(?:\s*\[[^\]]+\]\s*)+)\b(?:public|internal)\s+(?:(?:static|virtual|override|sealed|new|unsafe|async)\s+)*", RegexOptions.CultureInvariant)]
    private static partial Regex EndpointDeclarationRegex();

    [GeneratedRegex(@"\b(?:public|internal)\s+(?:(?:sealed|abstract|partial)\s+)*class\s+\w*Controller\b", RegexOptions.CultureInvariant)]
    private static partial Regex ControllerDeclarationRegex();

    [GeneratedRegex(@"\[Authorize(?:Attribute)?\s*\(\s*[^)\s][^)]*\)\]", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationAttributeRegex();

    [GeneratedRegex(@"\[AllowAnonymous(?:Attribute)?\s*\]", RegexOptions.CultureInvariant)]
    private static partial Regex AllowAnonymousAttributeRegex();

    [GeneratedRegex(@"(?im)(?<name>password|secret(?:key)?|accesskey|clientsecret|apikey|token)\s*(?:=|:|\])\s*(?<value>[^\r\n#]+)", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialAssignmentRegex();

    [GeneratedRegex(@"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyRegex();
}
