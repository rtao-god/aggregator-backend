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
            failures.AddRange(FindCommittedCredentialValues(source, relative));
            foreach (Match match in PrivateKeyRegex().Matches(source))
            {
                failures.Add($"{relative} contains a private-key material marker at offset {match.Index}.");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void CredentialScannerRejectsLiteralAssignmentsAndConnectionStrings()
    {
        const string configuration = """
        POSTGRES_PASSWORD: production-password
        TOKEN: production-bearer-token
        ConnectionStrings__Catalog: Host=db;Database=catalog;Password=production-database-password
        """;
        const string source = """
        private const string ClientSecret = "production-client-secret";
        """;
        const string xml = """
        <ApiKey>production-api-key</ApiKey>
        """;

        var failures = FindCommittedCredentialValues(configuration, "fixture.yml")
            .Concat(FindCommittedCredentialValues(source, "fixture.cs"))
            .Concat(FindCommittedCredentialValues(xml, "fixture.xml"))
            .ToArray();

        Assert.Equal(5, failures.Length);
    }

    [Fact]
    public void CredentialScannerAcceptsReferencesAndNonCredentialRuntimeTokens()
    {
        const string configuration = """
        POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:?required}
        CLIENT_SECRET: <secret-mount>
        ACCESS_TOKEN: {{ .Secrets.AccessToken }}
        """;
        const string source = """
        var cancellationToken = cancellationTokenSource.Token;
        LeaseToken = row.LeaseToken;
        AccessToken = configuration["Authentication:AccessToken"];
        private const string ExampleApiKey = "example-api-key";
        """;

        var failures = FindCommittedCredentialValues(configuration, "fixture.yml")
            .Concat(FindCommittedCredentialValues(source, "fixture.cs"));

        Assert.Empty(failures);
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

    private static IReadOnlyList<string> FindCommittedCredentialValues(
        string source,
        string sourceIdentity)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdentity);

        var failures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assignment in EnumerateCredentialAssignments(source, sourceIdentity))
        {
            if (!IsCredentialName(assignment.Name))
            {
                continue;
            }

            var value = NormalizeCredentialValue(assignment.Value);
            if (IsSafeCredentialPlaceholder(value))
            {
                continue;
            }

            failures.Add(
                $"{sourceIdentity} appears to commit a literal credential for '{assignment.Name}' near offset {assignment.Index}.");
        }

        return failures.OrderBy(failure => failure, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<(string Name, string Value, int Index)> EnumerateCredentialAssignments(
        string source,
        string sourceIdentity)
    {
        var extension = Path.GetExtension(sourceIdentity);
        if (string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase))
        {
            foreach (Match match in CSharpLiteralCredentialAssignmentRegex().Matches(source))
            {
                yield return (
                    match.Groups["name"].Value,
                    match.Groups["value"].Value,
                    match.Index);
            }
        }
        else if (string.Equals(extension, ".sql", StringComparison.OrdinalIgnoreCase))
        {
            foreach (Match match in SqlPasswordLiteralRegex().Matches(source))
            {
                yield return (
                    match.Groups["name"].Value,
                    match.Groups["value"].Value,
                    match.Index);
            }
        }
        else if (extension is ".xml" or ".props" or ".targets")
        {
            foreach (Match match in XmlCredentialElementRegex().Matches(source))
            {
                yield return (
                    match.Groups["name"].Value,
                    match.Groups["value"].Value,
                    match.Index);
            }

            foreach (Match match in XmlCredentialAttributeRegex().Matches(source))
            {
                yield return (
                    match.Groups["name"].Value,
                    match.Groups["value"].Value,
                    match.Index);
            }
        }
        else
        {
            foreach (Match match in ConfigurationCredentialAssignmentRegex().Matches(source))
            {
                yield return (
                    match.Groups["name"].Value,
                    match.Groups["value"].Value,
                    match.Index);
            }
        }

        foreach (Match match in ConnectionStringCredentialRegex().Matches(source))
        {
            yield return (
                match.Groups["name"].Value,
                match.Groups["value"].Value,
                match.Index);
        }
    }

    private static bool IsCredentialName(string name)
    {
        var withCamelCaseBoundaries = CamelCaseBoundaryRegex().Replace(name, "$1_$2");
        var normalized = NonCredentialNameCharacterRegex()
            .Replace(withCamelCaseBoundaries, "_")
            .Trim('_')
            .ToLowerInvariant();
        var parts = normalized.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && parts[0] == "token")
        {
            return true;
        }

        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index] is "password" or "passwd" or "pwd" or "pass" or "secret" or
                "apikey" or "accesskey" or "secretkey" or "clientsecret")
            {
                return true;
            }

            if (index + 1 >= parts.Length)
            {
                continue;
            }

            var first = parts[index];
            var second = parts[index + 1];
            if ((first, second) is ("client", "secret") or ("secret", "key") or
                ("access", "key") or ("api", "key"))
            {
                return true;
            }

            if (second == "token" &&
                first is "access" or "auth" or "bearer" or "service" or "worker" or
                    "bootstrap" or "jwt" or "oidc" or "api")
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeCredentialValue(string value)
    {
        var normalized = value.Trim().TrimEnd(',', ';').Trim();
        var quoteIndex = normalized.IndexOf('"');
        if (quoteIndex >= 0 && normalized.EndsWith('"'))
        {
            normalized = normalized[(quoteIndex + 1)..^1];
        }
        else if (normalized.Length >= 2 && normalized.StartsWith('\'') && normalized.EndsWith('\''))
        {
            normalized = normalized[1..^1];
        }

        return normalized.Trim();
    }

    private static bool IsSafeCredentialPlaceholder(string value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.Contains("${", StringComparison.Ordinal) ||
        value.Contains("{{", StringComparison.Ordinal) ||
        value.Contains("$(`", StringComparison.Ordinal) ||
        value.Contains('<') ||
        EnvironmentReferenceRegex().IsMatch(value) ||
        InterpolatedReferenceRegex().IsMatch(value) ||
        ParameterReferenceRegex().IsMatch(value) ||
        SafeCredentialMarkerRegex().IsMatch(value) ||
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
        var fileName = Path.GetFileName(relativePath);
        return extension is ".json" or ".yml" or ".yaml" or ".xml" or ".props" or
            ".targets" or ".cs" or ".md" or ".env" or ".sql" or ".toml" or ".ini" or
            ".conf" or ".sh" or ".ps1" ||
            fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase);
    }

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

    [GeneratedRegex(@"(?im)^\s*(?:-\s*)?(?:export\s+)?\$?(?:\[\s*[\"'](?<name>[A-Za-z_][A-Za-z0-9_.:-]*)[\"']\s*\]|[\"']?(?<name>[A-Za-z_][A-Za-z0-9_.:-]*)[\"']?)\s*(?:=|:)\s*(?<value>[^\r\n#]+)", RegexOptions.CultureInvariant)]
    private static partial Regex ConfigurationCredentialAssignmentRegex();

    [GeneratedRegex(@"(?im)^\s*(?:\[\s*[\"'](?<name>[A-Za-z_][A-Za-z0-9_.:-]*)[\"']\s*\]|(?:(?:public|private|protected|internal|static|readonly|const|required|volatile|new)\s+)*(?:(?:string|var)\s+)?(?<name>[A-Za-z_][A-Za-z0-9_.:-]*)(?:\s*\{[^}\r\n]*\})?)\s*=\s*(?<value>(?:\$@|@\$|\$|@)?\"(?:\\.|\"\"|[^\"\r\n])*\")", RegexOptions.CultureInvariant)]
    private static partial Regex CSharpLiteralCredentialAssignmentRegex();

    [GeneratedRegex(@"(?im)<(?<name>[A-Za-z_][A-Za-z0-9_.:-]*)>\s*(?<value>[^<\r\n]+)\s*</\k<name>>", RegexOptions.CultureInvariant)]
    private static partial Regex XmlCredentialElementRegex();

    [GeneratedRegex(@"(?im)\b(?<name>[A-Za-z_][A-Za-z0-9_.:-]*)\s*=\s*[\"'](?<value>[^\"']*)[\"']", RegexOptions.CultureInvariant)]
    private static partial Regex XmlCredentialAttributeRegex();

    [GeneratedRegex(@"(?im)\b(?<name>PASSWORD|PASSWD|PWD)\s*(?:=)?\s*[\"'](?<value>[^\"']*)[\"']", RegexOptions.CultureInvariant)]
    private static partial Regex SqlPasswordLiteralRegex();

    [GeneratedRegex(@"(?im)(?:^|[;\"'])\s*(?<name>Password|Passwd|Pwd)\s*=\s*(?<value>[^;\r\n\"']+)", RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStringCredentialRegex();

    [GeneratedRegex(@"([a-z0-9])([A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex CamelCaseBoundaryRegex();

    [GeneratedRegex(@"[^A-Za-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonCredentialNameCharacterRegex();

    [GeneratedRegex(@"(?i)(?:^|[-_.:/])(?:test|testing|example|fixture|dummy|fake|local|development|dev|change[-_]?me|required|placeholder|not[-_]?a[-_]?secret)(?:$|[-_.:/])", RegexOptions.CultureInvariant)]
    private static partial Regex SafeCredentialMarkerRegex();

    [GeneratedRegex(@"^\$[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentReferenceRegex();

    [GeneratedRegex(@"^\{[A-Za-z_0-9][A-Za-z0-9_.]*\}$", RegexOptions.CultureInvariant)]
    private static partial Regex InterpolatedReferenceRegex();

    [GeneratedRegex(@"^[@:][A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterReferenceRegex();

    [GeneratedRegex(@"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyRegex();
}
