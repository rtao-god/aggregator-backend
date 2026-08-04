using Aggregator.Promotion.Application;
using Aggregator.Promotion.Domain;
using Microsoft.EntityFrameworkCore;

namespace Aggregator.Promotion.Infrastructure;

public sealed partial class EfPromotionRepository
{
    public async Task<PromotionProduct?> GetProductAsync(
        Guid productId,
        CancellationToken cancellationToken)
    {
        var row = await _dbContext.Products
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == productId, cancellationToken);
        return row is null
            ? null
            : await RestoreProductAsync(row, cancellationToken);
    }

    public async Task<PromotionProduct?> GetProductByKeyAsync(
        string productKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productKey);
        var normalizedKey = productKey.Trim().ToLowerInvariant();
        var row = await _dbContext.Products
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.ProductKey == normalizedKey, cancellationToken);
        return row is null
            ? null
            : await RestoreProductAsync(row, cancellationToken);
    }

    public Task<PromotionCommandResult<PromotionProduct>> AddProductAsync(
        PromotionProduct product,
        PromotionCommandIdentity commandIdentity,
        PromotionCommandContext commandContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(product);
        return ExecuteCommandAsync(
            commandIdentity,
            commandContext,
            innerCancellationToken =>
            {
                innerCancellationToken.ThrowIfCancellationRequested();
                _dbContext.Products.Add(new PromotionProductRow
                {
                    Id = product.Id,
                    ProductKey = product.Key,
                    State = (int)product.State,
                    CurrentRevisionId = product.CurrentRevision.Id,
                    AggregateRevision = product.AggregateRevision,
                });
                _dbContext.ProductRevisions.Add(ToRow(product.CurrentRevision));
                return Task.FromResult(product);
            },
            cancellationToken);
    }

    public Task<PromotionCommandResult<PromotionProduct>> SaveProductAsync(
        PromotionProduct product,
        long expectedStoredAggregateRevision,
        PromotionCommandIdentity commandIdentity,
        PromotionCommandContext commandContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(product);
        return ExecuteCommandAsync(
            commandIdentity,
            commandContext,
            async innerCancellationToken =>
            {
                var row = await _dbContext.Products.SingleOrDefaultAsync(
                    candidate => candidate.Id == product.Id,
                    innerCancellationToken)
                    ?? throw Failure(
                        "Promotion.Products",
                        "PROMOTION_PRODUCT_NOT_FOUND",
                        404,
                        $"Promotion product '{product.Id}' was not found.",
                        "Reload the product inventory before retrying the command.");
                EnsureStoredRevision(
                    row.AggregateRevision,
                    expectedStoredAggregateRevision,
                    "Promotion product",
                    product.Id);
                row.State = (int)product.State;
                row.CurrentRevisionId = product.CurrentRevision.Id;
                row.AggregateRevision = product.AggregateRevision;
                var revisionExists = await _dbContext.ProductRevisions
                    .AnyAsync(candidate => candidate.Id == product.CurrentRevision.Id, innerCancellationToken);
                if (!revisionExists)
                {
                    _dbContext.ProductRevisions.Add(ToRow(product.CurrentRevision));
                }

                return product;
            },
            cancellationToken);
    }

    private async Task<PromotionProduct> RestoreProductAsync(
        PromotionProductRow row,
        CancellationToken cancellationToken)
    {
        var revisionRow = await _dbContext.ProductRevisions
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == row.CurrentRevisionId, cancellationToken)
            ?? throw Failure(
                "Promotion.Persistence",
                "PROMOTION_PRODUCT_REVISION_MISSING",
                500,
                $"Promotion product '{row.Id}' points to missing revision '{row.CurrentRevisionId}'.",
                "Restore the exact Promotion product revision from a verified database backup.");
        return PromotionProduct.Restore(
            row.Id,
            row.ProductKey,
            (PromotionProductState)row.State,
            RestoreRevision(revisionRow),
            row.AggregateRevision);
    }

    private static PromotionProductRevisionRow ToRow(PromotionProductRevision revision) =>
        new()
        {
            Id = revision.Id,
            ProductId = revision.ProductId,
            RevisionNumber = revision.RevisionNumber,
            DisplayNamesJson = PromotionPersistenceJson.SerializeStringDictionary(revision.DisplayNames),
            PresentationFeaturesJson = PromotionPersistenceJson.SerializeEnumSet(revision.PresentationFeatures),
            RequiresVerifiedContact = revision.RequiresVerifiedContact,
            RequiredContactCapability = revision.RequiredContactCapability,
            CreatedByActorId = revision.CreatedByActorId,
            CreatedAtUtc = revision.CreatedAtUtc,
            ContentDigest = revision.ContentDigest,
        };

    private static PromotionProductRevision RestoreRevision(PromotionProductRevisionRow row) =>
        PromotionProductRevision.Create(
            row.Id,
            row.ProductId,
            row.RevisionNumber,
            PromotionPersistenceJson.DeserializeStringDictionary(row.DisplayNamesJson),
            PromotionPersistenceJson.DeserializeEnumSet<PromotionPresentationFeature>(row.PresentationFeaturesJson),
            row.RequiresVerifiedContact,
            row.RequiredContactCapability,
            row.CreatedByActorId,
            row.CreatedAtUtc,
            row.ContentDigest);

    private static void EnsureStoredRevision(
        long actual,
        long expected,
        string owner,
        Guid aggregateId)
    {
        if (actual != expected)
        {
            throw new PromotionApplicationException(
                "Promotion.Persistence",
                "PROMOTION_REVISION_CONFLICT",
                409,
                $"{owner} '{aggregateId}' expected stored revision '{expected}' but is at '{actual}'.",
                "Reload the current aggregate revision before retrying the command.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["aggregateId"] = aggregateId,
                    ["expectedRevision"] = expected,
                    ["actualRevision"] = actual,
                });
        }
    }
}
