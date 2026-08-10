using Aggregator.Query.Application;
using Npgsql;

namespace Aggregator.Query.Infrastructure;

/// <summary>
/// Applies one exact immutable safety overlay to all public reads and refuses traffic while any
/// Catalog suppression event is known but not yet represented by the current public-read revision.
/// </summary>
public sealed partial class SafetyAwarePublicQueryStore : IPublicQueryStore, IPublicFacetCatalogStore
{
    private const int InnerPageSize = 101;
    private readonly NpgsqlPublicQueryStore _inner;
    private readonly NpgsqlDataSource _dataSource;
    private readonly IQueryClock _clock;

    public SafetyAwarePublicQueryStore(
        NpgsqlPublicQueryStore inner,
        NpgsqlDataSource dataSource,
        IQueryClock clock)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }
}
