using System.Text;
using Aggregator.Query.Application;
using Aggregator.Query.Contracts;
using Aggregator.Query.Domain;
using Npgsql;

namespace Aggregator.Query.Infrastructure;

/// <summary>Persists one exact public-read activation event inside its pointer-switch transaction.</summary>
internal static class QueryPublicReadActivationOutboxWriter
{
    public static async Task InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublicReadRevision revision,
        long activationRevision,
        DateTimeOffset activatedAtUtc,
        string correlationId,
        Guid causationId,
        IQueryIdFactory idFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(idFactory);
        if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 128)
        {
            throw Failure(
                "QUERY_PUBLIC_READ_CORRELATION_INVALID",
                "Public-read activation outbox correlation ID is missing or too long.");
        }

        if (causationId == Guid.Empty)
        {
            throw Failure(
                "QUERY_PUBLIC_READ_CAUSATION_INVALID",
                "Public-read activation outbox causation ID must identify the input event.");
        }

        var listingIds = await ReadPublicListingIdsAsync(
            connection,
            transaction,
            revision,
            cancellationToken);
        var placements = await ReadSponsoredPlacementsAsync(
            connection,
            transaction,
            revision,
            cancellationToken);
        var integrationEvent = PublicReadActivationEventFactory.Create(
            idFactory.Create(),
            revision,
            activationRevision,
            listingIds,
            placements,
            activatedAtUtc);
        var payload = QueryCanonicalJson.Serialize(integrationEvent);
        var payloadJson = Encoding.UTF8.GetString(payload);
        var payloadDigest = QueryCanonicalJson.ComputeDigest(payload.AsSpan());

        const string sql = """
            INSERT INTO messaging.outbox_message
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
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("message_id", integrationEvent.EventId));
        command.Parameters.Add(new NpgsqlParameter<string>(
            "routing_key",
            QueryIntegrationEventTypes.PublicReadRevisionActivated));
        command.Parameters.Add(new NpgsqlParameter<string>(
            "contract_identity",
            QueryIntegrationEventContracts.PublicReadRevisionActivated));
        command.Parameters.Add(new NpgsqlParameter<string>("payload_json", payloadJson));
        command.Parameters.Add(new NpgsqlParameter<string>("payload_digest", payloadDigest));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>(
            "occurred_at_utc",
            integrationEvent.OccurredAtUtc));
        command.Parameters.Add(new NpgsqlParameter<string>(
            "correlation_id",
            correlationId.Trim()));
        command.Parameters.Add(new NpgsqlParameter<Guid>("causation_id", causationId));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Failure(
                "QUERY_PUBLIC_READ_OUTBOX_INSERT_FAILED",
                $"Public-read activation event '{integrationEvent.EventId}' was not persisted.");
        }
    }

    private static async Task<IReadOnlyList<Guid>> ReadPublicListingIdsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublicReadRevision revision,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT document.listing_id
            FROM documents.listing_document document
            WHERE document.base_projection_id = @base_projection_id
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM projection.visibility_safety_overlay_item suppression
                  WHERE suppression.overlay_id = @safety_overlay_id
                    AND suppression.target_kind = 'listing'
                    AND suppression.listing_id = document.listing_id
              )
            ORDER BY document.listing_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>(
            "base_projection_id",
            revision.BaseProjectionId));
        command.Parameters.Add(new NpgsqlParameter<Guid>(
            "safety_overlay_id",
            revision.SafetyOverlayId));
        var result = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetGuid(0));
        }

        return result;
    }

    private static async Task<IReadOnlyList<PublicReadSponsoredPlacementReference>>
        ReadSponsoredPlacementsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            PublicReadRevision revision,
            CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT placement.placement_id,
                   placement.listing_id,
                   placement.scope_type,
                   placement.scope_key,
                   placement.starts_at_utc,
                   placement.hard_expiry_at_utc
            FROM projection.promotion_overlay_item placement
            JOIN documents.listing_document document
              ON document.base_projection_id = @base_projection_id
             AND document.listing_id = placement.listing_id
            WHERE placement.overlay_id = @promotion_overlay_id
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM projection.visibility_safety_overlay_item suppression
                  WHERE suppression.overlay_id = @safety_overlay_id
                    AND suppression.target_kind = 'listing'
                    AND suppression.listing_id = placement.listing_id
              )
            ORDER BY placement.placement_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>(
            "base_projection_id",
            revision.BaseProjectionId));
        command.Parameters.Add(new NpgsqlParameter<Guid>(
            "promotion_overlay_id",
            revision.PromotionOverlayId));
        command.Parameters.Add(new NpgsqlParameter<Guid>(
            "safety_overlay_id",
            revision.SafetyOverlayId));
        var result = new List<PublicReadSponsoredPlacementReference>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PublicReadSponsoredPlacementReference(
                reader.GetGuid(0),
                reader.GetGuid(1),
                MapScope(reader.GetString(2)),
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return result;
    }

    private static PublicReadPlacementScopeTypeContract MapScope(string value) => value switch
    {
        "catalog" => PublicReadPlacementScopeTypeContract.Catalog,
        "category" => PublicReadPlacementScopeTypeContract.Category,
        "district" => PublicReadPlacementScopeTypeContract.District,
        "editorial_landing" => PublicReadPlacementScopeTypeContract.EditorialLanding,
        _ => throw Failure(
            "QUERY_PUBLIC_READ_PLACEMENT_SCOPE_UNSUPPORTED",
            $"Promotion overlay contains unsupported scope type '{value}'."),
    };

    private static QueryProjectionException Failure(string code, string detail) =>
        new(
            "Query.PublicReadOutbox",
            code,
            500,
            detail,
            "Rollback the pointer transaction and repair the Query projection owner before retrying activation.");
}
