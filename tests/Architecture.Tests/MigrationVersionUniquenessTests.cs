using System.Text.RegularExpressions;
using Xunit;

namespace Architecture.Tests;

public sealed partial class MigrationVersionUniquenessTests
{
    [Fact]
    public void EveryBoundedContextOwnsOneFilePerMigrationVersion()
    {
        var repository = RepositoryModel.Load();
        var migrationDirectories = Directory
            .EnumerateDirectories(
                Path.Combine(repository.Root, "src"),
                "Migrations",
                SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.NotEmpty(migrationDirectories);
        foreach (var directory in migrationDirectories)
        {
            var duplicateVersions = Directory
                .EnumerateFiles(directory, "V*__*.sql", SearchOption.TopDirectoryOnly)
                .Select(path => new
                {
                    Path = path,
                    Match = MigrationFileName().Match(System.IO.Path.GetFileName(path)),
                })
                .Where(item => item.Match.Success)
                .GroupBy(
                    item => item.Match.Groups["version"].Value,
                    StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => new
                {
                    Version = group.Key,
                    Files = group
                        .Select(item => repository.Relative(item.Path))
                        .OrderBy(path => path, StringComparer.Ordinal)
                        .ToArray(),
                })
                .ToArray();

            Assert.True(
                duplicateVersions.Length == 0,
                $"Migration directory '{repository.Relative(directory)}' contains duplicate versions: " +
                string.Join(
                    "; ",
                    duplicateVersions.Select(duplicate =>
                        $"V{duplicate.Version} => {string.Join(", ", duplicate.Files)}")));
        }
    }

    [GeneratedRegex(@"^V(?<version>[0-9]+(?:\.[0-9]+)*)__.+\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex MigrationFileName();
}
