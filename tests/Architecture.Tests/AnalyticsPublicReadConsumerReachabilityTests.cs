using System.Xml.Linq;
using Xunit;

namespace Architecture.Tests;

public sealed class AnalyticsPublicReadConsumerReachabilityTests
{
    [Fact]
    public void AnalyticsConsumesOnlyTheProducerOwnedQueryContract()
    {
        var repository = RepositoryModel.Load();
        var applicationReferences = ReadProjectReferences(
            repository,
            "src/Analytics/Analytics.Application/Analytics.Application.csproj");
        var workerReferences = ReadProjectReferences(
            repository,
            "src/Analytics/Analytics.Worker/Analytics.Worker.csproj");

        Assert.Contains(
            "../../Query/Query.Contracts/Query.Contracts.csproj",
            applicationReferences);
        Assert.DoesNotContain(
            applicationReferences,
            reference => reference.Contains("Query.Application", StringComparison.OrdinalIgnoreCase) ||
                reference.Contains("Query.Domain", StringComparison.OrdinalIgnoreCase) ||
                reference.Contains("Query.Infrastructure", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(
            "../../Query/Query.Contracts/Query.Contracts.csproj",
            workerReferences);
        Assert.Contains(
            "../Analytics.Application/Analytics.Application.csproj",
            workerReferences);
        Assert.Contains(
            "../Analytics.Infrastructure/Analytics.Infrastructure.csproj",
            workerReferences);
        Assert.DoesNotContain(
            workerReferences,
            reference => reference.Contains("Query.Infrastructure", StringComparison.OrdinalIgnoreCase) ||
                reference.Contains("Query.Application", StringComparison.OrdinalIgnoreCase) ||
                reference.Contains("Query.Domain", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnalyticsWorkerRegistersTheQueryProjectionConsumer()
    {
        var repository = RepositoryModel.Load();
        var program = Read(repository, "src/Analytics/Analytics.Worker/Program.cs");
        var worker = Read(
            repository,
            "src/Analytics/Analytics.Worker/AnalyticsPublicReadProjectionWorker.cs");
        var infrastructure = Read(
            repository,
            "src/Analytics/Analytics.Infrastructure/AnalyticsInfrastructureServiceCollectionExtensions.cs");

        Assert.Contains("AddAnalyticsApplication()", program, StringComparison.Ordinal);
        Assert.Contains("AddAnalyticsInfrastructure(builder.Configuration)", program, StringComparison.Ordinal);
        Assert.Contains(
            "AddHostedService<AnalyticsPublicReadProjectionWorker>()",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "AnalyticsPublicReadProjectionWorkerOptions.SectionName",
            program,
            StringComparison.Ordinal);

        Assert.Contains(
            "ApplyPublicReadRevisionActivationService",
            worker,
            StringComparison.Ordinal);
        Assert.Contains(
            "QueryIntegrationEventContracts.PublicReadRevisionActivated",
            worker,
            StringComparison.Ordinal);
        Assert.Contains("payload-digest", worker, StringComparison.Ordinal);
        Assert.Contains("causation-id", worker, StringComparison.Ordinal);
        Assert.Contains("BasicAckAsync", worker, StringComparison.Ordinal);
        Assert.Contains("BasicNackAsync", worker, StringComparison.Ordinal);
        Assert.Contains("x-queue-type", worker, StringComparison.Ordinal);
        Assert.Contains("x-delivery-limit", worker, StringComparison.Ordinal);

        Assert.Contains(
            "IPublicReadActivationProjectionStore,",
            infrastructure,
            StringComparison.Ordinal);
        Assert.Contains(
            "EfPublicReadActivationProjectionStore",
            infrastructure,
            StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("Query.Api", worker, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyticsPersistenceOwnsInboxCheckpointAndExactPlacementMembership()
    {
        var repository = RepositoryModel.Load();
        var migration = Read(
            repository,
            "src/Analytics/Analytics.Migrations/Migrations/V003__query_public_read_projection.sql");
        var scopeMigration = Read(
            repository,
            "src/Analytics/Analytics.Migrations/Migrations/V004__interaction_placement_scope_key.sql");
        var store = Read(
            repository,
            "src/Analytics/Analytics.Infrastructure/EfPublicReadActivationProjectionStore.cs");
        var interactionRepository = Read(
            repository,
            "src/Analytics/Analytics.Infrastructure/EfAnalyticsRepository.cs");

        Assert.Contains(
            "CREATE TABLE messaging.inbox_message",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TABLE access_projection.public_read_activation_checkpoint",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TABLE access_projection.public_sponsored_placement_reference",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "fk_analytics_interaction_sponsored_placement",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "ux_analytics_public_read_catalog_activation_revision",
            migration,
            StringComparison.Ordinal);

        Assert.Contains(
            "ALTER COLUMN placement_scope_key TYPE varchar(200)",
            scopeMigration,
            StringComparison.Ordinal);

        Assert.Contains(
            "IsolationLevel.Serializable",
            store,
            StringComparison.Ordinal);
        Assert.Contains(
            "pg_advisory_xact_lock",
            store,
            StringComparison.Ordinal);
        Assert.Contains(
            "ANALYTICS_INBOX_MESSAGE_CORRUPT",
            store,
            StringComparison.Ordinal);
        Assert.Contains(
            "ANALYTICS_PUBLIC_ACTIVATION_REVISION_GAP",
            store,
            StringComparison.Ordinal);
        Assert.Contains(
            "ValidateInteractionAsync(",
            interactionRepository,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ValidateMembershipAsync(",
            interactionRepository,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptanceBootstrapUsesTheCanonicalQueryActivationOwner()
    {
        var repository = RepositoryModel.Load();
        var program = Read(
            repository,
            "tests/Acceptance/Acceptance.Analytics.Control/Program.cs");
        var references = ReadProjectReferences(
            repository,
            "tests/Acceptance/Acceptance.Analytics.Control/Acceptance.Analytics.Control.csproj");

        Assert.Contains(
            "ApplyPublicReadRevisionActivationService",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "PublicReadActivationEventFactory.Create(",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "QueryCanonicalJson.ComputeDigest(activationPayload)",
            program,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IPublicReadReferenceProjectionWriter",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "../../../src/Query/Query.Application/Query.Application.csproj",
            references);
        Assert.Contains(
            "../../../src/Query/Query.Domain/Query.Domain.csproj",
            references);
    }

    [Fact]
    public void ComposeWiresTheAnalyticsConsumerWithoutQueryDatabaseCredentials()
    {
        var repository = RepositoryModel.Load();
        var compose = Read(repository, "compose.yaml");
        var workerStart = compose.IndexOf("  analytics-worker:", StringComparison.Ordinal);
        var nextService = compose.IndexOf("\n  promotion-api:", workerStart, StringComparison.Ordinal);
        Assert.True(
            workerStart >= 0 && nextService > workerStart,
            "Analytics worker service block was not found.");
        var workerBlock = compose[workerStart..nextService];

        Assert.Contains(
            "Analytics__PublicReadProjection__BrokerUri:",
            workerBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "Analytics__PublicReadProjection__RoutingKey: query.public-read-revision.activated",
            workerBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "Analytics__PublicReadProjection__DeadLetterQueue:",
            workerBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "rabbitmq: {condition: service_healthy}",
            workerBlock,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ConnectionStrings__Query",
            workerBlock,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Query__", workerBlock, StringComparison.Ordinal);
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
