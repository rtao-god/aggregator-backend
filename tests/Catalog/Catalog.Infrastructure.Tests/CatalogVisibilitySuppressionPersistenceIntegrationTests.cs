using System.Security.Cryptography;
using System.Text;
using Aggregator.Catalog.Application;
using Aggregator.Catalog.Domain;
using Aggregator.Catalog.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Catalog.Infrastructure.Tests;

public sealed class CatalogVisibilitySuppressionPersistenceIntegrationTests
{
    private static readonly CatalogKey CatalogKey =
        Aggregator.Catalog.Domain.CatalogKey.Create("berlin-recording-services");
    private static readonly DateTimeOffset StartedAtUtc =
        new(2026, 8, 6, 9, 30, 0, TimeSpan.Zero);
    private static readonly Guid ActorId =
        Guid.Parse("0198fc00-0000-7000-8000-000000000001");

    [Fact]
    public async Task CreateAndResolvePersistExactHistoryAndOutboxPayloads()
    {
        await using var database = await CatalogPostgresTestDatabase.CreateAsync();
        await database.ApplyAllCatalogMigrationsAsync();
        await using var context = database.CreateContext();
        var repository = new PostgresCatalogVisibilitySuppressionRepository(context);
        var requested = CreateRequested(Guid.Parse("0198fc00-0000-7000-8000-000000000010"));
        var active = requested.Activate(
            requested.Revision,
            ActorId,
            "Emergency route removal accepted.",
            StartedAtUtc);
        var activeOutbox = CreateOutbox(
            Guid.Parse("0198fc00-0000-7000-8000-000000000011"),
            active.State,
            active.Revision,
            StartedAtUtc);

        await repository.CreateActiveAsync(
            requested,
            active,
            activeOutbox,
            CancellationToken.None);

        Assert.Equal(
            2L,
            await database.ScalarAsync<long>(
                "SELECT revision FROM catalog.public_visibility_suppression WHERE id = @id;",
                IdParameter(active.Id)));
        Assert.Equal(
            2,
            await database.ScalarAsync<int>(
                "SELECT state FROM catalog.public_visibility_suppression WHERE id = @id;",
                IdParameter(active.Id)));
        Assert.Equal(
            2L,
            await database.ScalarAsync<long>(
                "SELECT count(*) FROM catalog.public_visibility_suppression_revision WHERE suppression_id = @id;",
                IdParameter(active.Id)));
        Assert.Equal(
            activeOutbox.Payload,
            await database.ScalarAsync<string>(
                "SELECT payload_json FROM catalog.outbox_message WHERE message_id = @id;",
                IdParameter(activeOutbox.Id)));
        Assert.Equal(
            activeOutbox.PayloadDigest,
            await database.ScalarAsync<string>(
                "SELECT payload_digest FROM catalog.outbox_message WHERE message_id = @id;",
                IdParameter(activeOutbox.Id)));

        var restored = await repository.GetAsync(active.Id, CancellationToken.None);
        Assert.NotNull(restored);
        Assert.Equal(active.Id, restored.Id);
        Assert.Equal(active.Target, restored.Target);
        Assert.Equal(active.PrivateEvidenceReference, restored.PrivateEvidenceReference);
        Assert.Equal(PublicVisibilitySuppressionState.Active, restored.State);

        var resolvedAtUtc = StartedAtUtc.AddMinutes(5);
        var resolved = active.Resolve(
            active.Revision,
            ActorId,
            "Replacement publication removed the route.",
            resolvedAtUtc);
        var resolvedOutbox = CreateOutbox(
            Guid.Parse("0198fc00-0000-7000-8000-000000000012"),
            resolved.State,
            resolved.Revision,
            resolvedAtUtc);

        await repository.ResolveAsync(
            resolved,
            resolvedOutbox,
            CancellationToken.None);

        Assert.Equal(
            3L,
            await database.ScalarAsync<long>(
                "SELECT revision FROM catalog.public_visibility_suppression WHERE id = @id;",
                IdParameter(resolved.Id)));
        Assert.Equal(
            3,
            await database.ScalarAsync<int>(
                "SELECT state FROM catalog.public_visibility_suppression WHERE id = @id;",
                IdParameter(resolved.Id)));
        Assert.Equal(
            3L,
            await database.ScalarAsync<long>(
                "SELECT count(*) FROM catalog.public_visibility_suppression_revision WHERE suppression_id = @id;",
                IdParameter(resolved.Id)));
        Assert.Equal(
            2L,
            await database.ScalarAsync<long>(
                "SELECT count(*) FROM catalog.outbox_message;"));
        Assert.Equal(
            resolvedOutbox.Payload,
            await database.ScalarAsync<string>(
                "SELECT payload_json FROM catalog.outbox_message WHERE message_id = @id;",
                IdParameter(resolvedOutbox.Id)));
    }

