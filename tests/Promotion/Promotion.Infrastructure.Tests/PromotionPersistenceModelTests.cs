using Aggregator.Promotion.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Promotion.Infrastructure.Tests;

public sealed class PromotionPersistenceModelTests
{
    [Fact]
    public void ProductAndPlacementRevisionsAreSeparateImmutableOwners()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var product = FindTable(model, "products", "promotion_product");
        var productRevision = FindTable(model, "products", "promotion_product_revision");
        var placement = FindTable(model, "placements", "sponsored_placement");
        var placementRevision = FindTable(model, "placements", "sponsored_placement_revision");

        Assert.True(product.FindProperty("AggregateRevision")?.IsConcurrencyToken);
        Assert.True(placement.FindProperty("AggregateRevision")?.IsConcurrencyToken);
        Assert.Contains(
            productRevision.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(property => property.Name)
                    .SequenceEqual(["ProductId", "RevisionNumber"]));
        Assert.Contains(
            placementRevision.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(property => property.Name)
                    .SequenceEqual(["PlacementId", "RevisionNumber"]));
    }

    [Fact]
    public void CapacityProjectionScopesExactSlotLocaleAndWindow()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var capacity = FindTable(model, "placements", "sponsored_placement_capacity");
        var primaryKey = capacity.FindPrimaryKey()
            ?? throw new InvalidOperationException("Promotion capacity primary key is missing.");
        var checkNames = capacity.GetCheckConstraints()
            .Select(check => check.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            ["PlacementId", "Locale"],
            primaryKey.Properties.Select(property => property.Name).ToArray());
        Assert.Contains("ck_sponsored_capacity_scope", checkNames);
        Assert.Contains("ck_sponsored_capacity_slot", checkNames);
        Assert.Contains("ck_sponsored_capacity_window", checkNames);
        Assert.Contains("ck_sponsored_capacity_state", checkNames);
        Assert.All(
            capacity.GetForeignKeys(),
            foreignKey => Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior));
    }

    [Fact]
    public void EligibilityAndCommandResultsCarryRevisionAndDigestProof()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var eligibility = FindTable(
            model,
            "access_projection",
            "listing_eligibility_projection");
        var command = FindTable(model, "operations", "command_result");
        var commandPrimaryKey = command.FindPrimaryKey()
            ?? throw new InvalidOperationException("Promotion command-result primary key is missing.");

        Assert.True(eligibility.FindProperty("SourceRevision")?.IsConcurrencyToken);
        Assert.Contains(
            eligibility.GetCheckConstraints(),
            check => string.Equals(
                check.Name,
                "ck_promotion_eligibility_state",
                StringComparison.Ordinal));
        Assert.Equal(
            ["Scope", "IdempotencyKey"],
            commandPrimaryKey.Properties.Select(property => property.Name).ToArray());
        var commandChecks = command.GetCheckConstraints()
            .Select(check => check.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("ck_promotion_command_request_digest", commandChecks);
        Assert.Contains("ck_promotion_command_result_digest", commandChecks);
    }

    [Fact]
    public void OutboxShapeMatchesSharedDispatcherContract()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var outbox = FindTable(model, "messaging", "outbox_message");
        var properties = outbox.GetProperties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("MessageId", properties);
        Assert.Contains("RoutingKey", properties);
        Assert.Contains("ContractIdentity", properties);
        Assert.Contains("PayloadJson", properties);
        Assert.Contains("PayloadDigest", properties);
        Assert.Contains("OccurredAtUtc", properties);
        Assert.Contains("CorrelationId", properties);
        Assert.Contains("DeliveryAttempts", properties);
        Assert.Contains("DispatchedAtUtc", properties);
        Assert.Contains("DeadLetteredAtUtc", properties);
        var payload = outbox.FindProperty("PayloadJson")
            ?? throw new InvalidOperationException("Promotion outbox payload property is missing.");
        Assert.Equal("text", payload.GetColumnType());
    }

    private static PromotionDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PromotionDbContext>()
            .UseNpgsql("Host=localhost;Database=promotion_db;Username=promotion_app;Password=test")
            .Options;
        return new PromotionDbContext(options);
    }

    private static IEntityType FindTable(IModel model, string schema, string tableName) =>
        model.GetEntityTypes().Single(entity =>
            string.Equals(entity.GetSchema(), schema, StringComparison.Ordinal) &&
            string.Equals(entity.GetTableName(), tableName, StringComparison.Ordinal));
}
