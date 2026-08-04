namespace Aggregator.Query.Infrastructure;

public sealed record QueryDatabaseOptions
{
    public required string ConnectionString { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException("Query database connection string is required.");
        }
    }
}

public sealed record QueryPublicationArtifactReaderOptions
{
    public string AllowedObjectPrefix { get; init; } = "catalog/";

    public long MaximumArtifactBytes { get; init; } = 64L * 1024L * 1024L;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AllowedObjectPrefix) ||
            AllowedObjectPrefix.StartsWith('/') ||
            AllowedObjectPrefix.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Query publication artifact prefix is invalid.");
        }

        if (MaximumArtifactBytes is < 1 or > 512L * 1024L * 1024L)
        {
            throw new InvalidOperationException("Query publication artifact limit must be between 1 byte and 512 MiB.");
        }
    }
}
