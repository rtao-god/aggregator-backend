using Aggregator.Catalog.Application;
using Microsoft.EntityFrameworkCore;

namespace Aggregator.Catalog.Infrastructure;

public sealed class EfCatalogIngestionTargetProjectionWriter(
    CatalogIngestionDbContext dbContext) : ICatalogIngestionTargetProjectionWriter
{
    public async Task UpsertAsync(
        string siteKey,
        string catalogKey,
        Guid activeConfigurationRevisionId,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogKey);
        if (activeConfigurationRevisionId == Guid.Empty)
        {
            throw new ArgumentException(
                "An active Catalog configuration revision ID is required.",
                nameof(activeConfigurationRevisionId));
        }

        if (observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The Catalog configuration observation timestamp must use UTC.",
                nameof(observedAtUtc));
        }

        var row = await dbContext.Targets.SingleOrDefaultAsync(candidate =>
            candidate.SiteKey == siteKey && candidate.CatalogKey == catalogKey,
            cancellationToken);
        if (row is null)
        {
            dbContext.Targets.Add(new CatalogIngestionTargetRow
            {
                SiteKey = siteKey,
                CatalogKey = catalogKey,
                ActiveConfigurationRevisionId = activeConfigurationRevisionId,
                ProjectionRevision = 1,
                UpdatedAtUtc = observedAtUtc,
            });
        }
        else if (row.ActiveConfigurationRevisionId != activeConfigurationRevisionId)
        {
            row.ActiveConfigurationRevisionId = activeConfigurationRevisionId;
            row.ProjectionRevision++;
            row.UpdatedAtUtc = observedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