    [Fact]
    public async Task CreateRollsBackAggregateAndHistoryWhenOutboxIdentityConflicts()
    {
        await using var database = await CatalogPostgresTestDatabase.CreateAsync();
        await database.ApplyAllCatalogMigrationsAsync();
        await using var context = database.CreateContext();
        var repository = new PostgresCatalogVisibilitySuppressionRepository(context);
        var requested = CreateRequested(Guid.Parse("0198fc00-0000-7000-8000-000000000020"));
        var active = requested.Activate(
            requested.Revision,
            ActorId,
            "Emergency route removal accepted.",
            StartedAtUtc);
        var outbox = CreateOutbox(
            Guid.Parse("0198fc00-0000-7000-8000-000000000021"),
            active.State,
            active.Revision,
            StartedAtUtc);
        await InsertOutboxAsync(database, outbox);

        await Assert.ThrowsAsync<CatalogConflictException>(() =>
            repository.CreateActiveAsync(
                requested,
                active,
                outbox,
                CancellationToken.None));

        Assert.Equal(
            0L,
            await database.ScalarAsync<long>(
                "SELECT count(*) FROM catalog.public_visibility_suppression WHERE id = @id;",
                IdParameter(active.Id)));
        Assert.Equal(
            0L,
            await database.ScalarAsync<long>(
                "SELECT count(*) FROM catalog.public_visibility_suppression_revision WHERE suppression_id = @id;",
                IdParameter(active.Id)));
        Assert.Equal(
            1L,
            await database.ScalarAsync<long>("SELECT count(*) FROM catalog.outbox_message;"));
    }

    [Fact]
    public async Task ResolveRollsBackCurrentStateAndRevisionWhenOutboxIdentityConflicts()
    {
        await using var database = await CatalogPostgresTestDatabase.CreateAsync();
        await database.ApplyAllCatalogMigrationsAsync();
        await using var context = database.CreateContext();
        var repository = new PostgresCatalogVisibilitySuppressionRepository(context);
        var requested = CreateRequested(Guid.Parse("0198fc00-0000-7000-8000-000000000030"));
        var active = requested.Activate(
            requested.Revision,
            ActorId,
            "Emergency route removal accepted.",
            StartedAtUtc);
        await repository.CreateActiveAsync(
            requested,
            active,
            CreateOutbox(
                Guid.Parse("0198fc00-0000-7000-8000-000000000031"),
                active.State,
                active.Revision,
                StartedAtUtc),
            CancellationToken.None);

        var resolvedAtUtc = StartedAtUtc.AddMinutes(5);
        var resolved = active.Resolve(
            active.Revision,
            ActorId,
            "Replacement publication removed the route.",
            resolvedAtUtc);
        var conflictingOutbox = CreateOutbox(
            Guid.Parse("0198fc00-0000-7000-8000-000000000032"),
            resolved.State,
            resolved.Revision,
            resolvedAtUtc);
        await InsertOutboxAsync(database, conflictingOutbox);

        await Assert.ThrowsAsync<CatalogConflictException>(() =>
            repository.ResolveAsync(
                resolved,
                conflictingOutbox,
                CancellationToken.None));

        Assert.Equal(
            2L,
            await database.ScalarAsync<long>(
                "SELECT revision FROM catalog.public_visibility_suppression WHERE id = @id;",
                IdParameter(active.Id)));
        Assert.Equal(
            2,
            await database.ScalarAsync<int>(
                "SELECT state FROM catalog.public_visibility_suppression WHERE id = @id;",
                IdParameter(active.Id)));
        Assert.Equal(
            2L,
            await database.ScalarAsync<long>(
                "SELECT count(*) FROM catalog.public_visibility_suppression_revision WHERE suppression_id = @id;",
                IdParameter(active.Id)));
    }

