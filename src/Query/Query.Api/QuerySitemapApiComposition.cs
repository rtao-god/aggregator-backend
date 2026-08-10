using Aggregator.Query.Application;
using Aggregator.Query.Infrastructure;
using Npgsql;

namespace Aggregator.Query.Api;

/// <summary>Query API composition boundary for the read-only sitemap owner.</summary>
internal static class QuerySitemapApiComposition
{
    public static ReadPublicSitemapService Create(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        return new ReadPublicSitemapService(
            new PostgresPublicSitemapStore(dataSource));
    }
}
