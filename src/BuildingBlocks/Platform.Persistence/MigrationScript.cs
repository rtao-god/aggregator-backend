using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Platform.Persistence;

/// <summary>A checksum-addressed owner migration embedded in its migration executable.</summary>
public sealed record MigrationScript(string Version, string ResourceName, string Sql, string Sha256)
{
    public static IReadOnlyList<MigrationScript> LoadFromAssembly(Assembly assembly, string resourcePrefix)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (string.IsNullOrWhiteSpace(resourcePrefix))
        {
            throw new ArgumentException("A resource prefix is required.", nameof(resourcePrefix));
        }

        var scripts = assembly
            .GetManifestResourceNames()
            .Where(name => name.StartsWith(resourcePrefix, StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => Load(assembly, name, resourcePrefix))
            .ToArray();

        if (scripts.Length == 0)
        {
            throw new InvalidOperationException($"No embedded SQL migrations were found under '{resourcePrefix}'.");
        }

        if (scripts.Select(script => script.Version).Distinct(StringComparer.Ordinal).Count() != scripts.Length)
        {
            throw new InvalidOperationException("Migration versions must be unique within one owner database.");
        }

        return scripts;
    }

    private static MigrationScript Load(Assembly assembly, string resourceName, string prefix)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration '{resourceName}' cannot be opened.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        var sql = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new InvalidOperationException($"Embedded migration '{resourceName}' is empty.");
        }

        var relative = resourceName[prefix.Length..].TrimStart('.');
        var separator = relative.IndexOf('.', StringComparison.Ordinal);
        var version = separator > 0 ? relative[..separator] : relative[..^4];
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();
        return new MigrationScript(version, resourceName, sql, digest);
    }
}
