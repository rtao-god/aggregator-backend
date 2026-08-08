using System.Security.Cryptography;
using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;
using Aggregator.Catalog.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Catalog.Infrastructure.Tests;

public sealed class BerlinProductConfigurationPersistenceTests
{
    private static readonly DateTimeOffset ImportedAtUtc =
        new(2026, 8, 8, 7, 0, 0, TimeSpan.Zero);
    private static readonly Guid ActorId =
        Guid.Parse("019fe000-0000-7000-8000-000000000001");
    private static readonly Guid ExpectedRevisionId =
        Guid.Parse("019fdeab-5c00-7000-8000-000000000001");
    private const string ExpectedSiteKey = "berlin-recording";
    private const string ExpectedCatalogKey = "berlin-recording-services";
    private const string ExpectedContentDigest =
        "c9aed74233be4bd8fba9fd728677f522b27b8e27c82af21924542b5cf1aed941";
    private const string ExpectedValidationResultDigest =
        "393bed7bd10341cf7dff42b5514f57f48090c0e3c3c462e5da3dc4564f2a0f47";

    [Fact]
    public async Task AuthoredBerlinConfigurationImportsPersistsAndActivatesExactly()
    {
        var sourceDirectory = Path.Combine(
            FindRepositoryRoot(),
            "product-config",
            ExpectedSiteKey);
        var request = await CatalogProductConfigurationSourceLoader.LoadAsync(
            sourceDirectory,
            CancellationToken.None);

        Assert.Equal(ExpectedRevisionId, request.Configuration.RevisionId);
        Assert.Equal(ExpectedSiteKey, request.Configuration.Site.Key);
        Assert.Equal(ExpectedCatalogKey, request.Configuration.Catalog.Key);
        Assert.Equal(ExpectedContentDigest, request.ExpectedContentDigest);

        await using var database = await CatalogPostgresTestDatabase.CreateAsync();
        await database.ApplyAllCatalogMigrationsAsync();
        await using var context = database.CreateContext();
        var repository = new EfCatalogRepository(context);
        var service = new CatalogConfigurationService(
            repository,
            new FixedTimeProvider(ImportedAtUtc));
        var actor = CatalogActor.Create(ActorId);

        var imported = await service.ImportAsync(
            request,
            actor,
            CancellationToken.None);

        Assert.Equal(ExpectedRevisionId, imported.RevisionId);
        Assert.Equal(ExpectedSiteKey, imported.SiteKey);
        Assert.Equal(ExpectedCatalogKey, imported.CatalogKey);
        Assert.Equal(ExpectedContentDigest, imported.ContentDigest);
        Assert.Equal(ImportedAtUtc, imported.ImportedAtUtc);
        Assert.False(imported.IsActive);
        await AssertStoredArtifactAsync(database, request);
        Assert.Equal(
            ActorId.ToString("D"),
            await database.ScalarAsync<string>(
                """
                SELECT imported_by_actor_id::text
                FROM catalog.configuration_import_actor
                WHERE configuration_revision_id = @revision_id;
                """,
                new NpgsqlParameter<Guid>("revision_id", ExpectedRevisionId)));
        await AssertStoredValidationResultAsync(database);

        var duplicate = await Assert.ThrowsAsync<CatalogConflictException>(() =>
            service.ImportAsync(request, actor, CancellationToken.None));
        Assert.Contains(ExpectedRevisionId.ToString("D"), duplicate.Message, StringComparison.Ordinal);
        Assert.Contains(ExpectedContentDigest, duplicate.Message, StringComparison.Ordinal);

        var activated = await service.ActivateAsync(
            ExpectedCatalogKey,
            new ActivateProductConfigurationRequest(
                ExpectedRevisionId,
                new ConfigurationPointerExpectationContract(
                    PointerExpectationKindContract.Absent,
                    ConfigurationRevisionId: null)),
            actor,
            CancellationToken.None);

        Assert.True(activated.IsActive);
        Assert.Equal(ExpectedRevisionId, activated.RevisionId);
        Assert.Equal(ExpectedContentDigest, activated.ContentDigest);
        Assert.Equal(ImportedAtUtc, activated.ImportedAtUtc);
        Assert.Equal(
            ExpectedRevisionId.ToString("D"),
            await database.ScalarAsync<string>(
                """
                SELECT configuration_revision_id::text
                FROM catalog.active_configuration
                WHERE catalog_key = @catalog_key;
                """,
                new NpgsqlParameter<string>("catalog_key", ExpectedCatalogKey)));
        Assert.Equal(
            ActorId.ToString("D"),
            await database.ScalarAsync<string>(
                """
                SELECT activated_by_actor_id::text
                FROM catalog.active_configuration
                WHERE catalog_key = @catalog_key;
                """,
                new NpgsqlParameter<string>("catalog_key", ExpectedCatalogKey)));

        var staleExpectation = await Assert.ThrowsAsync<CatalogConflictException>(() =>
            service.ActivateAsync(
                ExpectedCatalogKey,
                new ActivateProductConfigurationRequest(
                    ExpectedRevisionId,
                    new ConfigurationPointerExpectationContract(
                        PointerExpectationKindContract.Absent,
                        ConfigurationRevisionId: null)),
                actor,
                CancellationToken.None));
        Assert.Contains("expected no active configuration", staleExpectation.Message, StringComparison.Ordinal);

        await using var verificationContext = database.CreateContext();
        var verificationRepository = new EfCatalogRepository(verificationContext);
        var activeConfiguration = await verificationRepository.GetActiveConfigurationAsync(
            CatalogKey.Create(ExpectedCatalogKey),
            CancellationToken.None);
        Assert.NotNull(activeConfiguration);
        Assert.Equal(ExpectedRevisionId, activeConfiguration.RevisionId);
        Assert.Equal(ExpectedContentDigest, activeConfiguration.Digest);
    }

