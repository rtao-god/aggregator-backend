using System.Xml.Linq;
using Xunit;

namespace Architecture.Tests;

public sealed class PromotionEligibilityReconciliationReachabilityTests
{
    [Fact]
    public void PromotionConsumesOnlyTheProducerOwnedCatalogContract()
    {
        var repository = RepositoryModel.Load();
        var applicationReferences = ReadProjectReferences(
            repository,
            "src/Promotion/Promotion.Application/Promotion.Application.csproj");
        var workerReferences = ReadProjectReferences(
            repository,
            "src/Promotion/Promotion.Worker/Promotion.Worker.csproj");

        Assert.Contains(
            "../../Catalog/Catalog.Contracts/Catalog.Contracts.csproj",
            applicationReferences);
        Assert.Contains(
            "../../Catalog/Catalog.Contracts/Catalog.Contracts.csproj",
            workerReferences);
        Assert.DoesNotContain(
            applicationReferences.Concat(workerReferences),
            reference =>
                reference.Contains("Catalog.Domain", StringComparison.OrdinalIgnoreCase) ||
                reference.Contains("Catalog.Application", StringComparison.OrdinalIgnoreCase) ||
                reference.Contains("Catalog.Infrastructure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WorkerRoutesOneCatalogEventThroughProjectionAndReconciliation()
    {
        var repository = RepositoryModel.Load();
        var program = Read(repository, "src/Promotion/Promotion.Worker/Program.cs");
        var worker = Read(
            repository,
            "src/Promotion/Promotion.Worker/PromotionEligibilityProjectionWorker.cs");
        var application = Read(
            repository,
            "src/Promotion/Promotion.Application/CatalogListingPromotionEligibilityProjection.cs");
        var registration = Read(
            repository,
            "src/Promotion/Promotion.Infrastructure/PromotionInfrastructureServiceCollectionExtensions.cs");

        Assert.Contains("AddPromotionApplication()", program, StringComparison.Ordinal);
        Assert.Contains(
            "AddPromotionInfrastructure(builder.Configuration)",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddHostedService<PromotionEligibilityProjectionWorker>()",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "PromotionActor.Create(_ownerOptions.SystemActorId)",
            worker,
            StringComparison.Ordinal);
        Assert.Contains("store.ApplyAsync(", application, StringComparison.Ordinal);
        Assert.Contains(
            "placementReconciler.PauseIneligiblePlacementsAsync(",
            application,
            StringComparison.Ordinal);
        Assert.Contains(
            "PromotionCommandContext.Continue(",
            application,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddScoped<IPromotionEligibilityPlacementReconciler>",
            registration,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReconciliationIsFailClosedAndCannotAutoResume()
    {
        var repository = RepositoryModel.Load();
        var reconciler = Read(
            repository,
            "src/Promotion/Promotion.Infrastructure/EfPromotionRepository.EligibilityReconciliation.cs");
        var domain = Read(
            repository,
            "src/Promotion/Promotion.Domain/SponsoredPlacement.cs");

        Assert.Contains("IsolationLevel.Serializable", reconciler, StringComparison.Ordinal);
        Assert.Contains(
            "pg_advisory_xact_lock(hashtextextended",
            reconciler,
            StringComparison.Ordinal);
        Assert.Contains(
            "currentEligibility.SourceRevision > eligibility.SourceRevision",
            reconciler,
            StringComparison.Ordinal);
        Assert.Contains(
            "currentEligibility.SourceRevision < eligibility.SourceRevision",
            reconciler,
            StringComparison.Ordinal);
        Assert.Contains("EnsureCurrentEligibilityMatches", reconciler, StringComparison.Ordinal);
        Assert.Contains("PauseWhenCatalogIneligible", reconciler, StringComparison.Ordinal);
        Assert.Contains("PlacementCapacity.RemoveRange", reconciler, StringComparison.Ordinal);
        Assert.Contains("PromotionOutboxMessageFactory.Create", reconciler, StringComparison.Ordinal);
        Assert.DoesNotContain(".Resume(", reconciler, StringComparison.Ordinal);
        Assert.Contains("public bool PauseWhenCatalogIneligible(", domain, StringComparison.Ordinal);
        Assert.Contains(
            "State is not (SponsoredPlacementState.Scheduled or SponsoredPlacementState.Active)",
            domain,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ScheduledActivationReadsCurrentLocalEligibilityBeforeTransition()
    {
        var repository = RepositoryModel.Load();
        var scheduling = Read(
            repository,
            "src/Promotion/Promotion.Infrastructure/EfPromotionRepository.Scheduling.cs");
        var policy = Read(
            repository,
            "src/Promotion/Promotion.Application/PromotionScheduledPlacementPolicy.cs");

        Assert.Contains("GetEligibilityAsync(", scheduling, StringComparison.Ordinal);
        Assert.Contains(
            "PromotionScheduledPlacementPolicy.Synchronize(",
            scheduling,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "placement.SynchronizeTime(placement.AggregateRevision, nowUtc)",
            scheduling,
            StringComparison.Ordinal);
        Assert.Contains(
            "catalog eligibility projection is unavailable at scheduled activation",
            policy,
            StringComparison.Ordinal);
        Assert.Contains(
            "entitlement.IsEffectiveAt(changedAtUtc)",
            policy,
            StringComparison.Ordinal);
        Assert.Contains(
            "placement.PauseWhenCatalogIneligible(",
            policy,
            StringComparison.Ordinal);
        Assert.DoesNotContain(".Resume(", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void SchedulerCommitsIndependentItemsAndLocksEligibilityBeforeSnapshot()
    {
        var repository = RepositoryModel.Load();
        var scheduling = Read(
            repository,
            "src/Promotion/Promotion.Infrastructure/EfPromotionRepository.Scheduling.cs");

        Assert.Contains("SynchronizeEntitlementAsync(", scheduling, StringComparison.Ordinal);
        Assert.Contains("SynchronizePlacementAsync(", scheduling, StringComparison.Ordinal);
        Assert.Contains("ExecuteInTransactionAsync(async", scheduling, StringComparison.Ordinal);
        Assert.Contains(
            "pg_advisory_lock(hashtextextended",
            scheduling,
            StringComparison.Ordinal);
        Assert.Contains(
            "pg_advisory_unlock(hashtextextended",
            scheduling,
            StringComparison.Ordinal);
        Assert.Contains("OpenConnectionAsync", scheduling, StringComparison.Ordinal);
        Assert.Contains("CloseConnectionAsync", scheduling, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE", scheduling, StringComparison.Ordinal);
        Assert.Contains("FOR SHARE", scheduling, StringComparison.Ordinal);
        Assert.Contains(
            "await _dbContext.SaveChangesAsync(innerCancellationToken)",
            scheduling,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "BeginTransactionAsync(\n            IsolationLevel.Serializable",
            scheduling,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PromotionWorkerHasNoCatalogDatabaseCredential()
    {
        var repository = RepositoryModel.Load();
        var compose = Read(repository, "compose.yaml");
        var start = compose.IndexOf("  promotion-worker:", StringComparison.Ordinal);
        var end = compose.IndexOf("\n  reverse-proxy:", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "Promotion worker service block was not found.");
        var block = compose[start..end];

        Assert.Contains("ConnectionStrings__Promotion:", block, StringComparison.Ordinal);
        Assert.Contains("Messaging__BrokerUri:", block, StringComparison.Ordinal);
        Assert.Contains("PromotionWorker__SystemActorId:", block, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionStrings__Catalog", block, StringComparison.Ordinal);
        Assert.DoesNotContain("catalog_db", block, StringComparison.Ordinal);
    }

    private static HashSet<string> ReadProjectReferences(
        RepositoryModel repository,
        string relativePath)
    {
        var project = XDocument.Load(Path.Combine(
            repository.Root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return project
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value?.Replace('\\', '/'))
            .Where(value => value is not null)
            .Select(value => value!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string Read(RepositoryModel repository, string relativePath) =>
        File.ReadAllText(Path.Combine(
            repository.Root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
