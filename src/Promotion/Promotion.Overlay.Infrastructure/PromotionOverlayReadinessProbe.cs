using Npgsql;

namespace Aggregator.Promotion.Overlay.Infrastructure;

public sealed class PromotionOverlayReadinessProbe
{
    private readonly NpgsqlDataSource _dataSource;

    public PromotionOverlayReadinessProbe(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<bool> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var command = _dataSource.CreateCommand(
                "SELECT EXISTS (SELECT 1 FROM promotion.current_overlay LIMIT 1);");
            _ = await command.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return false;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }
}
