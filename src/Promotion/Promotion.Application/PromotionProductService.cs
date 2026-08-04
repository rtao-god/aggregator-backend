using Aggregator.Promotion.Contracts;
using Aggregator.Promotion.Domain;

namespace Aggregator.Promotion.Application;

public sealed class PromotionProductService(
    IPromotionRepository repository,
    IPromotionIdSource idSource,
    IPromotionClock clock)
{
    public async Task<PromotionResponseResult<PromotionProductResponse>> CreateAsync(
        CreatePromotionProductRequest request,
        PromotionCommandContext commandContext,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(commandContext);
        ValidateContract(request.ContractIdentity, request.ContractRevision);
        var normalized = NormalizeRevision(
            request.DisplayNames,
            request.PresentationFeatures,
            request.RequiresVerifiedContact,
            request.RequiredContactCapability);
        var requestDigest = PromotionCanonicalJson.ComputeDigest(new
        {
            operation = "promotion-product-create",
            request.ContractIdentity,
            request.ContractRevision,
            key = request.Key,
            normalized.DisplayNames,
            normalized.Features,
            normalized.RequiresVerifiedContact,
            normalized.RequiredContactCapability,
        });
        var commandIdentity = PromotionCommandIdentity.Create(
            "promotion.product.create",
            idempotencyKey,
            requestDigest);
        var createdAtUtc = clock.GetUtcNow();
        var product = PromotionProduct.Create(
            idSource.CreateId(),
            request.Key,
            idSource.CreateId(),
            normalized.DisplayNames,
            normalized.Features,
            normalized.RequiresVerifiedContact,
            normalized.RequiredContactCapability,
            commandContext.Actor.Id,
            createdAtUtc,
            normalized.ContentDigest);
        var result = await repository.AddProductAsync(
            product,
            commandIdentity,
            commandContext,
            cancellationToken);
        return new PromotionResponseResult<PromotionProductResponse>(
            PromotionContractMapper.ToResponse(result.Aggregate),
            result.Replayed);
    }

    public async Task<PromotionResponseResult<PromotionProductResponse>> AddRevisionAsync(
        Guid productId,
        CreatePromotionProductRevisionRequest request,
        PromotionCommandContext commandContext,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(commandContext);
        var product = await RequireProductAsync(productId, cancellationToken);
        var storedRevision = product.AggregateRevision;
        var normalized = NormalizeRevision(
            request.DisplayNames,
            request.PresentationFeatures,
            request.RequiresVerifiedContact,
            request.RequiredContactCapability);
        var requestDigest = PromotionCanonicalJson.ComputeDigest(new
        {
            operation = "promotion-product-revision-create",
            productId,
            request.ExpectedAggregateRevision,
            normalized.DisplayNames,
            normalized.Features,
            normalized.RequiresVerifiedContact,
            normalized.RequiredContactCapability,
        });
        var commandIdentity = PromotionCommandIdentity.Create(
            $"promotion.product.{productId:N}.revision.create",
            idempotencyKey,
            requestDigest);
        product.AddRevision(
            request.ExpectedAggregateRevision,
            idSource.CreateId(),
            normalized.DisplayNames,
            normalized.Features,
            normalized.RequiresVerifiedContact,
            normalized.RequiredContactCapability,
            commandContext.Actor.Id,
            clock.GetUtcNow(),
            normalized.ContentDigest);
        var result = await repository.SaveProductAsync(
            product,
            storedRevision,
            commandIdentity,
            commandContext,
            cancellationToken);
        return new PromotionResponseResult<PromotionProductResponse>(
            PromotionContractMapper.ToResponse(result.Aggregate),
            result.Replayed);
    }

    public async Task<PromotionResponseResult<PromotionProductResponse>> ChangeStateAsync(
        Guid productId,
        ChangePromotionProductStateRequest request,
        PromotionCommandContext commandContext,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(commandContext);
        var product = await RequireProductAsync(productId, cancellationToken);
        var storedRevision = product.AggregateRevision;
        var state = PromotionContractMapper.ToDomain(request.State);
        var requestDigest = PromotionCanonicalJson.ComputeDigest(new
        {
            operation = "promotion-product-state-change",
            productId,
            request.ExpectedAggregateRevision,
            state,
        });
        var commandIdentity = PromotionCommandIdentity.Create(
            $"promotion.product.{productId:N}.state.change",
            idempotencyKey,
            requestDigest);
        product.ChangeState(request.ExpectedAggregateRevision, state);
        var result = await repository.SaveProductAsync(
            product,
            storedRevision,
            commandIdentity,
            commandContext,
            cancellationToken);
        return new PromotionResponseResult<PromotionProductResponse>(
            PromotionContractMapper.ToResponse(result.Aggregate),
            result.Replayed);
    }

    public async Task<PromotionProductResponse> GetAsync(
        Guid productId,
        CancellationToken cancellationToken) =>
        PromotionContractMapper.ToResponse(await RequireProductAsync(productId, cancellationToken));

    private async Task<PromotionProduct> RequireProductAsync(
        Guid productId,
        CancellationToken cancellationToken)
    {
        if (productId == Guid.Empty)
        {
            throw new PromotionApplicationException(
                "Promotion.Products",
                "PROMOTION_PRODUCT_ID_INVALID",
                400,
                "Promotion product ID is empty.",
                "Use the exact product ID returned by the Promotion API.");
        }

        return await repository.GetProductAsync(productId, cancellationToken)
            ?? throw new PromotionApplicationException(
                "Promotion.Products",
                "PROMOTION_PRODUCT_NOT_FOUND",
                404,
                $"Promotion product '{productId}' was not found.",
                "Reload the Promotion product inventory before submitting another command.");
    }

    private static NormalizedProductRevision NormalizeRevision(
        IReadOnlyDictionary<string, string>? displayNames,
        IReadOnlyList<PromotionPresentationFeatureContract>? presentationFeatures,
        bool requiresVerifiedContact,
        string? requiredContactCapability)
    {
        if (displayNames is null || presentationFeatures is null)
        {
            throw new PromotionApplicationException(
                "Promotion.Products",
                "PROMOTION_PRODUCT_CONTRACT_INVALID",
                400,
                "Promotion product display names and presentation features are required.",
                "Submit the complete Promotion product revision contract.");
        }

        var normalizedNames = displayNames
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var features = presentationFeatures
            .Select(PromotionContractMapper.ToDomain)
            .Distinct()
            .OrderBy(feature => (int)feature)
            .ToArray();
        var contentDigest = PromotionCanonicalJson.ComputeDigest(new
        {
            displayNames = normalizedNames,
            presentationFeatures = features,
            requiresVerifiedContact,
            requiredContactCapability,
        });
        return new NormalizedProductRevision(
            normalizedNames,
            features,
            requiresVerifiedContact,
            requiredContactCapability,
            contentDigest);
    }

    private static void ValidateContract(string identity, int revision)
    {
        if (!string.Equals(identity, PromotionContractIdentity.AdminApi, StringComparison.Ordinal) ||
            revision != PromotionContractIdentity.AdminApiRevision)
        {
            throw new PromotionApplicationException(
                "Promotion.Contracts",
                "PROMOTION_CONTRACT_UNSUPPORTED",
                422,
                $"Promotion contract '{identity}' revision '{revision}' is unsupported.",
                $"Use '{PromotionContractIdentity.AdminApi}' revision '{PromotionContractIdentity.AdminApiRevision}'.");
        }
    }

    private sealed record NormalizedProductRevision(
        IReadOnlyDictionary<string, string> DisplayNames,
        IReadOnlyList<PromotionPresentationFeature> Features,
        bool RequiresVerifiedContact,
        string? RequiredContactCapability,
        string ContentDigest);
}
