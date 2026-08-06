using Xunit;

namespace Architecture.Tests;

public sealed class CatalogMediaOwnerIdentityTests
{
    [Fact]
    public void CatalogMediaUsesCanonicalCatalogMediaClrAndDiagnosticIdentity()
    {
        var repository = RepositoryModel.Load();
        var topologyManifest = Path.Combine(
            repository.Root,
            "docs",
            "architecture",
            "project-topology.json");
        var obsoleteToken = "Catalog" + "Media.";
        var violations = new List<string>();

        foreach (var root in new[] { "src", "tests", "tools", ".tools", ".codex", "docs" })
        {
            var directory = Path.Combine(repository.Root, root);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(path, topologyManifest, StringComparison.OrdinalIgnoreCase) ||
                    path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
                {
                    continue;
                }

                var extension = Path.GetExtension(path);
                if (extension is not (".cs" or ".csproj" or ".json" or ".md" or ".props" or ".py" or ".slnx" or ".targets" or ".yaml" or ".yml"))
                {
                    continue;
                }

                if (File.ReadAllText(path).Contains(obsoleteToken, StringComparison.Ordinal))
                {
                    violations.Add(repository.Relative(path));
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "The obsolete Catalog Media CLR or diagnostic identity remains active:\n" +
            string.Join('\n', violations.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void CatalogMediaTransportDoesNotNormalizeLegacyOwnerIdentity()
    {
        var repository = RepositoryModel.Load();
        var middlewarePath = Path.Combine(
            repository.Root,
            "src",
            "Catalog",
            "Catalog.Api",
            "CatalogMediaFailureMiddleware.cs");
        var middleware = File.ReadAllText(middlewarePath);

        Assert.DoesNotContain("NormalizeOwner", middleware, StringComparison.Ordinal);
        Assert.Contains("exception.Owner", middleware, StringComparison.Ordinal);
    }
}
