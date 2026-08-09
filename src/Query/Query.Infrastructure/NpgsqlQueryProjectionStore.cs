using System.Data;
using Aggregator.Query.Application;
using Aggregator.Query.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Aggregator.Query.Infrastructure;

public sealed class NpgsqlQueryProjectionStore : IQueryProjectionStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IQueryIdFactory _idFactory;

    public NpgsqlQueryProjectionStore(NpgsqlDataSource dataSource)
        : this(dataSource, new UuidV7QueryIdFactory())
    {
    }

    public NpgsqlQueryProjectionStore(
        NpgsqlDataSource dataSource,
        IQueryIdFactory idFactory)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _idFactory = idFactory ?? throw new ArgumentNullException(nameof(idFactory));
    }

    public async Task<QueryProjectionActivationResult> ActivateAsync(
        QueryProjectionActivation activation,
        QueryInboxMessage inboxMessage,
        CancellationToken cancellationToken)
    {
        ValidateInput(activation, inboxMessage);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var existingInbox = await ReadInboxAsync(
            connection,
            transaction,
            inboxMessage.EventId,
            cancellationToken);
        if (existingInbox is not null)
        {
            if (!string.Equals(existingInbox.PayloadDigest, inboxMessage.PayloadDigest, StringComparison.Ordinal))
            {
                throw Failure(
                    "QUERY_INBOX_DIGEST_CONFLICT",
                    409,
                    $"Event '{inboxMessage.EventId}' was already recorded with another payload digest.",
                    "Quarantine the broker message and inspect the producer outbox for corruption.");
            }

            var replayRevision = await LoadPublicReadRevisionAsync(
                connection,
                transaction,
                existingInbox.ResultPublicReadRevisionId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new QueryProjectionActivationResult(
                replayRevision,
                QueryProjectionActivationDisposition.Replayed);
        }

        var sameActivation = await ReadActivationOwnerAsync(
            connection,
            transaction,
            activation.BaseProjection.CatalogKey,
            inboxMessage.ActivationRevision,
            cancellationToken);
        if (sameActivation is not null)
        {
            throw Failure(
                "QUERY_ACTIVATION_REVISION_CONFLICT",
                409,
                $"Catalog '{activation.BaseProjection.CatalogKey}' activation revision '{inboxMessage.ActivationRevision}' belongs to event '{sameActivation.EventId}', not '{inboxMessage.EventId}'.",
                "Quarantine the conflicting event and inspect Catalog activation revision allocation.");
        }

        var checkpoint = await ReadCheckpointAsync(
            connection,
            transaction,
            activation.BaseProjection.CatalogKey,
            cancellationToken);
        if (checkpoint is not null && inboxMessage.ActivationRevision <= checkpoint.LastActivationRevision)
        {
            var currentRevision = await LoadPublicReadRevisionAsync(
                connection,
                transaction,
                checkpoint.CurrentPublicReadRevisionId,
                cancellationToken);
            await InsertInboxAsync(
                connection,
                transaction,
                inboxMessage,
                activation.BaseProjection.CatalogKey,
                "ignored_stale",
                currentRevision.Id,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new QueryProjectionActivationResult(
                currentRevision,
                QueryProjectionActivationDisposition.IgnoredStale);
        }

        var publicReadActivationRevision = await AllocatePublicReadActivationRevisionAsync(
            connection,
            transaction,
            activation.BaseProjection.CatalogKey,
            cancellationToken);
        await InsertBaseProjectionAsync(connection, transaction, activation.BaseProjection, cancellationToken);
        await InsertDocumentsAsync(connection, transaction, activation.BaseProjection, cancellationToken);
        await InsertOverlayAsync(connection, transaction, activation.PromotionOverlay, cancellationToken);
        await InsertOverlayAsync(connection, transaction, activation.SafetyOverlay, cancellationToken);
        await InsertPublicReadRevisionAsync(
            connection,
            transaction,
            activation.PublicReadRevision,
            cancellationToken);
        await UpsertCurrentPointerAsync(
            connection,
            transaction,
            activation.PublicReadRevision,
            publicReadActivationRevision,
            inboxMessage.ReceivedAtUtc,
            cancellationToken);
        await UpsertCheckpointAsync(
            connection,
            transaction,
            activation.PublicReadRevision,
            inboxMessage,
            cancellationToken);
        await InsertInboxAsync(
            connection,
            transaction,
            inboxMessage,
            activation.BaseProjection.CatalogKey,
            "activated",
            activation.PublicReadRevision.Id,
            cancellationToken);
        if (!await HasPendingPublicationRecompositionAsync(
                connection,
                transaction,
                inboxMessage.EventId,
                cancellationToken))
        {
            await QueryPublicReadActivationOutboxWriter.InsertAsync(
                connection,
                transaction,
                activation.PublicReadRevision,
                publicReadActivationRevision,
                inboxMessage.ReceivedAtUtc,
                inboxMessage.CorrelationId,
                inboxMessage.EventId,
                _idFactory,
                cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);

        return new QueryProjectionActivationResult(
            activation.PublicReadRevision,
            QueryProjectionActivationDisposition.Activated);
    }

    private static void ValidateInput(
        QueryProjectionActivation activation,
        QueryInboxMessage inboxMessage)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(activation.BaseProjection);
        ArgumentNullException.ThrowIfNull(activation.PromotionOverlay);
        ArgumentNullException.ThrowIfNull(activation.SafetyOverlay);
        ArgumentNullException.ThrowIfNull(activation.PublicReadRevision);
        ArgumentNullException.ThrowIfNull(inboxMessage);
        if (inboxMessage.EventId == Guid.Empty ||
            string.IsNullOrWhiteSpace(inboxMessage.EventType) ||
            string.IsNullOrWhiteSpace(inboxMessage.CorrelationId) ||
            inboxMessage.CorrelationId.Length > 128 ||
            inboxMessage.ActivationRevision <= 0 ||
            inboxMessage.ReceivedAtUtc.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "QUERY_INBOX_CONTRACT_INVALID",
                500,
                "Query projection store received an invalid inbox contract.",
                "Correct the Query application composition before retrying activation.");
        }

        if (inboxMessage.PayloadDigest.Length != 64 ||
            inboxMessage.PayloadDigest.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw Failure(
                "QUERY_INBOX_DIGEST_INVALID",
                500,
                "Query projection store received an invalid inbox payload digest.",
                "Correct event digest validation before persistence.");
        }

        var catalogKey = activation.BaseProjection.CatalogKey;
        if (!string.Equals(catalogKey, activation.PromotionOverlay.CatalogKey, StringComparison.Ordinal) ||
            !string.Equals(catalogKey, activation.SafetyOverlay.CatalogKey, StringComparison.Ordinal) ||
            !string.Equals(catalogKey, activation.PublicReadRevision.CatalogKey, StringComparison.Ordinal))
        {
            throw Failure(
                "QUERY_ACTIVATION_CATALOG_MISMATCH",
                500,
                "Query projection activation contains components from different catalogs.",
                "Correct the Query projection builder before persistence.");
        }
    }

    private static async Task<InboxState?> ReadInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT payload_digest, result_public_read_revision_id
            FROM messaging.inbox_message
            WHERE event_id = @event_id;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("event_id", eventId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new InboxState(
            reader.GetString(0).TrimEnd(),
            reader.GetGuid(1));
    }

    private static async Task<ActivationOwner?> ReadActivationOwnerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string catalogKey,
        long activationRevision,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT event_id
            FROM messaging.inbox_message
            WHERE catalog_key = @catalog_key
              AND activation_revision = @activation_revision;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", catalogKey));
        command.Parameters.Add(new NpgsqlParameter<long>("activation_revision", activationRevision));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is Guid eventId ? new ActivationOwner(eventId) : null;
    }

    private static async Task<CheckpointState?> ReadCheckpointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string catalogKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT last_activation_revision, current_public_read_revision_id
            FROM projection.catalog_activation_checkpoint
            WHERE catalog_key = @catalog_key
            FOR UPDATE;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", catalogKey));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CheckpointState(reader.GetInt64(0), reader.GetGuid(1));
    }

    private static async Task<long> AllocatePublicReadActivationRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string catalogKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT activation_revision
            FROM projection.current_public_read
            WHERE catalog_key = @catalog_key
            FOR UPDATE;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", catalogKey));
        var current = await command.ExecuteScalarAsync(cancellationToken);
        return current is null or DBNull
            ? 1
            : checked(Convert.ToInt64(
                current,
                System.Globalization.CultureInfo.InvariantCulture) + 1);
    }

    private static async Task<bool> HasPendingPublicationRecompositionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sourceEventId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS
            (
                SELECT 1
                FROM projection.publication_overlay_recomposition
                WHERE source_event_id = @source_event_id
            );
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("source_event_id", sourceEventId));
        return (bool)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw Failure(
                "QUERY_PUBLICATION_RECOMPOSITION_STATE_UNREADABLE",
                500,
                $"Query could not determine whether event '{sourceEventId}' has pending publication recomposition.",
                "Keep the catalog blocked and restore the Query projection owner state."));
    }

    private static async Task<PublicReadRevision> LoadPublicReadRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid revisionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id,
                   catalog_key,
                   base_projection_id,
                   promotion_overlay_id,
                   safety_overlay_id,
                   source_publication_id,
                   created_at_utc,
                   content_digest
            FROM projection.public_read_revision
            WHERE id = @id;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", revisionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw Failure(
                "QUERY_INBOX_RESULT_MISSING",
                500,
                $"Query inbox references missing public read revision '{revisionId}'.",
                "Restore the Query database from owner backup or rebuild it from Catalog publications.");
        }

        return PublicReadRevision.Restore(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.GetGuid(4),
            reader.GetGuid(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetString(7).TrimEnd());
    }

    private static async Task InsertBaseProjectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        QueryBaseProjection projection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO projection.base_projection
            (
                id,
                catalog_key,
                default_locale,
                supported_locales,
                source_publication_id,
                source_publication_digest,
                source_publication_sequence,
                builder_identity,
                created_at_utc,
                content_digest
            )
            VALUES
            (
                @id,
                @catalog_key,
                @default_locale,
                @supported_locales,
                @source_publication_id,
                @source_publication_digest,
                @source_publication_sequence,
                @builder_identity,
                @created_at_utc,
                @content_digest
            );
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", projection.Id));
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", projection.CatalogKey));
        command.Parameters.Add(new NpgsqlParameter<string>("default_locale", projection.LocalePolicy.DefaultLocale));
        command.Parameters.Add(new NpgsqlParameter<string[]>(
            "supported_locales",
            projection.LocalePolicy.SupportedLocales.ToArray()));
        command.Parameters.Add(new NpgsqlParameter<Guid>("source_publication_id", projection.SourcePublicationId));
        command.Parameters.Add(new NpgsqlParameter<string>("source_publication_digest", projection.SourcePublicationDigest));
        command.Parameters.Add(new NpgsqlParameter<long>("source_publication_sequence", projection.SourcePublicationSequence));
        command.Parameters.Add(new NpgsqlParameter<string>("builder_identity", projection.BuilderIdentity));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("created_at_utc", projection.CreatedAtUtc));
        command.Parameters.Add(new NpgsqlParameter<string>("content_digest", projection.ContentDigest));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertDocumentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        QueryBaseProjection projection,
        CancellationToken cancellationToken)
    {
        foreach (var document in projection.Documents)
        {
            await InsertDocumentAsync(connection, transaction, projection.Id, document, cancellationToken);
            foreach (var localization in document.Localizations)
            {
                await InsertLocalizationAsync(
                    connection,
                    transaction,
                    projection.Id,
                    document.ListingId,
                    localization,
                    cancellationToken);
            }

            foreach (var categoryKey in document.CategoryKeys)
            {
                await InsertCategoryAsync(
                    connection,
                    transaction,
                    projection.Id,
                    document.ListingId,
                    categoryKey,
                    cancellationToken);
            }

            foreach (var attribute in document.Attributes)
            {
                await InsertAttributeAsync(
                    connection,
                    transaction,
                    projection.Id,
                    document.ListingId,
                    attribute,
                    cancellationToken);
            }

            await InsertGeographyAsync(
                connection,
                transaction,
                projection.Id,
                document.ListingId,
                document.Geography,
                cancellationToken);
            for (var index = 0; index < document.Contacts.Count; index++)
            {
                await InsertContactAsync(
                    connection,
                    transaction,
                    projection.Id,
                    document.ListingId,
                    index,
                    document.Contacts[index],
                    cancellationToken);
            }

            foreach (var media in document.Media)
            {
                await InsertMediaAsync(
                    connection,
                    transaction,
                    projection.Id,
                    document.ListingId,
                    media,
                    cancellationToken);
            }
        }

        var facets = projection.Documents
            .SelectMany(document => document.CategoryKeys.Select(categoryKey => (document.ListingId, CategoryKey: categoryKey)))
            .GroupBy(item => item.CategoryKey, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new CategoryFacet(group.Key, group.Select(item => item.ListingId).Distinct().Count()));
        foreach (var facet in facets)
        {
            await InsertFacetAsync(
                connection,
                transaction,
                projection.Id,
                facet,
                cancellationToken);
        }
    }

    private static async Task InsertDocumentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid baseProjectionId,
        QueryListingDocument document,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO documents.listing_document
            (
                base_projection_id,
                listing_id,
                listing_revision_id,
                subject_id,
                subject_revision_id,
                listing_kind,
                source_content_digest,
                published_at_utc
            )
            VALUES
            (
                @base_projection_id,
                @listing_id,
                @listing_revision_id,
                @subject_id,
                @subject_revision_id,
                @listing_kind,
                @source_content_digest,
                @published_at_utc
            );
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("base_projection_id", baseProjectionId));
        command.Parameters.Add(new NpgsqlParameter<Guid>("listing_id", document.ListingId));
        command.Parameters.Add(new NpgsqlParameter<Guid>("listing_revision_id", document.ListingRevisionId));
        command.Parameters.Add(new NpgsqlParameter<Guid>("subject_id", document.SubjectId));
        command.Parameters.Add(new NpgsqlParameter<Guid>("subject_revision_id", document.SubjectRevisionId));
        command.Parameters.Add(new NpgsqlParameter<string>("listing_kind", MapListingKind(document.ListingKind)));
        command.Parameters.Add(new NpgsqlParameter<string>("source_content_digest", document.SourceContentDigest));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("published_at_utc", document.PublishedAtUtc));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertLocalizationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid baseProjectionId,
        Guid listingId,
        QueryLocalizedDocument localization,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO documents.listing_localization
            (
                base_projection_id,
                listing_id,
                locale,
                route_path,
                title,
                description_state,
                description
            )
            VALUES
            (
                @base_projection_id,
                @listing_id,
                @locale,
                @route_path,
                @title,
                @description_state,
                @description
            );
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("base_projection_id", baseProjectionId));
        command.Parameters.Add(new NpgsqlParameter<Guid>("listing_id", listingId));
        command.Parameters.Add(new NpgsqlParameter<string>("locale", localization.Locale));
        command.Parameters.Add(new NpgsqlParameter<string>("route_path", localization.RoutePath));
        command.Parameters.Add(new NpgsqlParameter<string>("title", localization.Title));
        command.Parameters.Add(new NpgsqlParameter<string>("description_state", MapFieldState(localization.DescriptionState)));
        command.Parameters.Add(NullableParameter("description", NpgsqlDbType.Text, localization.Description));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertCategoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid baseProjectionId,
        Guid listingId,
        string categoryKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO documents.listing_category
                (base_projection_id, listing_id, category_key)
            VALUES
                (@base_projection_id, @listing_id, @category_key);
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("base_projection_id", baseProjectionId));
        command.Parameters.Add(new NpgsqlParameter<Guid>("listing_id", listingId));
        command.Parameters.Add(new NpgsqlParameter<string>("category_key", categoryKey));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertAttributeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid baseProjectionId,
        Guid listingId,
        QueryAttributeDocument attribute,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO documents.listing_attribute
            (
                base_projection_id,
                listing_id,
                attribute_key,
                state,
                value_kind,
                boolean_value,
                decimal_value,
                text_value,
                text_collection_value
            )
            VALUES
            (
                @base_projection_id,
                @listing_id,
                @attribute_key,
                @state,
                @value_kind,
                @boolean_value,
                @decimal_value,
                @text_value,
                @text_collection_value
            );
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("base_projection_id", baseProjectionId));
        command.Parameters.Add(new NpgsqlParameter<Guid>("listing_id", listingId));
        command.Parameters.Add(new NpgsqlParameter<string>("attribute_key", attribute.AttributeKey));
        command.Parameters.Add(new NpgsqlParameter<string>("state", MapFieldState(attribute.State)));
        command.Parameters.Add(NullableParameter(
            "value_kind",
            NpgsqlDbType.Text,
            attribute.ValueKind is null ? null : MapValueKind(attribute.ValueKind.Value)));
        command.Parameters.Add(NullableParameter("boolean_value", NpgsqlDbType.Boolean, attribute.BooleanValue));
        command.Parameters.Add(NullableParameter("decimal_value", NpgsqlDbType.Numeric, attribute.DecimalValue));
        command.Parameters.Add(NullableParameter("text_value", NpgsqlDbType.Text, attribute.TextValue));
        command.Parameters.Add(NullableParameter(
            "text_collection_value",
            NpgsqlDbType.Array | NpgsqlDbType.Text,
            attribute.TextCollectionValue?.ToArray()));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertGeographyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid baseProjectionId,
        Guid listingId,
        QueryGeographyDocument geography,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO documents.listing_geography
            (
                base_projection_id,
                listing_id,
                state,
                latitude,
                longitude,
                district_key
            )
            VALUES
            (
                @base_projection_id,
                @listing_id,
                @state,
                @latitude,
                @longitude,
                @district_key
            );
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("base_projection_id", baseProjectionId));
        command.Parameters.Add(new NpgsqlParameter<Guid>("listing_id", listingId));
        command.Parameters.Add(new NpgsqlParameter<string>("state", MapGeographyState(geography.State)));
        command.Parameters.Add(NullableParameter("latitude", NpgsqlDbType.Numeric, geography.Latitude));
        command.Parameters.Add(NullableParameter("longitude", NpgsqlDbType.Numeric, geography.Longitude));
        command.Parameters.Add(NullableParameter("district_key", NpgsqlDbType.Text, geography.DistrictKey));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertContactAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid baseProjectionId,
        Guid listingId,
        int ordinal,
        QueryContactDocument contact,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO documents.listing_contact
                (base_projection_id, listing_id, contact_id, ordinal, kind, target, label)
            VALUES
                (@base_projection_id, @listing_id, @contact_id, @ordinal, @kind, @target, @label);
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("base_projection_id", baseProjectionId));
        command.Parameters.Add(new NpgsqlParameter<Guid>("listing_id", listingId));
        command.Parameters.Add(new NpgsqlParameter<Guid>("contact_id", contact.ContactId));
        command.Parameters.Add(new NpgsqlParameter<int>("ordinal", ordinal));
        command.Parameters.Add(new NpgsqlParameter<string>("kind", MapContactKind(contact.Kind)));
        command.Parameters.Add(new NpgsqlParameter<string>("target", contact.Target));
        command.Parameters.Add(NullableParameter("label", NpgsqlDbType.Text, contact.Label));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertMediaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid baseProjectionId,
        Guid listingId,
        QueryMediaDocument media,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO documents.listing_media
            (
                base_projection_id,
                listing_id,
                media_id,
                object_uri,
                content_type,
                content_digest,
                rights_basis
            )
            VALUES
            (
                @base_projection_id,
                @listing_id,
                @media_id,
                @object_uri,
                @content_type,
                @content_digest,
                @rights_basis
            );
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("base_projection_id", baseProjectionId));
        command.Parameters.Add(new NpgsqlParameter<Guid>("listing_id", listingId));
        command.Parameters.Add(new NpgsqlParameter<Guid>("media_id", media.MediaId));
        command.Parameters.Add(new NpgsqlParameter<string>("object_uri", media.ObjectUri));
        command.Parameters.Add(new NpgsqlParameter<string>("content_type", media.ContentType));
        command.Parameters.Add(new NpgsqlParameter<string>("content_digest", media.ContentDigest));
        command.Parameters.Add(new NpgsqlParameter<string>("rights_basis", MapRightsBasis(media.RightsBasis)));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertFacetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid baseProjectionId,
        CategoryFacet facet,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO documents.category_facet
                (base_projection_id, category_key, listing_count)
            VALUES
                (@base_projection_id, @category_key, @listing_count);
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("base_projection_id", baseProjectionId));
        command.Parameters.Add(new NpgsqlParameter<string>("category_key", facet.CategoryKey));
        command.Parameters.Add(new NpgsqlParameter<int>("listing_count", facet.ListingCount));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertOverlayAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        QueryOverlayRevision overlay,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO projection.overlay_revision
                (id, catalog_key, kind, source_revision, created_at_utc, content_digest, item_count)
            VALUES
                (@id, @catalog_key, @kind, @source_revision, @created_at_utc, @content_digest, @item_count);
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", overlay.Id));
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", overlay.CatalogKey));
        command.Parameters.Add(new NpgsqlParameter<string>("kind", MapOverlayKind(overlay.Kind)));
        command.Parameters.Add(new NpgsqlParameter<long>("source_revision", overlay.SourceRevision));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("created_at_utc", overlay.CreatedAtUtc));
        command.Parameters.Add(new NpgsqlParameter<string>("content_digest", overlay.ContentDigest));
        command.Parameters.Add(new NpgsqlParameter<int>("item_count", overlay.ItemCount));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertPublicReadRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublicReadRevision revision,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO projection.public_read_revision
            (
                id,
                catalog_key,
                base_projection_id,
                promotion_overlay_id,
                safety_overlay_id,
                source_publication_id,
                created_at_utc,
                content_digest
            )
            VALUES
            (
                @id,
                @catalog_key,
                @base_projection_id,
                @promotion_overlay_id,
                @safety_overlay_id,
                @source_publication_id,
                @created_at_utc,
                @content_digest
            );
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", revision.Id));
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", revision.CatalogKey));
        command.Parameters.Add(new NpgsqlParameter<Guid>("base_projection_id", revision.BaseProjectionId));
        command.Parameters.Add(new NpgsqlParameter<Guid>("promotion_overlay_id", revision.PromotionOverlayId));
        command.Parameters.Add(new NpgsqlParameter<Guid>("safety_overlay_id", revision.SafetyOverlayId));
        command.Parameters.Add(new NpgsqlParameter<Guid>("source_publication_id", revision.SourcePublicationId));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("created_at_utc", revision.CreatedAtUtc));
        command.Parameters.Add(new NpgsqlParameter<string>("content_digest", revision.ContentDigest));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertCurrentPointerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublicReadRevision revision,
        long activationRevision,
        DateTimeOffset activatedAtUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO projection.current_public_read
                (catalog_key, public_read_revision_id, activation_revision, activated_at_utc)
            VALUES
                (@catalog_key, @public_read_revision_id, @activation_revision, @activated_at_utc)
            ON CONFLICT (catalog_key)
            DO UPDATE SET
                public_read_revision_id = EXCLUDED.public_read_revision_id,
                activation_revision = EXCLUDED.activation_revision,
                activated_at_utc = EXCLUDED.activated_at_utc;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", revision.CatalogKey));
        command.Parameters.Add(new NpgsqlParameter<Guid>("public_read_revision_id", revision.Id));
        command.Parameters.Add(new NpgsqlParameter<long>("activation_revision", activationRevision));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("activated_at_utc", activatedAtUtc));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertCheckpointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublicReadRevision revision,
        QueryInboxMessage inboxMessage,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO projection.catalog_activation_checkpoint
            (
                catalog_key,
                last_activation_revision,
                current_public_read_revision_id,
                last_event_id,
                last_payload_digest,
                updated_at_utc
            )
            VALUES
            (
                @catalog_key,
                @last_activation_revision,
                @current_public_read_revision_id,
                @last_event_id,
                @last_payload_digest,
                @updated_at_utc
            )
            ON CONFLICT (catalog_key)
            DO UPDATE SET
                last_activation_revision = EXCLUDED.last_activation_revision,
                current_public_read_revision_id = EXCLUDED.current_public_read_revision_id,
                last_event_id = EXCLUDED.last_event_id,
                last_payload_digest = EXCLUDED.last_payload_digest,
                updated_at_utc = EXCLUDED.updated_at_utc;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", revision.CatalogKey));
        command.Parameters.Add(new NpgsqlParameter<long>("last_activation_revision", inboxMessage.ActivationRevision));
        command.Parameters.Add(new NpgsqlParameter<Guid>("current_public_read_revision_id", revision.Id));
        command.Parameters.Add(new NpgsqlParameter<Guid>("last_event_id", inboxMessage.EventId));
        command.Parameters.Add(new NpgsqlParameter<string>("last_payload_digest", inboxMessage.PayloadDigest));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("updated_at_utc", inboxMessage.ReceivedAtUtc));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        QueryInboxMessage inboxMessage,
        string catalogKey,
        string outcome,
        Guid resultPublicReadRevisionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO messaging.inbox_message
            (
                event_id,
                event_type,
                payload_digest,
                catalog_key,
                activation_revision,
                outcome,
                result_public_read_revision_id,
                received_at_utc
            )
            VALUES
            (
                @event_id,
                @event_type,
                @payload_digest,
                @catalog_key,
                @activation_revision,
                @outcome,
                @result_public_read_revision_id,
                @received_at_utc
            );
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("event_id", inboxMessage.EventId));
        command.Parameters.Add(new NpgsqlParameter<string>("event_type", inboxMessage.EventType));
        command.Parameters.Add(new NpgsqlParameter<string>("payload_digest", inboxMessage.PayloadDigest));
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", catalogKey));
        command.Parameters.Add(new NpgsqlParameter<long>("activation_revision", inboxMessage.ActivationRevision));
        command.Parameters.Add(new NpgsqlParameter<string>("outcome", outcome));
        command.Parameters.Add(new NpgsqlParameter<Guid>("result_public_read_revision_id", resultPublicReadRevisionId));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("received_at_utc", inboxMessage.ReceivedAtUtc));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlCommand CreateCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string commandText) =>
        new(commandText, connection, transaction);

    private static NpgsqlParameter NullableParameter(
        string name,
        NpgsqlDbType type,
        object? value) =>
        new(name, type)
        {
            Value = value ?? DBNull.Value,
        };

    private static string MapListingKind(QueryListingKind kind) => kind switch
    {
        QueryListingKind.Place => "place",
        QueryListingKind.Provider => "provider",
        _ => throw UnsupportedEnum(nameof(QueryListingKind), kind),
    };

    private static string MapFieldState(QueryFieldState state) => state switch
    {
        QueryFieldState.Observed => "observed",
        QueryFieldState.Missing => "missing",
        QueryFieldState.NotApplicable => "not_applicable",
        QueryFieldState.Withheld => "withheld",
        _ => throw UnsupportedEnum(nameof(QueryFieldState), state),
    };

    private static string MapValueKind(QueryValueKind kind) => kind switch
    {
        QueryValueKind.BooleanValue => "boolean",
        QueryValueKind.DecimalNumber => "decimal",
        QueryValueKind.TextValue => "text",
        QueryValueKind.TextCollection => "text_collection",
        QueryValueKind.DurationMinutes => "duration_minutes",
        _ => throw UnsupportedEnum(nameof(QueryValueKind), kind),
    };

    private static string MapGeographyState(QueryGeographyState state) => state switch
    {
        QueryGeographyState.PrimaryMarket => "primary_market",
        QueryGeographyState.NearbyMarket => "nearby_market",
        QueryGeographyState.RemoteOnly => "remote_only",
        QueryGeographyState.OutsideMarket => "outside_market",
        _ => throw UnsupportedEnum(nameof(QueryGeographyState), state),
    };

    private static string MapContactKind(QueryContactKind kind) => kind switch
    {
        QueryContactKind.Website => "website",
        QueryContactKind.Email => "email",
        QueryContactKind.Phone => "phone",
        QueryContactKind.WhatsApp => "whatsapp",
        QueryContactKind.BookingReference => "booking_reference",
        QueryContactKind.MapReference => "map_reference",
        _ => throw UnsupportedEnum(nameof(QueryContactKind), kind),
    };

    private static string MapRightsBasis(QueryMediaRightsBasis rightsBasis) => rightsBasis switch
    {
        QueryMediaRightsBasis.OwnerProvided => "owner_provided",
        QueryMediaRightsBasis.ExplicitLicense => "explicit_license",
        QueryMediaRightsBasis.OriginalEditorialWork => "original_editorial_work",
        QueryMediaRightsBasis.PublicDomain => "public_domain",
        _ => throw UnsupportedEnum(nameof(QueryMediaRightsBasis), rightsBasis),
    };

    private static string MapOverlayKind(QueryOverlayKind kind) => kind switch
    {
        QueryOverlayKind.Promotion => "promotion",
        QueryOverlayKind.VisibilitySafety => "visibility_safety",
        _ => throw UnsupportedEnum(nameof(QueryOverlayKind), kind),
    };

    private static QueryProjectionException UnsupportedEnum<TEnum>(string enumName, TEnum value)
        where TEnum : struct, Enum =>
        Failure(
            "QUERY_PERSISTENCE_ENUM_UNSUPPORTED",
            500,
            $"Value '{value}' is not supported for enum '{enumName}'.",
            "Correct the Query persistence mapper before retrying activation.");

    private static QueryProjectionException Failure(
        string code,
        int statusCode,
        string message,
        string requiredAction) =>
        new(
            "Query.Persistence",
            code,
            statusCode,
            message,
            requiredAction);

    private sealed record InboxState(string PayloadDigest, Guid ResultPublicReadRevisionId);

    private sealed record ActivationOwner(Guid EventId);

    private sealed record CheckpointState(long LastActivationRevision, Guid CurrentPublicReadRevisionId);

    private sealed record CategoryFacet(string CategoryKey, int ListingCount);
}
