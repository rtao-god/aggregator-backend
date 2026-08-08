using Xunit;

namespace Architecture.Tests;

public sealed class ProductGeographyIsolationTests
{
    [Fact]
    public void BerlinGeographyNamesDoNotBecomeCoreDomainOrWireStates()
    {
        var repository = RepositoryModel.Load();
        var violations = new List<string>();
        var forbiddenTokens = new[]
        {
            "BerlinCore",
            "BerlinNearby",
        };
        var roots = new[]
        {
            Path.Combine(repository.Root, "src", "Catalog"),
            Path.Combine(repository.Root, "src", "Query"),
        };

        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
                {
                    continue;
                }

                var text = File.ReadAllText(path);
                foreach (var token in forbiddenTokens)
                {
                    if (text.Contains(token, StringComparison.Ordinal))
                    {
                        violations.Add($"{repository.Relative(path)}: {token}");
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Product-specific geography names remain in reusable Catalog/Query production contracts:\n" +
            string.Join('\n', violations.Order(StringComparer.Ordinal)));
    }
}
