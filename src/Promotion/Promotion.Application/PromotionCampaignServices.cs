using System.Security.Cryptography;
using System.Text.Json;
using Aggregator.Promotion.Contracts;
using Aggregator.Promotion.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Aggregator.Promotion.Application;

public sealed record PromotionEligibilitySnapshot(
    Guid ProductRevisionId,
    bool ProductRevisionActive,
    Guid EntitlementId,
    bool EntitlementActive,
    Guid ListingId,
    bool ListingEligible,
    string CatalogKey,
    string PlacementKey,
    int PlacementCapacityLimit,
    long ProjectionRevision);

public interface IPromotionEligibilityReader
{
    public Task<PromotionEligibilitySnapshot?> ReadAsync(
        Guid productRevisionId,
        Guid entitlementId,
        Guid listingId,
        string catalogKey,
        string placementKey,
        CancellationToken cancellationToken);
}

public sealed record PromotionCommandIdentity(string Scope, string Key, string RequestDigest)
{
    public static PromotionCommandIdentity Create(string scope, string key, string requestDigest)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope.Length > 150)
        {
            throw new PromotionCampaignApplicationException(
                "Promotion.Commands",
                "PROMOTION_COMMAND_SCOPE_INVALID",
                500,
                "The Promotion command scope is invalid.",
                "Correct the command composition before retrying.");
        }

        if (string.IsNullOrWhiteSpace(key) || key.Length > 200 || key.Any(char.IsControl))
        {
            throw new PromotionCampaignApplicationException(
                "Promotion.Commands",
                "PROMOTION_IDEMPOTENCY_KEY_INVALID",
                400,
                "A stable Idempotency-Key of at most 200 characters is required.",
                "Retry with one exact Idempotency-Key for this command.");
        }

        RequireDigest(requestDigest);
        return new PromotionCommandIdentity(scope, key, requestDigest);
    }

    private static void RequireDigest(string digest)
    {
        if (digest is not { Length: 64 } ||
            digest.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new PromotionCampaignApplicationException(
                "Promotion.Commands",
                "PROMOTION_REQUEST_DIGEST_INVALID",
                500,
                "The command request digest is invalid.",
                "Correct canonical command hashing before retrying.");
        }
    }
}

public sealed record PromotionCampaignSnapshot(
    Guid Id,
    Guid ProductRevisionId,
    Guid EntitlementId,
    Guid ListingId,
    string CatalogKey,
    string PlacementKey,
    int CapacityUnits,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastChangedAtUtc,
    PromotionCampaignState State,
    long AggregateRevision,
    string? SuspensionReason)
{
    public static PromotionCampaignSnapshot From(PromotionCampaign campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        return new PromotionCampaignSnapshot(
            campaign.Id,
            campaign.ProductRevisionId,
            campaign.EntitlementId,
            campaign.ListingId,
            campaign.CatalogKey,
            campaign.PlacementKey,
            campaign.CapacityUnits,
            campaign.StartsAtUtc,
            campaign.EndsAtUtc,
            campaign.CreatedAtUtc,
            campaign.LastChangedAtUtc,
            campaign.State,
            campaign.AggregateRevision,
            campaign.SuspensionReason);
    }

    public PromotionCampaign Restore() =>
        PromotionCampaign.Restore(
            Id,
            ProductRevisionId,
            EntitlementId,
            ListingId,
            CatalogKey,
            PlacementKey,
            CapacityUnits,
            StartsAtUtc,
            EndsAtUtc,
            CreatedAtUtc,
            LastChangedAtUtc,
            State,
            AggregateRevision,
            SuspensionReason);
}

public sealed record PromotionCampaignCommandResult(
    PromotionCampaignSnapshot Campaign,
    bool Replayed);

public interface IPromotionCampaignStore
{
    public Task<PromotionCampaignCommandResult> CreateAsync(
        PromotionCampaign campaign,
        int placementCapacityLimit,
        PromotionCommandIdentity commandIdentity,
        string callerIdentity,
        CancellationToken cancellationToken);

    public Task<PromotionCampaignSnapshot?> ReadAsync(
        Guid campaignId,
        CancellationToken cancellationToken);

    public Task<PromotionCampaignCommandResult> SaveAsync(
        PromotionCampaign campaign,
        long expectedStoredAggregateRevision,
        PromotionCommandIdentity commandIdentity,
        string callerIdentity,
        CancellationToken cancellationToken);

    public Task<IReadOnlyList<PromotionCampaignSnapshot>> ReadActiveAsync(
        string catalogKey,
        string placementKey,
        DateTimeOffset effectiveAtUtc,
        int limit,
        CancellationToken cancellationToken);