    [Fact]
    public async Task SecondResolveOfTheSameRevisionIsRejectedByDatabaseConcurrency()
    {
        await using var database = await CatalogPostgresTestDatabase.CreateAsync();
        await database.ApplyAllCatalogMigrationsAsync();
        await using var context = database.CreateContext();
        var repository = new PostgresCatalogVisibilitySuppressionRepository(context);
        var requested = CreateRequested(Guid.Parse("0198fc00-0000-7000-8000-000000000040"));
        var active = requested.Activate(
            requested.Revision,
            ActorId,
            "Emergency route removal accepted.",
            StartedAtUtc);
        await repository.CreateActiveAsync(
            requested,
            active,
            CreateOutbox(
                Guid.Parse("0198fc00-0000-7000-8000-000000000041"),
                active.State,
                active.Revision,
                StartedAtUtc),
            CancellationToken.None);
        var resolved = active.Resolve(
            active.Revision,
            ActorId,
            "Replacement publication removed the route.",
            StartedAtUtc.AddMinutes(5));
        await repository.ResolveAsync(
            resolved,
            CreateOutbox(
                Guid.Parse("0198fc00-0000-7000-8000-000000000042"),
                resolved.State,
                resolved.Revision,
                resolved.ChangedAtUtc),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<CatalogSuppressionConcurrencyException>(() =>
            repository.ResolveAsync(
                resolved,
                CreateOutbox(
                    Guid.Parse("0198fc00-0000-7000-8000-000000000043"),
                    resolved.State,
                    resolved.Revision,
                    resolved.ChangedAtUtc),
                CancellationToken.None));

        Assert.Equal(2L, exception.ExpectedRevision);
        Assert.Equal(3L, exception.ActualRevision);
        Assert.Equal(
            2L,
            await database.ScalarAsync<long>("SELECT count(*) FROM catalog.outbox_message;"));
    }

    private static PublicVisibilitySuppression CreateRequested(Guid id) =>
        PublicVisibilitySuppression.Request(
            id,
            CatalogKey,
            PublicVisibilitySuppressionTarget.Create(
                PublicVisibilitySuppressionTargetKind.Route,
                listingId: null,
                "/de-DE/studios/revoked"),
            "legal-removal",
            "catalog-evidence/visibility/0198fc00",
            PublicVisibilitySuppressionResponseMode.Gone,
            StartedAtUtc,
            expiresAtUtc: null,
            ActorId,
            "Emergency route removal requested.",
            StartedAtUtc);

    private static CatalogOutboxMessage CreateOutbox(
        Guid messageId,
        PublicVisibilitySuppressionState state,
        long revision,
        DateTimeOffset occurredAtUtc)
    {
        var payload = $$"""
            {"eventId":"{{messageId:D}}","state":"{{state.ToString().ToLowerInvariant()}}","revision":{{revision}}}
            """;
        var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant();
        return new CatalogOutboxMessage(
            messageId,
            "catalog.public-visibility-suppression.changed",
            "aggregator.catalog.public-visibility-suppression-changed@1",
            payload,
            digest,
            occurredAtUtc,
            "corr.catalog-suppression:0001",
            CausationId: null);
    }

    private static Task InsertOutboxAsync(
        CatalogPostgresTestDatabase database,
        CatalogOutboxMessage message) =>
        database.ExecuteAsync(
            """
            INSERT INTO catalog.outbox_message
            (
                message_id,
                routing_key,
                contract_identity,
                payload_json,
                payload_digest,
                occurred_at_utc,
                correlation_id,
                causation_id
            )
            VALUES
            (
                @message_id,
                @routing_key,
                @contract_identity,
                @payload_json,
                @payload_digest,
                @occurred_at_utc,
                @correlation_id,
                @causation_id
            );
            """,
            new NpgsqlParameter<Guid>("message_id", message.Id),
            new NpgsqlParameter<string>("routing_key", message.EventType),
            new NpgsqlParameter<string>("contract_identity", message.ContractIdentity),
            new NpgsqlParameter<string>("payload_json", message.Payload),
            new NpgsqlParameter<string>("payload_digest", message.PayloadDigest),
            new NpgsqlParameter("occurred_at_utc", NpgsqlDbType.TimestampTz)
            {
                Value = message.OccurredAtUtc,
            },
            new NpgsqlParameter<string>("correlation_id", message.CorrelationId),
            new NpgsqlParameter("causation_id", NpgsqlDbType.Uuid)
            {
                Value = message.CausationId is { } causationId ? causationId : DBNull.Value,
            });

    private static NpgsqlParameter<Guid> IdParameter(Guid id) => new("id", id);
}
