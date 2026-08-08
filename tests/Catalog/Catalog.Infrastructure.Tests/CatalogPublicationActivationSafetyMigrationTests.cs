using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;
using Aggregator.Catalog.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Catalog.Infrastructure.Tests;

public sealed class CatalogPublicationActivationSafetyMigrationTests
{
    private static readonly Guid ConfigurationRevisionId =
        Guid.Parse("0198fe00-0000-7000-8000-000000000001");
    private static readonly Guid ListingId =
        Guid.Parse("0198fe00-0000-7000-8000-000000000002");
    private static readonly Guid ListingRevisionId =
        Guid.Parse("0198fe00-0000-7000-8000-000000000003");
    private static readonly Guid TargetPublicationId =
        Guid.Parse("0198fe00-0000-7000-8000-000000000004");
    private static readonly Guid CurrentPublicationId =
        Guid.Parse("0198fe00-0000-7000-8000-000000000005");
    private static readonly Guid ActorId =
        Guid.Parse("0198fe00-0000-7000-8000-000000000006");
    private static readonly Guid ActivationEventId =
        Guid.Parse("0198fe00-0000-7000-8000-000000000007");
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 7, 16, 0, 0, TimeSpan.Zero);
    private const string EmptyJsonDigest =
        "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a";

    [Fact]
    public async Task ActiveListingSuppressionBlocksRollbackActivation()
    {
        await using var database = await CatalogPostgresTestDatabase.CreateAsync();
        await database.ApplyAllCatalogMigrationsAsync();
        await SeedPublicationsAsync(database);
        await InsertActiveListingSuppressionAsync(database);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            """
            UPDATE catalog.current_publication
            SET publication_id = @target_publication_id,
                publication_sequence = 1,
                activated_at_utc = @activated_at_utc,
                activated_by_actor_id = @actor_id
            WHERE catalog_key = 'berlin-recording-services';
            """,
            new NpgsqlParameter<Guid>("target_publication_id", TargetPublicationId),
            UtcParameter("activated_at_utc", Timestamp.AddMinutes(1)),
            new NpgsqlParameter<Guid>("actor_id", ActorId)));

        Assert.Contains(
            "blocked by an active public visibility suppression",
            exception.MessageText,
            StringComparison.Ordinal);
        Assert.Equal(
            CurrentPublicationId.ToString("D"),
            await database.ScalarAsync<string>(
                """
                SELECT publication_id::text
                FROM catalog.current_publication
                WHERE catalog_key = 'berlin-recording-services';
                """));
    }

    [Fact]
    public async Task PointerSequenceMustMatchExactPublicationIdentity()
    {
        await using var database = await CatalogPostgresTestDatabase.CreateAsync();
        await database.ApplyAllCatalogMigrationsAsync();
        await SeedPublicationsAsync(database);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            """
            UPDATE catalog.current_publication
            SET publication_id = @target_publication_id,
                publication_sequence = 99,
                activated_at_utc = @activated_at_utc,
                activated_by_actor_id = @actor_id
            WHERE catalog_key = 'berlin-recording-services';
            """,
            new NpgsqlParameter<Guid>("target_publication_id", TargetPublicationId),
            UtcParameter("activated_at_utc", Timestamp.AddMinutes(1)),
            new NpgsqlParameter<Guid>("actor_id", ActorId)));

        Assert.Contains(
            "does not match its pointer identity",
            exception.MessageText,
            StringComparison.Ordinal);
        Assert.Equal(
            CurrentPublicationId.ToString("D"),
            await database.ScalarAsync<string>(
                """
                SELECT publication_id::text
                FROM catalog.current_publication
                WHERE catalog_key = 'berlin-recording-services';
                """));
    }

    [Fact]
    public async Task FailedActivationRollsBackPointerOutboxAndActivationRevision()
    {
        await using var database = await CatalogPostgresTestDatabase.CreateAsync();
        await database.ApplyAllCatalogMigrationsAsync();
        await SeedPublicationsAsync(database);
        await InsertActiveListingSuppressionAsync(database);
        var observedActivationRevisions = new List<long>();
        var target = CreateTargetPublication();
        var pointer = CurrentPublicationPointer.Create(
            target.CatalogKey,
            target.Id,
            target.Sequence,
            Timestamp.AddMinutes(1),
            ActorId);

        CatalogOutboxMessage CreateOutbox(long activationRevision)
        {
            observedActivationRevisions.Add(activationRevision);
            return new CatalogOutboxMessage(
                ActivationEventId,
                CatalogIntegrationEventTypes.PublicationActivated,
                CatalogIntegrationEventContracts.PublicationActivated,
                "{}",
                EmptyJsonDigest,
                Timestamp.AddMinutes(1),
                "corr.catalog-activation:0001",
                CausationId: null);
        }

        await using (var context = database.CreateContext())
        {
            var repository = new EfCatalogRepository(context);
            var exception = await Assert.ThrowsAsync<CatalogPublicationActivationBlockedException>(() =>
                repository.ActivateExistingPublicationAsync(
                    target,
                    CurrentPublicationId,
                    pointer,
                    CreateOutbox,
                    CancellationToken.None));

            Assert.Equal(
                CatalogPublicationActivationBlockReason.PublicVisibilitySuppression,
                exception.Reason);
        }

        Assert.Equal([1L], observedActivationRevisions);
        Assert.Equal(
            CurrentPublicationId.ToString("D"),
            await ReadCurrentPublicationIdAsync(database));
        Assert.Equal(
            0L,
            await database.ScalarAsync<long>(
                "SELECT count(*) FROM catalog.outbox_message;"));
        Assert.Equal(
            0L,
            await database.ScalarAsync<long>(
                """
                SELECT count(*)
                FROM catalog.publication_activation_sequence
                WHERE catalog_key = 'berlin-recording-services';
                """));

        await database.ExecuteAsync(
            """
            UPDATE catalog.public_visibility_suppression
            SET state = 3,
                revision = 3,
                transition_reason = 'Replacement publication is ready.',
                changed_at_utc = @resolved_at_utc
            WHERE id = '0198fe00-0000-7000-8000-000000000020';
            """,
            UtcParameter("resolved_at_utc", Timestamp.AddMinutes(2)));

        await using (var context = database.CreateContext())
        {
            var repository = new EfCatalogRepository(context);
            await repository.ActivateExistingPublicationAsync(
                target,
                CurrentPublicationId,
                CurrentPublicationPointer.Create(
                    target.CatalogKey,
                    target.Id,
                    target.Sequence,
                    Timestamp.AddMinutes(3),
                    ActorId),
                CreateOutbox,
                CancellationToken.None);
        }

        Assert.Equal([1L, 1L], observedActivationRevisions);
        Assert.Equal(
            TargetPublicationId.ToString("D"),
            await ReadCurrentPublicationIdAsync(database));
        Assert.Equal(
            1L,
            await database.ScalarAsync<long>(
                "SELECT count(*) FROM catalog.outbox_message;"));
        Assert.Equal(
            2L,
            await database.ScalarAsync<long>(
                """
                SELECT next_revision
                FROM catalog.publication_activation_sequence
                WHERE catalog_key = 'berlin-recording-services';
                """));
    }

    private static CatalogPublication CreateTargetPublication() =>
        CatalogPublication.Create(
            TargetPublicationId,
            CatalogKey.Create("berlin-recording-services"),
            ConfigurationRevisionId,
            sequence: 1,
            "catalog/berlin-recording-services/publications/target.json",
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            [
                PublicationEntry.Create(
                    ListingId,
                    ListingRevisionId,
                    Guid.Parse("0198fe00-0000-7000-8000-000000000011"),
                    "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            ],
            ActorId,
            Timestamp);

    private static Task<string> ReadCurrentPublicationIdAsync(
        CatalogPostgresTestDatabase database) =>
        database.ScalarAsync<string>(
            """
            SELECT publication_id::text
            FROM catalog.current_publication
            WHERE catalog_key = 'berlin-recording-services';
            """);

    private static async Task SeedPublicationsAsync(CatalogPostgresTestDatabase database)
    {
        await database.ExecuteAsync(
            """
            INSERT INTO catalog.configuration_revision
            (
                id,
                site_key,
                catalog_key,
                content_digest,
                canonical_document,
                created_at_utc,
                imported_at_utc,
                validation_contract_identity,
                validation_contract_revision,
                validation_state,
                validation_result_digest,
                validated_at_utc,
                validated_by_actor_id
            )
            VALUES
            (
                @configuration_revision_id,
                'berlin-recording',
                'berlin-recording-services',
                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                decode('7b7d', 'hex'),
                @timestamp,
                @timestamp,
                'aggregator-catalog-product-configuration-validation',
                1,
                1,
                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                @timestamp,
                @actor_id
            );

            INSERT INTO catalog.listing
            (
                id,
                catalog_key,
                subject_id,
                subject_revision_id,
                subject_kind,
                state,
                version,
                latest_revision_number,
                current_draft_revision_id,
                approved_revision_id,
                published_revision_id,
                created_at_utc,
                updated_at_utc
            )
            VALUES
            (
                @listing_id,
                'berlin-recording-services',
                '0198fe00-0000-7000-8000-000000000010',
                '0198fe00-0000-7000-8000-000000000011',
                2,
                3,
                4,
                1,
                NULL,
                NULL,
                NULL,
                @timestamp,
                @timestamp
            );

            INSERT INTO catalog.listing_revision
            (
                id,
                listing_id,
                revision_number,
                configuration_revision_id,
                subject_id,
                subject_revision_id,
                subject_kind,
                content_digest,
                created_by_actor_id,
                created_at_utc
            )
            VALUES
            (
                @listing_revision_id,
                @listing_id,
                1,
                @configuration_revision_id,
                '0198fe00-0000-7000-8000-000000000010',
                '0198fe00-0000-7000-8000-000000000011',
                2,
                'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                @actor_id,
                @timestamp
            );

            UPDATE catalog.listing
            SET approved_revision_id = @listing_revision_id,
                published_revision_id = @listing_revision_id
            WHERE id = @listing_id;

            INSERT INTO catalog.publication
            (
                id,
                catalog_key,
                configuration_revision_id,
                sequence,
                artifact_key,
                artifact_digest,
                created_by_actor_id,
                created_at_utc
            )
            VALUES
            (
                @target_publication_id,
                'berlin-recording-services',
                @configuration_revision_id,
                1,
                'catalog/berlin-recording-services/publications/target.json',
                'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc',
                @actor_id,
                @timestamp
            ),
            (
                @current_publication_id,
                'berlin-recording-services',
                @configuration_revision_id,
                2,
                'catalog/berlin-recording-services/publications/current.json',
                'dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd',
                @actor_id,
                @timestamp
            );

            INSERT INTO catalog.publication_entry
            (
                publication_id,
                listing_id,
                listing_revision_id,
                subject_revision_id,
                content_digest
            )
            VALUES
            (
                @target_publication_id,
                @listing_id,
                @listing_revision_id,
                '0198fe00-0000-7000-8000-000000000011',
                'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
            ),
            (
                @current_publication_id,
                @listing_id,
                @listing_revision_id,
                '0198fe00-0000-7000-8000-000000000011',
                'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
            );

            INSERT INTO catalog.current_publication
            (
                catalog_key,
                publication_id,
                publication_sequence,
                activated_at_utc,
                activated_by_actor_id
            )
            VALUES
            (
                'berlin-recording-services',
                @current_publication_id,
                2,
                @timestamp,
                @actor_id
            );
            """,
            new NpgsqlParameter<Guid>("configuration_revision_id", ConfigurationRevisionId),
            new NpgsqlParameter<Guid>("listing_id", ListingId),
            new NpgsqlParameter<Guid>("listing_revision_id", ListingRevisionId),
            new NpgsqlParameter<Guid>("target_publication_id", TargetPublicationId),
            new NpgsqlParameter<Guid>("current_publication_id", CurrentPublicationId),
            new NpgsqlParameter<Guid>("actor_id", ActorId),
            UtcParameter("timestamp", Timestamp));
    }

    private static Task InsertActiveListingSuppressionAsync(
        CatalogPostgresTestDatabase database) =>
        database.ExecuteAsync(
            """
            INSERT INTO catalog.public_visibility_suppression
            (
                id,
                catalog_key,
                target_kind,
                listing_id,
                target_key,
                public_reason_class,
                private_evidence_reference,
                response_mode,
                starts_at_utc,
                expires_at_utc,
                state,
                revision,
                changed_by_actor_id,
                transition_reason,
                changed_at_utc
            )
            VALUES
            (
                '0198fe00-0000-7000-8000-000000000020',
                'berlin-recording-services',
                1,
                @listing_id,
                @target_key,
                'legal-removal',
                'catalog/claims/private/evidence-020',
                2,
                @timestamp,
                NULL,
                2,
                2,
                @actor_id,
                'Hide the exact listing while replacement publication is prepared.',
                @timestamp
            );
            """,
            new NpgsqlParameter<Guid>("listing_id", ListingId),
            new NpgsqlParameter<string>("target_key", ListingId.ToString("D")),
            new NpgsqlParameter<Guid>("actor_id", ActorId),
            UtcParameter("timestamp", Timestamp));

    private static NpgsqlParameter UtcParameter(string name, DateTimeOffset value) =>
        new(name, NpgsqlDbType.TimestampTz)
        {
            Value = value,
        };
}