    public Task<IReadOnlyList<PromotionCampaignSnapshot>> ReadExpiredAsync(
        DateTimeOffset effectiveAtUtc,
        int limit,
        CancellationToken cancellationToken);
}

public sealed class PromotionCampaignService(
    IPromotionCampaignStore store,
    IPromotionEligibilityReader eligibilityReader,
    TimeProvider timeProvider)
{
    public async Task<PromotionCampaignResponse> CreateAsync(
        CreatePromotionCampaignRequest request,
        string idempotencyKey,
        string callerIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var eligibility = await RequireEligibilityAsync(request, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var campaign = PromotionCampaign.Create(
            Guid.CreateVersion7(),
            request.ProductRevisionId,
            request.EntitlementId,
            request.ListingId,
            request.CatalogKey,
            request.PlacementKey,
            request.CapacityUnits,
            request.StartsAtUtc,
            request.EndsAtUtc,
            now);
        var identity = PromotionCommandIdentity.Create(
            "promotion.campaign.create",
            idempotencyKey,
            PromotionRequestHash.Compute(request));
        var result = await store.CreateAsync(
            campaign,
            eligibility.PlacementCapacityLimit,
            identity,
            callerIdentity,
            cancellationToken);
        return PromotionCampaignMapper.ToResponse(result.Campaign, result.Replayed);
    }

    public Task<PromotionCampaignResponse> ActivateAsync(
        Guid campaignId,
        PromotionCampaignRevisionRequest request,
        string idempotencyKey,
        string callerIdentity,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            campaignId,
            request,
            "promotion.campaign.activate",
            idempotencyKey,
            callerIdentity,
            async (campaign, eligibility, now) =>
            {
                campaign.Activate(
                    eligibility.ProductRevisionActive,
                    eligibility.EntitlementActive,
                    eligibility.ListingEligible,
                    request.ExpectedAggregateRevision,
                    now);
                await Task.CompletedTask;
            },
            cancellationToken);

    public Task<PromotionCampaignResponse> SuspendAsync(
        Guid campaignId,
        SuspendPromotionCampaignRequest request,
        string idempotencyKey,
        string callerIdentity,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            campaignId,
            request,
            "promotion.campaign.suspend",
            idempotencyKey,
            callerIdentity,
            async (campaign, _, now) =>
            {
                campaign.Suspend(request.Reason, request.ExpectedAggregateRevision, now);
                await Task.CompletedTask;
            },
            cancellationToken);

    public Task<PromotionCampaignResponse> ResumeAsync(
        Guid campaignId,
        PromotionCampaignRevisionRequest request,
        string idempotencyKey,
        string callerIdentity,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            campaignId,
            request,
            "promotion.campaign.resume",
            idempotencyKey,
            callerIdentity,
            async (campaign, eligibility, now) =>
            {
                campaign.Resume(
                    eligibility.ProductRevisionActive,
                    eligibility.EntitlementActive,
                    eligibility.ListingEligible,
                    request.ExpectedAggregateRevision,
                    now);
                await Task.CompletedTask;
            },
            cancellationToken);

    public Task<PromotionCampaignResponse> CancelAsync(
        Guid campaignId,
        PromotionCampaignRevisionRequest request,
        string idempotencyKey,
        string callerIdentity,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            campaignId,
            request,
            "promotion.campaign.cancel",
            idempotencyKey,
            callerIdentity,
            async (campaign, _, now) =>
            {
                campaign.Cancel(request.ExpectedAggregateRevision, now);
                await Task.CompletedTask;
            },
            cancellationToken);

    public async Task<PromotionCampaignResponse> ReadAsync(
        Guid campaignId,
        CancellationToken cancellationToken)
    {
        if (campaignId == Guid.Empty)
        {
            throw ContractFailure("PROMOTION_CAMPAIGN_ID_REQUIRED", "A campaign ID is required.");
        }

        var campaign = await store.ReadAsync(campaignId, cancellationToken)
            ?? throw NotFound(campaignId);
        return PromotionCampaignMapper.ToResponse(campaign, replayed: false);
    }

    public async Task<SponsoredPlacementResponse> ReadSponsoredPlacementAsync(
        string catalogKey,
        string placementKey,
        DateTimeOffset? effectiveAtUtc,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(catalogKey) || string.IsNullOrWhiteSpace(placementKey))
        {
            throw ContractFailure(
                "PROMOTION_PLACEMENT_IDENTITY_REQUIRED",
                "Catalog and placement keys are required.");
        }

        if (limit is < 1 or > 100)
        {
            throw ContractFailure(
                "PROMOTION_PLACEMENT_LIMIT_INVALID",
                "The sponsored placement limit must be between 1 and 100.");
        }

        var effectiveAt = effectiveAtUtc ?? timeProvider.GetUtcNow();
        if (effectiveAt.Offset != TimeSpan.Zero)
        {
            throw ContractFailure(
                "PROMOTION_EFFECTIVE_TIME_NOT_UTC",
                "The sponsored placement effective time must use UTC.");
        }

        var campaigns = await store.ReadActiveAsync(
            catalogKey,
            placementKey,
            effectiveAt,
            limit,
            cancellationToken);
        var items = campaigns
            .OrderBy(campaign => campaign.StartsAtUtc)
            .ThenBy(campaign => campaign.Id)
            .Select(campaign => new SponsoredPlacementItem(
                campaign.Id,
                campaign.ListingId,
                campaign.PlacementKey,
                campaign.StartsAtUtc,
                campaign.EndsAtUtc,
                "sponsored"))
            .ToArray();
        return new SponsoredPlacementResponse(
            catalogKey,
            placementKey,
            effectiveAt,
            items);
    }

    private async Task<PromotionCampaignResponse> TransitionAsync<TRequest>(
        Guid campaignId,
        TRequest request,
        string scope,
        string idempotencyKey,
        string callerIdentity,
        Func<PromotionCampaign, PromotionEligibilitySnapshot, DateTimeOffset, Task> transition,
        CancellationToken cancellationToken)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(request);
        if (campaignId == Guid.Empty)
        {
            throw ContractFailure("PROMOTION_CAMPAIGN_ID_REQUIRED", "A campaign ID is required.");
        }

        var identity = PromotionCommandIdentity.Create(
            scope,
            idempotencyKey,
            PromotionRequestHash.Compute(new PromotionTransitionHashInput<TRequest>(campaignId, request)));
        var existingResult = await TryReadCommandReplayAsync(identity, cancellationToken);
        if (existingResult is not null)
        {
            return PromotionCampaignMapper.ToResponse(existingResult, replayed: true);
        }

        var snapshot = await store.ReadAsync(campaignId, cancellationToken)
            ?? throw NotFound(campaignId);
        var campaign = snapshot.Restore();
        var eligibility = await RequireEligibilityAsync(campaign, cancellationToken);
        await transition(campaign, eligibility, timeProvider.GetUtcNow());
        var result = await store.SaveAsync(
            campaign,
            snapshot.AggregateRevision,
            identity,
            callerIdentity,
            cancellationToken);
        return PromotionCampaignMapper.ToResponse(result.Campaign, result.Replayed);
    }

    private async Task<PromotionCampaignSnapshot?> TryReadCommandReplayAsync(
        PromotionCommandIdentity identity,
        CancellationToken cancellationToken)
    {
        if (store is IPromotionCommandResultReader reader)
        {
            return await reader.ReadCommandResultAsync(identity, cancellationToken);
        }

        return null;
    }

    private Task<PromotionEligibilitySnapshot> RequireEligibilityAsync(
        CreatePromotionCampaignRequest request,
        CancellationToken cancellationToken) =>
        RequireEligibilityCoreAsync(
            request.ProductRevisionId,
            request.EntitlementId,
            request.ListingId,
            request.CatalogKey,
            request.PlacementKey,
            cancellationToken);

    private Task<PromotionEligibilitySnapshot> RequireEligibilityAsync(
        PromotionCampaign campaign,
        CancellationToken cancellationToken) =>
        RequireEligibilityCoreAsync(
            campaign.ProductRevisionId,
            campaign.EntitlementId,
            campaign.ListingId,
            campaign.CatalogKey,
            campaign.PlacementKey,
            cancellationToken);

    private async Task<PromotionEligibilitySnapshot> RequireEligibilityCoreAsync(
        Guid productRevisionId,
        Guid entitlementId,
        Guid listingId,
        string catalogKey,
        string placementKey,
        CancellationToken cancellationToken) =>
        await eligibilityReader.ReadAsync(
            productRevisionId,
            entitlementId,
            listingId,
            catalogKey,
            placementKey,
            cancellationToken)
        ?? throw new PromotionCampaignApplicationException(
            "Promotion.Eligibility",
            "PROMOTION_ELIGIBILITY_PROJECTION_MISSING",
            409,
            "Promotion cannot prove the exact product, entitlement, listing and placement eligibility projection.",
            "Wait for the producer events to reach Promotion and retry with the exact identities.");

    private static PromotionCampaignApplicationException NotFound(Guid campaignId) =>
        new(
            "Promotion.Campaigns",
            "PROMOTION_CAMPAIGN_NOT_FOUND",
            404,
            $"Promotion campaign '{campaignId:D}' was not found.",
            "Use the exact campaign identity returned by creation.");

    private static PromotionCampaignApplicationException ContractFailure(string code, string detail) =>
        new(
            "Promotion.Contracts",
            code,
            400,
            detail,
            "Correct the request contract and retry.");
}

