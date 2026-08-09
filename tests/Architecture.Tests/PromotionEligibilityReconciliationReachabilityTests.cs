using Xunit;

namespace Architecture.Tests;

public sealed class PromotionEligibilityReconciliationReachabilityTests
{
    [Fact]
    public void ConsumerReconcilesAfterInboxProjectionBeforeAcknowledgement()
    {
        var repository = RepositoryModel.Load();
        var application = Read(
            repository,
            "src/Promotion/Promotion.Application/CatalogListingPromotionEligibilityProjection.cs");
        var worker = Read(
            repository,
            "src/Promotion/Promotion.Worker/PromotionEligibilityProjectionWorker.cs");

        var projectionWrite = application.IndexOf(
            "var projectionResult = await store.ApplyAsync(",
            StringComparison.Ordinal);
        var reconciliation = application.IndexOf(
            "placementReconciler.PauseIneligiblePlacementsAsync(",
            StringComparison.Ordinal);
        var returnResult = application.IndexOf(
            "return projectionResult;",
            StringComparison.Ordinal);
        Assert.True(
            projectionWrite >= 0 &&
            reconciliation > projectionWrite &&
            returnResult > reconciliation,
            "Promotion must persist/replay the exact Catalog projection before reconciling placements.");
        Assert.Contains(
            "PromotionCommandContext.Continue(",
            application,
            StringComparison.Ordinal);
        Assert.Contains(
            "PromotionActor.Create(_ownerOptions.SystemActorId)",
            worker,
            StringComparison.Ordinal);
        Assert.Contains(
            "await service.ApplyAsync(",
            worker,
            StringComparison.Ordinal);
        Assert.Contains(
            "BasicAckAsync(",
            worker,
            StringComparison.Ordinal);
        Assert.True(
            worker.IndexOf("await service.ApplyAsync(", StringComparison.Ordinal) <
            worker.IndexOf("BasicAckAsync(", StringComparison.Ordinal),
            "Promotion must acknowledge Catalog eligibility only after projection and placement reconciliation.");
    }

    [Fact]
    public void ReconcilerPausesWithoutInventingAutomaticResume()
    {
        var repository = RepositoryModel.Load();
        var reconciler = Read(
            repository,
            "src/Promotion/Promotion.Infrastructure/EfPromotionRepository.EligibilityReconciliation.cs");
        var domain = Read(
            repository,
            "src/Promotion/Promotion.Domain/SponsoredPlacement.cs");
        var composition = Read(
            repository,
            "src/Promotion/Promotion.Infrastructure/PromotionInfrastructureServiceCollectionExtensions.cs");

        Assert.Contains("IsolationLevel.Serializable", reconciler, StringComparison.Ordinal);
        Assert.Contains("PauseWhenCatalogIneligible(", reconciler, StringComparison.Ordinal);
        Assert.Contains("PlacementCapacity.RemoveRange", reconciler, StringComparison.Ordinal);
        Assert.Contains("PromotionOutboxMessageFactory.Create(", reconciler, StringComparison.Ordinal);
        Assert.Contains("PromotionIntegrationEventTypes.PlacementChanged", reconciler, StringComparison.Ordinal);
        Assert.Contains("PROMOTION_ELIGIBILITY_RECONCILIATION_CONFLICT", reconciler, StringComparison.Ordinal);
        Assert.Contains("PROMOTION_ELIGIBILITY_RECONCILIATION_SERIALIZATION_CONFLICT", reconciler, StringComparison.Ordinal);
        Assert.DoesNotContain(".Resume(", reconciler, StringComparison.Ordinal);
        Assert.Contains("public bool PauseWhenCatalogIneligible(", domain, StringComparison.Ordinal);
        Assert.Contains(
            "services.AddScoped<IPromotionEligibilityPlacementReconciler>",
            composition,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PromotionDependsOnlyOnProducerOwnedCatalogContracts()
    {
        var repository = RepositoryModel.Load();
        var applicationProject = Full(
            repository,
            "src/Promotion/Promotion.Application/Promotion.Application.csproj");
        var workerProject = Full(
            repository,
            "src/Promotion/Promotion.Worker/Promotion.Worker.csproj");
        var applicationTargets = repository.References
            .Where(edge => string.Equals(edge.Source, applicationProject, StringComparison.OrdinalIgnoreCase))
            .Select(edge => repository.Relative(edge.Target))
            .ToArray();
        var workerTargets = repository.References
            .Where(edge => string.Equals(edge.Source, workerProject, StringComparison.OrdinalIgnoreCase))
            .Select(edge => repository.Relative(edge.Target))
            .ToArray();

        Assert.Contains(
            "src/Catalog/Catalog.Contracts/Catalog.Contracts.csproj",
            applicationTargets);
        Assert.Contains(
            "src/Catalog/Catalog.Contracts/Catalog.Contracts.csproj",
            workerTargets);
        Assert.DoesNotContain(
            applicationTargets.Concat(workerTargets),
            target =>
                target.Contains("Catalog.Application", StringComparison.OrdinalIgnoreCase) ||
                target.Contains("Catalog.Domain", StringComparison.OrdinalIgnoreCase) ||
                target.Contains("Catalog.Infrastructure", StringComparison.OrdinalIgnoreCase));
    }

    private static string Read(RepositoryModel repository, string relativePath) =>
        File.ReadAllText(Full(repository, relativePath));

    private static string Full(RepositoryModel repository, string relativePath) =>
        Path.GetFullPath(Path.Combine(
            repository.Root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
