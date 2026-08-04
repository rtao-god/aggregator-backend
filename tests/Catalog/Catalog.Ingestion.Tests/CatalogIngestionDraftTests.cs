using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Catalog.Ingestion.Tests;

public sealed class CatalogIngestionDraftTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CanonicalDigestMismatchFailsBeforeStoreMutation()
    {
        var store = new RecordingStore();
        var service = new CatalogIngestionDraftService(store);
        var command = CreateCommand() with { CommandDigest = new string('f', 64) };

        var exception = await Assert.ThrowsAsync<CatalogIngestionDraftException>(() =>
            service.ExecuteAsync(command, "ingestion-worker", CancellationToken.None));

        Assert.Equal("CATALOG_INGESTION_COMMAND_DIGEST_MISMATCH", exception.Code);
        Assert.Equal(0, store.CallCount);
    }

    [Fact]
    public async Task ForbiddenEvidenceNeverReachesCatalogDraftStore()
    {
        var store = new RecordingStore();
        var service = new CatalogIngestionDraftService(store);
        var invalid = CreateCommand(
            usagePolicy: "forbidden",
            kind: CatalogDraftValueKindContract.Text);

        var exception = await Assert.ThrowsAsync<CatalogIngestionDraftException>(() =>
            service.ExecuteAsync(invalid, "ingestion-worker", CancellationToken.None));

        Assert.Equal("CATALOG_INGESTION_USAGE_POLICY_BLOCKED", exception.Code);
        Assert.Equal(0, store.CallCount);
    }

    [Fact]
    public async Task ExactDraftCommandReturnsProducerOwnedOutcome()
    {
        var outcome = new CatalogIngestionCommandOutcome(
            Guid.Parse("019ba000-0000-7000-8000-000000000101"),
            Guid.Parse("019ba000-0000-7000-8000-000000000102"),
            "provider-one",
            CatalogIngestionOutcomeStateContract.DraftCreated,
            Guid.Parse("019ba000-0000-7000-8000-000000000103"),
            Guid.Parse("019ba000-0000-7000-8000-000000000104"),
            FailureCode: null,
            FailureDetail: null,
            Now);
        var store = new RecordingStore(outcome);
        var service = new CatalogIngestionDraftService(store);
        var command = CreateCommand();

        var actual = await service.ExecuteAsync(
            command,
            "ingestion-worker",
            CancellationToken.None);

        Assert.Equal(outcome, actual);
        Assert.Equal(1, store.CallCount);
    }

    [Fact]
    public void IngestionServiceAndStoreExposeNoPublicationOperation()
    {
        var methodNames = typeof(CatalogIngestionDraftService)
            .GetMethods()
            .Select(method => method.Name)
            .Concat(typeof(ICatalogIngestionDraftStore).GetMethods().Select(method => method.Name))
            .ToArray();

        Assert.DoesNotContain(methodNames, name =>
            name.Contains("Publish", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ActivatePublication", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Rollback", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("ExecuteAsync", methodNames);
        Assert.Contains("UpsertAsync", methodNames);
    }

    [Fact]
    public void PersistenceModelOwnsDraftNaturalIdentityAndImmutableCommandResult()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var draft = FindTable(model, "catalog_ingestion", "draft_proposal");
        var command = FindTable(model, "catalog_ingestion", "command_result");

        Assert.Contains(
            draft.GetIndexes(),
            index => index.IsUnique &&
                     index.Properties.Select(property => property.Name)
                         .SequenceEqual(["CatalogKey", "EntityKind", "SubjectNaturalKey"]));
        Assert.Contains(
            draft.GetIndexes(),
            index => index.IsUnique &&
                     index.Properties.Select(property => property.Name)
                         .SequenceEqual(["IngestionBatchId", "IngestionItemKey"]));
        Assert.Equal(["CommandId"], command.FindPrimaryKey()!.Properties.Select(item => item.Name));
        Assert.DoesNotContain(
            draft.GetProperties(),
            property => property.Name.Contains("Publication", StringComparison.OrdinalIgnoreCase));
    }

    private static CatalogIngestionUpsertDraftCommand CreateCommand(
        string usagePolicy = "public_allowed",
        CatalogDraftValueKindContract kind = CatalogDraftValueKindContract.Text)
    {
        var input = new CatalogIngestionCommandDigestInput(
            Guid.Parse("019ba000-0000-7000-8000-000000000101"),
            Guid.Parse("019ba000-0000-7000-8000-000000000102"),
            "provider-one",
            "berlin",
            "berlin",
            Guid.Parse("019ba000-0000-7000-8000-000000000105"),
            "provider",
            "provider:one",
            [
                new CatalogDraftFieldValueContract(
                    "name",
                    kind,
                    "Provider One",
                    "en",
                    "source-one",
                    new string('a', 64),
                    usagePolicy),
            ],
            Now);
        return new CatalogIngestionUpsertDraftCommand(
            input.CommandId,
            input.IngestionBatchId,
            input.IngestionItemKey,
            CatalogIngestionCommandDigest.Compute(input),
            input.SiteKey,
            input.CatalogKey,
            input.ExpectedCatalogConfigurationRevisionId,
            input.EntityKind,
            input.SubjectNaturalKey,
            input.Fields,
            input.RequestedAtUtc,
            "ingestion:batch:command");
    }

    private static CatalogIngestionDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CatalogIngestionDbContext>()
            .UseNpgsql("Host=localhost;Database=catalog_db;Username=catalog_app;Password=test")
            .Options;
        return new CatalogIngestionDbContext(options);
    }

    private static IEntityType FindTable(IModel model, string schema, string tableName) =>
        model.GetEntityTypes().Single(entity =>
            string.Equals(entity.GetSchema(), schema, StringComparison.Ordinal) &&
            string.Equals(entity.GetTableName(), tableName, StringComparison.Ordinal));

    private sealed class RecordingStore(CatalogIngestionCommandOutcome? outcome = null)
        : ICatalogIngestionDraftStore
    {
        public int CallCount { get; private set; }

        public Task<CatalogIngestionCommandOutcome> UpsertAsync(
            CatalogIngestionUpsertDraftCommand command,
            string callerIdentity,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(outcome ?? new CatalogIngestionCommandOutcome(
                command.CommandId,
                command.IngestionBatchId,
                command.IngestionItemKey,
                CatalogIngestionOutcomeStateContract.DraftCreated,
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                FailureCode: null,
                FailureDetail: null,
                command.RequestedAtUtc));
        }
    }
}