    [Fact]
    public async Task ConfigurationRevisionWithoutValidationResultCannotCommit()
    {
        await using var database = await CatalogPostgresTestDatabase.CreateAsync();
        await database.ApplyAllCatalogMigrationsAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            """
            INSERT INTO catalog.configuration_revision
            (
                id,
                site_key,
                catalog_key,
                content_digest,
                canonical_document,
                created_at_utc,
                imported_at_utc
            )
            VALUES
            (
                '019fe000-0000-7000-8000-000000000099',
                'unproven-site',
                'unproven-catalog',
                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                decode('7b7d', 'hex'),
                @timestamp,
                @timestamp
            );
            """,
            UtcParameter("timestamp", ImportedAtUtc)));

        Assert.Equal("P7113", exception.SqlState);
        Assert.Contains(
            "no matching owner validation result",
            exception.MessageText,
            StringComparison.Ordinal);
        Assert.Equal(
            0L,
            await database.ScalarAsync<long>(
                """
                SELECT count(*)
                FROM catalog.configuration_revision
                WHERE id = '019fe000-0000-7000-8000-000000000099';
                """));
    }

    private static async Task AssertStoredArtifactAsync(
        CatalogPostgresTestDatabase database,
        ImportProductConfigurationRequest request)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                site_key,
                catalog_key,
                content_digest,
                canonical_document,
                imported_at_utc
            FROM catalog.configuration_revision
            WHERE id = @revision_id;
            """;
        command.Parameters.Add(
            new NpgsqlParameter<Guid>(
                "revision_id",
                request.Configuration.RevisionId));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(ExpectedSiteKey, reader.GetString(0));
        Assert.Equal(ExpectedCatalogKey, reader.GetString(1));
        Assert.Equal(ExpectedContentDigest, reader.GetString(2));
        var canonicalDocument = reader.GetFieldValue<byte[]>(3);
        var storedDocumentDigest = Convert
            .ToHexString(SHA256.HashData(canonicalDocument))
            .ToLowerInvariant();
        Assert.Equal(ExpectedContentDigest, storedDocumentDigest);
        Assert.Equal(ImportedAtUtc, reader.GetFieldValue<DateTimeOffset>(4));
        Assert.False(await reader.ReadAsync());
    }

    private static async Task AssertStoredValidationResultAsync(
        CatalogPostgresTestDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                contract_identity,
                contract_revision,
                content_digest,
                validation_state,
                result_digest,
                validated_at_utc
            FROM catalog.configuration_validation_result
            WHERE configuration_revision_id = @revision_id;
            """;
        command.Parameters.Add(
            new NpgsqlParameter<Guid>("revision_id", ExpectedRevisionId));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(
            CatalogProductConfigurationValidationContract.Identity,
            reader.GetString(0));
        Assert.Equal(
            CatalogProductConfigurationValidationContract.Revision,
            reader.GetInt32(1));
        Assert.Equal(ExpectedContentDigest, reader.GetString(2));
        Assert.Equal((short)ProductConfigurationValidationState.Validated, reader.GetInt16(3));
        Assert.Equal(ExpectedValidationResultDigest, reader.GetString(4));
        Assert.Equal(ImportedAtUtc, reader.GetFieldValue<DateTimeOffset>(5));
        Assert.False(await reader.ReadAsync());
    }

    private static NpgsqlParameter UtcParameter(
        string name,
        DateTimeOffset value) =>
        new(name, NpgsqlDbType.TimestampTz)
        {
            Value = value,
        };

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AggregatorBackend.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root could not be located.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }
}