public interface IPromotionCommandResultReader
{
    public Task<PromotionCampaignSnapshot?> ReadCommandResultAsync(
        PromotionCommandIdentity identity,
        CancellationToken cancellationToken);
}

public sealed class CompleteExpiredPromotionCampaignsService(
    IPromotionCampaignStore store,
    TimeProvider timeProvider)
{
    public async Task<int> CompleteAsync(int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 1_000)
        {
            throw new PromotionCampaignApplicationException(
                "Promotion.Worker",
                "PROMOTION_COMPLETION_LIMIT_INVALID",
                500,
                "The expiry completion batch must be between 1 and 1000.",
                "Correct the Promotion worker configuration.");
        }

        var now = timeProvider.GetUtcNow();
        var campaigns = await store.ReadExpiredAsync(now, limit, cancellationToken);
        var completed = 0;
        foreach (var snapshot in campaigns)
        {
            var campaign = snapshot.Restore();
            campaign.Complete(snapshot.AggregateRevision, now);
            var hashInput = new
            {
                campaignId = snapshot.Id,
                expectedAggregateRevision = snapshot.AggregateRevision,
                completedAtUtc = now,
            };
            var identity = PromotionCommandIdentity.Create(
                "promotion.campaign.complete-expired",
                $"complete:{snapshot.Id:D}:{snapshot.AggregateRevision}",
                PromotionRequestHash.Compute(hashInput));
            var result = await store.SaveAsync(
                campaign,
                snapshot.AggregateRevision,
                identity,
                "promotion-expiry-worker",
                cancellationToken);
            if (!result.Replayed)
            {
                completed++;
            }
        }

        return completed;
    }
}

