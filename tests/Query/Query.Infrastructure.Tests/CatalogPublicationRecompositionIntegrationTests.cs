using System.Reflection;
using Aggregator.Catalog.Contracts;
using Aggregator.Query.Application;
using Aggregator.Query.Domain;
using Aggregator.Query.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Query.Infrastructure.Tests;

public sealed class CatalogPublicationRecompositionIntegrationTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 7, 22, 0, 0, TimeSpan.Zero);
    private static readonly Guid ConfigurationRevisionId =
        Guid.Parse("01990500-0000-7000-8000-000000000001");
    private static readonly Guid ListingId =
        Guid.Parse("01990500-0000-7000-8000-000000000002");
    private static readonly Guid ListingRevisionId =
        Guid.Parse("01990500-0000-7000-8000-000000000003");
    private static readonly Guid SubjectId =
        Guid.Parse("01990500-0000-7000-8000-000000000004");
    private static readonly Guid SubjectRevisionId =
        Guid.Parse("01990500-0000-7000-8000-000000000005");
    private static readonly Guid FirstPublicationId =
        Guid.Parse("01990500-0000-7000-8000-000000000010");
    private static readonly Guid SecondPublicationId =
        Guid.Parse("01990500-0000-7000-8000-000000000011");
    private static readonly Guid FirstEventId =
        Guid.Parse("01990500-0000-7000-8000-000000000012");
    private static readonly Guid SecondEventId =
        Guid.Parse("01990500-0000-7000-8000-000000000013");
    private static readonly Guid SuppressionId =
        Guid.Parse("01990500-0000-7000-8000-000000000014");
    private const string CatalogKey = "berlin-recording-services";
    private const string ArtifactDigest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string EventDigest =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task NewCatalogBasePreservesCurrentPromotionAndSafetyComponents()
    {
        await using var database = await QueryPostgresTestDatabase.CreateAsync();
        await database.ApplyAllQueryMigrationsAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddSingleton<IQueryClock>(new FixedQueryClock(Timestamp.AddMinutes(10)));
        services.AddSingleton<IQueryIdFactory, UuidV7TestIdFactory>();
        services.AddSingleton<NpgsqlQueryProjectionStore>();
        services.AddSingleton<IQueryProjectionStore>(provider =>
            provider.GetRequiredService<NpgsqlQueryProjectionStore>());
        services.AddScoped<IPromotionPlacementProjectionStore, PostgresPromotionOverlayProjectionStore>();
        services.AddScoped<IVisibilitySafetyProjectionStore, PostgresVisibilitySafetyProjectionStore>();
        AddProductionProjectionCoordination(services);
        await using var serviceProvider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateScopes = true,
            });
        await using var projectionScope = serviceProvider.CreateAsyncScope();
        var store = projectionScope.ServiceProvider.GetRequiredService<IQueryProjectionStore>();
        var firstActivation = BuildActivation(
            FirstPublicationId,
            FirstEventId,
            publicationSequence: 1,
            activationRevision: 1,
            previousPublicationId: null,
            name: "Studio Alpha",
            builtAtUtc: Timestamp,
            Guid.Parse("01990500-0000-7000-8000-000000000020"),
            Guid.Parse("01990500-0000-7000-8000-000000000021"),
            Guid.Parse("01990500-0000-7000-8000-000000000022"),
            Guid.Parse("01990500-0000-7000-8000-000000000023"));

        var firstResult = await store.ActivateAsync(
            firstActivation,
            new QueryInboxMessage(
                FirstEventId,
                CatalogIntegrationEventTypes.PublicationActivated,
                EventDigest,
                ActivationRevision: 1,
                Timestamp),
            CancellationToken.None);

        Assert.Equal(QueryProjectionActivationDisposition.Activated, firstResult.Disposition);
        var originalPromotionOverlayId = firstResult.PublicReadRevision.PromotionOverlayId;
        Guid currentSafetyOverlayId;
        Guid safetyPublicReadRevisionId;
        await using (var scope = serviceProvider.CreateAsyncScope())
        {
            var visibilityStore = scope.ServiceProvider
                .GetRequiredService<IVisibilitySafetyProjectionStore>();
            var safetyResult = await visibilityStore.ApplyAsync(
                QueryVisibilitySuppression.Create(
                    SuppressionId,
                    CatalogKey,
                    QueryVisibilitySuppressionTargetKind.Route,
                    listingId: null,
                    "/legal-removal",
                    "legal-removal",
                    QueryVisibilitySuppressionResponseMode.Gone,
                    QueryVisibilitySuppressionState.Active,
                    Timestamp.AddMinutes(1),
                    expiresAtUtc: null,
                    aggregateRevision: 2,
                    Timestamp.AddMinutes(1)),
                new VisibilitySuppressionInboxMessage(
                    Guid.Parse("01990500-0000-7000-8000-000000000015"),
                    "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                    Timestamp.AddMinutes(1)),
                CancellationToken.None);
            Assert.Equal(VisibilitySafetyProjectionDisposition.Activated, safetyResult.Disposition);
            Assert.Equal(originalPromotionOverlayId, safetyResult.PublicReadRevision.PromotionOverlayId);
            currentSafetyOverlayId = safetyResult.PublicReadRevision.SafetyOverlayId;
            safetyPublicReadRevisionId = safetyResult.PublicReadRevision.Id;
        }

        var candidatePromotionOverlayId =
            Guid.Parse("01990500-0000-7000-8000-000000000031");
        var candidateSafetyOverlayId =
            Guid.Parse("01990500-0000-7000-8000-000000000032");
        var secondActivation = BuildActivation(
            SecondPublicationId,
            SecondEventId,
            publicationSequence: 2,
            activationRevision: 2,
            FirstPublicationId,
            name: "Studio Alpha Updated",
            builtAtUtc: Timestamp.AddMinutes(2),
            Guid.Parse("01990500-0000-7000-8000-000000000030"),
            candidatePromotionOverlayId,
            candidateSafetyOverlayId,
            Guid.Parse("01990500-0000-7000-8000-000000000033"));

        var secondResult = await store.ActivateAsync(
            secondActivation,
            new QueryInboxMessage(
                SecondEventId,
                CatalogIntegrationEventTypes.PublicationActivated,
                "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
                ActivationRevision: 2,
                Timestamp.AddMinutes(2)),
            CancellationToken.None);

        Assert.Equal(QueryProjectionActivationDisposition.Activated, secondResult.Disposition);
        Assert.Equal(secondActivation.BaseProjection.Id, secondResult.PublicReadRevision.BaseProjectionId);
        Assert.Equal(originalPromotionOverlayId, secondResult.PublicReadRevision.PromotionOverlayId);
        Assert.Equal(currentSafetyOverlayId, secondResult.PublicReadRevision.SafetyOverlayId);
        Assert.Equal(SecondPublicationId, secondResult.PublicReadRevision.SourcePublicationId);
        Assert.NotEqual(secondActivation.PublicReadRevision.Id, secondResult.PublicReadRevision.Id);
        Assert.Equal(
            secondResult.PublicReadRevision.Id.ToString("D"),
            await database.ScalarAsync<string>(
                """
                SELECT public_read_revision_id::text
                FROM projection.current_public_read
                WHERE catalog_key = @catalog_key;
                """,
                new NpgsqlParameter<string>("catalog_key", CatalogKey)));
        Assert.Equal(
            2L,
            await database.ScalarAsync<long>(
                """
                SELECT activation_revision
                FROM projection.current_public_read
                WHERE catalog_key = @catalog_key;
                """,
                new NpgsqlParameter<string>("catalog_key", CatalogKey)));
        Assert.Equal(
            2L,
            await database.ScalarAsync<long>(
                """
                SELECT last_activation_revision
                FROM projection.catalog_activation_checkpoint
                WHERE catalog_key = @catalog_key;
                """,
                new NpgsqlParameter<string>("catalog_key", CatalogKey)));
        Assert.Equal(
            secondResult.PublicReadRevision.Id.ToString("D"),
            await database.ScalarAsync<string>(
                """
                SELECT result_public_read_revision_id::text
                FROM messaging.inbox_message
                WHERE event_id = @event_id;
                """,
                new NpgsqlParameter<Guid>("event_id", SecondEventId)));
        Assert.Equal(
            1L,
            await database.ScalarAsync<long>(
                """
                SELECT count(*)
                FROM projection.visibility_safety_overlay_item
                WHERE overlay_id = @overlay_id
                  AND suppression_id = @suppression_id;
                """,
                new NpgsqlParameter<Guid>("overlay_id", currentSafetyOverlayId),
                new NpgsqlParameter<Guid>("suppression_id", SuppressionId)));
        Assert.Equal(
            0L,
            await database.ScalarAsync<long>(
                """
                SELECT count(*)
                FROM projection.overlay_revision
                WHERE id = ANY(@candidate_overlay_ids);
                """,
                new NpgsqlParameter<Guid[]>("candidate_overlay_ids",
                    [candidatePromotionOverlayId, candidateSafetyOverlayId])));
        Assert.Equal(
            1L,
            await database.ScalarAsync<long>(
                """
                SELECT count(*)
                FROM projection.public_read_revision
                WHERE id = @prior_revision_id
                  AND safety_overlay_id = @safety_overlay_id;
                """,
                new NpgsqlParameter<Guid>("prior_revision_id", safetyPublicReadRevisionId),
                new NpgsqlParameter<Guid>("safety_overlay_id", currentSafetyOverlayId)));
        Assert.Equal(
            0L,
            await database.ScalarAsync<long>(
                """
                SELECT count(*)
                FROM projection.catalog_visibility_block
                WHERE catalog_key = @catalog_key
                  AND block_kind = 'publication_recomposition';
                """,
                new NpgsqlParameter<string>("catalog_key", CatalogKey)));
    }

    private static QueryProjectionActivation BuildActivation(
        Guid publicationId,
        Guid eventId,
        long publicationSequence,
        long activationRevision,
        Guid? previousPublicationId,
        string name,
        DateTimeOffset builtAtUtc,
        Guid baseProjectionId,
        Guid promotionOverlayId,
        Guid safetyOverlayId,
        Guid publicReadRevisionId)
    {
        var producerEvent = new CatalogPublicationActivated(
            eventId,
            publicationId,
            CatalogKey,
            ConfigurationRevisionId,
            publicationSequence,
            activationRevision,
            $"catalog/{CatalogKey}/publications/{publicationId:N}.json",
            ArtifactDigest,
            PublicationActivationKindContract.Publication,
            previousPublicationId,
            builtAtUtc);
        return CatalogPublicationProjectionBuilder.Build(
            producerEvent,
            CreateArtifact(publicationId, publicationSequence, name, builtAtUtc),
            baseProjectionId,
            promotionOverlayId,
            safetyOverlayId,
            publicReadRevisionId,
            builtAtUtc);
    }

    private static CatalogPublicationArtifact CreateArtifact(
        Guid publicationId,
        long publicationSequence,
        string name,
        DateTimeOffset createdAtUtc) =>
        new(
            CatalogPublicationArtifactContract.Identity,
            CatalogPublicationArtifactContract.Revision,
            publicationId,
            CatalogKey,
            "de-DE",
            ["de-DE", "en-GB"],
            ConfigurationRevisionId,
            publicationSequence,
            createdAtUtc,
            [
                new PublicListingDocument(
                    ListingId,
                    ListingRevisionId,
                    SubjectId,
                    SubjectRevisionId,
                    SubjectKindContract.Place,
                    [
                        new PublicLocalizedText(
                            "de-DE",
                            FieldValueStateContract.Observed,
                            name,
                            MissingReason: null,
                            Guid.Parse("01990500-0000-7000-8000-000000000040")),
                    ],
                    Descriptions: [],
                    CategoryKeys: ["recording-studio"],
                    Attributes: [],
                    new PublicGeography(
                        GeographyStateContract.PrimaryMarket,
                        Latitude: 52.520008m,
                        Longitude: 13.404954m,
                        DistrictKey: "mitte",
                        Guid.Parse("01990500-0000-7000-8000-000000000041")),
                    Contacts: [],
                    Media: [],
                    Provenance: [],
                    ContentDigest:
                        "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"),
            ]);

    private static void AddProductionProjectionCoordination(IServiceCollection services)
    {
        var extensionType = typeof(NpgsqlQueryProjectionStore).Assembly.GetType(
            "Aggregator.Query.Infrastructure.QueryProjectionCoordinationServiceCollectionExtensions",
            throwOnError: true)
            ?? throw new InvalidOperationException(
                "Query projection coordination registration owner was not found.");
        var registrationMethod = extensionType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method =>
            {
                var parameters = method.GetParameters();
                return parameters.Length == 1 &&
                       parameters[0].ParameterType == typeof(IServiceCollection) &&
                       method.ReturnType == typeof(IServiceCollection);
            });
        _ = registrationMethod.Invoke(null, [services]);
    }

    private sealed class UuidV7TestIdFactory : IQueryIdFactory
    {
        public Guid Create() => Guid.CreateVersion7();
    }

    private sealed class FixedQueryClock(DateTimeOffset timestamp) : IQueryClock
    {
        public DateTimeOffset GetUtcNow() => timestamp;
    }
}