public static class PromotionCampaignApplicationExtensions
{
    public static IServiceCollection AddPromotionCampaignApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<PromotionCampaignService>();
        services.AddScoped<CompleteExpiredPromotionCampaignsService>();
        return services;
    }
}

public static class PromotionCampaignMapper
{
    public static PromotionCampaignResponse ToResponse(
        PromotionCampaignSnapshot campaign,
        bool replayed) =>
        new(
            campaign.Id,
            campaign.ProductRevisionId,
            campaign.EntitlementId,
            campaign.ListingId,
            campaign.CatalogKey,
            campaign.PlacementKey,
            campaign.CapacityUnits,
            campaign.StartsAtUtc,
            campaign.EndsAtUtc,
            ToContract(campaign.State),
            campaign.AggregateRevision,
            campaign.LastChangedAtUtc,
            campaign.SuspensionReason,
            "sponsored",
            replayed);

    private static PromotionCampaignStateContract ToContract(PromotionCampaignState state) => state switch
    {
        PromotionCampaignState.Draft => PromotionCampaignStateContract.Draft,
        PromotionCampaignState.Active => PromotionCampaignStateContract.Active,
        PromotionCampaignState.Suspended => PromotionCampaignStateContract.Suspended,
        PromotionCampaignState.Completed => PromotionCampaignStateContract.Completed,
        PromotionCampaignState.Cancelled => PromotionCampaignStateContract.Cancelled,
        _ => throw new PromotionCampaignApplicationException(
            "Promotion.Persistence",
            "PROMOTION_CAMPAIGN_STATE_UNSUPPORTED",
            500,
            "The campaign state cannot be mapped to the producer contract.",
            "Repair the persisted campaign through an owner migration or restore operation."),
    };
}

public static class PromotionRequestHash
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public static string Compute<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}

public sealed record PromotionTransitionHashInput<TRequest>(Guid CampaignId, TRequest Request);

public sealed class PromotionCampaignApplicationException : InvalidOperationException
{
    public PromotionCampaignApplicationException(
        string owner,
        string code,
        int statusCode,
        string message,
        string requiredAction,
        IReadOnlyDictionary<string, object?>? context = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredAction);
        Owner = owner;
        Code = code;
        StatusCode = statusCode;
        RequiredAction = requiredAction;
        Context = context ?? new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    public string Owner { get; }

    public string Code { get; }

    public int StatusCode { get; }

    public string RequiredAction { get; }

    public IReadOnlyDictionary<string, object?> Context { get; }
}
