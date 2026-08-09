using System.Data;
using Aggregator.Promotion.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Aggregator.Promotion.Infrastructure;

/// <summary>
/// Atomically persists one Catalog message inbox record and the current Promotion eligibility checkpoint.
/// </summary>
public sealed class PostgresPromotionEligibilityProjectionStore(
    PromotionDbContext dbContext) : IPromotionEligibilityProjectionStore
{
    public async Task<PromotionEligibilityProjectionApplyResult> ApplyAsync(
        PromotionEligibilityProjectionChange change,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        ValidateChange(change, receivedAtUtc);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        var npgsqlTransaction = transaction.GetDbTransaction();
        await AcquireLocksAsync(
            connection,
            npgsqlTransaction,
            change,
            cancellationToken);

        var existingInbox = await ReadInboxAsync(
            connection,
            npgsqlTransaction,
            change.MessageId,
            cancellationToken);
        if (existingInbox is not null)
        {
            EnsureSameInbox(existingInbox, change);
            var currentForReplay = await ReadCurrentAsync(
                connection,
                npgsqlTransaction,
                change.Eligibility.CatalogKey,
                change.Eligibility.ListingId,
                cancellationToken)
                ?? throw Failure(
                    "PROMOTION_ELIGIBILITY_INBOX_RESULT_MISSING",
                    500,
                    "Promotion inbox contains an applied Catalog event but the eligibility projection is absent.",
                    "Stop placement mutations and rebuild the Promotion eligibility projection from Catalog events.",
                    change);
            if (currentForReplay.SourceRevision < change.Eligibility.SourceRevision)
            {
                throw Failure(
                    "PROMOTION_ELIGIBILITY_INBOX_AHEAD_OF_PROJECTION",
                    500,
                    "Promotion inbox is ahead of its current eligibility checkpoint.",
                    "Stop placement mutations and rebuild the Promotion eligibility projection from Catalog events.",
                    change,
                    currentForReplay.SourceRevision);
            }

            await transaction.CommitAsync(cancellationToken);
            return PromotionEligibilityProjectionApplyResult.Replayed;
        }

        var current = await ReadCurrentAsync(
            connection,
            npgsqlTransaction,
            change.Eligibility.CatalogKey,
            change.Eligibility.ListingId,
            cancellationToken);
        ValidateRevisionTransition(current, change);
        await InsertInboxAsync(
            connection,
            npgsqlTransaction,
            change,
            receivedAtUtc,
            cancellationToken);
        if (current is null)
        {
            await InsertCurrentAsync(
                connection,
                npgsqlTransaction,
                change,
                receivedAtUtc,
                cancellationToken);
        }
        else
        {
            await UpdateCurrentAsync(
                connection,
                npgsqlTransaction,
                current.SourceRevision,
                change,
                receivedAtUtc,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return PromotionEligibilityProjectionApplyResult.Applied;
    }

    private static async Task AcquireLocksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PromotionEligibilityProjectionChange change,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT pg_advisory_xact_lock(hashtextextended(@message_id, 1));
            SELECT pg_advisory_xact_lock(hashtextextended(@listing_stream, 2));
            """;
        command.Parameters.AddWithValue(
            "message_id",
            NpgsqlDbType.Text,
            change.MessageId.ToString("D"));
        command.Parameters.AddWithValue(
            "listing_stream",
            NpgsqlDbType.Text,
            $"{change.Eligibility.CatalogKey}:{change.Eligibility.ListingId:D}");
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<InboxSnapshot?> ReadInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                contract_identity,
                payload_digest,
                catalog_key,
                listing_id,
                source_revision,
                projection_digest,
                correlation_id,
                causation_id
            FROM messaging.inbox_message
            WHERE message_id = @message_id;
            """;
        command.Parameters.AddWithValue("message_id", NpgsqlDbType.Uuid, messageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new InboxSnapshot(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetGuid(3),
            reader.GetInt64(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetGuid(7));
    }

    private static async Task<CurrentSnapshot?> ReadCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string catalogKey,
        Guid listingId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                source_revision,
                projection_digest,
                source_message_id
            FROM access_projection.listing_eligibility_projection
            WHERE catalog_key = @catalog_key
              AND listing_id = @listing_id;
            """;
        command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Varchar, catalogKey);
        command.Parameters.AddWithValue("listing_id", NpgsqlDbType.Uuid, listingId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CurrentSnapshot(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetGuid(2));
    }

    private static void EnsureSameInbox(
        InboxSnapshot existing,
        PromotionEligibilityProjectionChange change)
    {
        if (!string.Equals(existing.ContractIdentity, change.ContractIdentity, StringComparison.Ordinal) ||
            !string.Equals(existing.PayloadDigest, change.PayloadDigest, StringComparison.Ordinal) ||
            !string.Equals(existing.CatalogKey, change.Eligibility.CatalogKey, StringComparison.Ordinal) ||
            existing.ListingId != change.Eligibility.ListingId ||
            existing.SourceRevision != change.Eligibility.SourceRevision ||
            !string.Equals(existing.ProjectionDigest, change.ProjectionDigest, StringComparison.Ordinal) ||
            !string.Equals(existing.CorrelationId, change.CorrelationId, StringComparison.Ordinal) ||
            existing.CausationId != change.CausationId)
        {
            throw Failure(
                "PROMOTION_ELIGIBILITY_INBOX_MESSAGE_CORRUPT",
                409,
                "The same Catalog message ID arrived with different contract, payload, or projection identity.",
                "Dead-letter the message and inspect the Catalog outbox bytes before replay.",
                change);
        }
    }

    private static void ValidateRevisionTransition(
        CurrentSnapshot? current,
        PromotionEligibilityProjectionChange change)
    {
        var incomingRevision = change.Eligibility.SourceRevision;
        if (current is null)
        {
            if (incomingRevision != 1)
            {
                throw RevisionGap(change, currentRevision: 0);
            }

            return;
        }

        if (incomingRevision < current.SourceRevision)
        {
            throw Failure(
                "PROMOTION_ELIGIBILITY_EVENT_STALE",
                409,
                $"Catalog eligibility revision '{incomingRevision}' trails stored revision '{current.SourceRevision}'.",
                "Replay only the missing next Catalog eligibility revision or perform an explicit projection rebuild.",
                change,
                current.SourceRevision);
        }

        if (incomingRevision == current.SourceRevision)
        {
            throw Failure(
                string.Equals(current.ProjectionDigest, change.ProjectionDigest, StringComparison.Ordinal)
                    ? "PROMOTION_ELIGIBILITY_REVISION_REISSUED"
                    : "PROMOTION_ELIGIBILITY_REVISION_DIVERGED",
                409,
                "A different message attempted to reuse an already checkpointed Catalog eligibility revision.",
                "Dead-letter the message and inspect the Catalog listing eligibility revision stream.",
                change,
                current.SourceRevision);
        }

        var expectedRevision = checked(current.SourceRevision + 1);
        if (incomingRevision != expectedRevision)
        {
            throw RevisionGap(change, current.SourceRevision);
        }
    }

    private static async Task InsertInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PromotionEligibilityProjectionChange change,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO messaging.inbox_message (
                message_id,
                contract_identity,
                payload_digest,
                catalog_key,
                listing_id,
                source_revision,
                projection_digest,
                correlation_id,
                causation_id,
                received_at_utc,
                processed_at_utc,
                processing_state)
            VALUES (
                @message_id,
                @contract_identity,
                @payload_digest,
                @catalog_key,
                @listing_id,
                @source_revision,
                @projection_digest,
                @correlation_id,
                @causation_id,
                @received_at_utc,
                @processed_at_utc,
                'applied');
            """;
        AddInboxParameters(command, change, receivedAtUtc);
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PromotionEligibilityProjectionChange change,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO access_projection.listing_eligibility_projection (
                catalog_key,
                listing_id,
                published_listing_revision_id,
                is_published,
                is_archived,
                has_blocking_dispute,
                has_verified_contact,
                contact_capabilities_json,
                category_keys_json,
                district_key,
                source_revision,
                changed_at_utc,
                source_message_id,
                source_contract_identity,
                source_payload_digest,
                projection_digest,
                correlation_id,
                causation_id,
                received_at_utc)
            VALUES (
                @catalog_key,
                @listing_id,
                @published_listing_revision_id,
                @is_published,
                @is_archived,
                @has_blocking_dispute,
                @has_verified_contact,
                @contact_capabilities_json,
                @category_keys_json,
                @district_key,
                @source_revision,
                @changed_at_utc,
                @source_message_id,
                @source_contract_identity,
                @source_payload_digest,
                @projection_digest,
                @correlation_id,
                @causation_id,
                @received_at_utc);
            """;
        AddProjectionParameters(command, change, receivedAtUtc);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw PersistenceConcurrencyFailure(change);
        }
    }

    private static async Task UpdateCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long expectedSourceRevision,
        PromotionEligibilityProjectionChange change,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE access_projection.listing_eligibility_projection
            SET published_listing_revision_id = @published_listing_revision_id,
                is_published = @is_published,
                is_archived = @is_archived,
                has_blocking_dispute = @has_blocking_dispute,
                has_verified_contact = @has_verified_contact,
                contact_capabilities_json = @contact_capabilities_json,
                category_keys_json = @category_keys_json,
                district_key = @district_key,
                source_revision = @source_revision,
                changed_at_utc = @changed_at_utc,
                source_message_id = @source_message_id,
                source_contract_identity = @source_contract_identity,
                source_payload_digest = @source_payload_digest,
                projection_digest = @projection_digest,
                correlation_id = @correlation_id,
                causation_id = @causation_id,
                received_at_utc = @received_at_utc
            WHERE catalog_key = @catalog_key
              AND listing_id = @listing_id
              AND source_revision = @expected_source_revision;
            """;
        AddProjectionParameters(command, change, receivedAtUtc);
        command.Parameters.AddWithValue(
            "expected_source_revision",
            NpgsqlDbType.Bigint,
            expectedSourceRevision);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw PersistenceConcurrencyFailure(change);
        }
    }

    private static void AddInboxParameters(
        NpgsqlCommand command,
        PromotionEligibilityProjectionChange change,
        DateTimeOffset receivedAtUtc)
    {
        command.Parameters.AddWithValue("message_id", NpgsqlDbType.Uuid, change.MessageId);
        command.Parameters.AddWithValue("contract_identity", NpgsqlDbType.Varchar, change.ContractIdentity);
        command.Parameters.AddWithValue("payload_digest", NpgsqlDbType.Char, change.PayloadDigest);
        command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Varchar, change.Eligibility.CatalogKey);
        command.Parameters.AddWithValue("listing_id", NpgsqlDbType.Uuid, change.Eligibility.ListingId);
        command.Parameters.AddWithValue("source_revision", NpgsqlDbType.Bigint, change.Eligibility.SourceRevision);
        command.Parameters.AddWithValue("projection_digest", NpgsqlDbType.Char, change.ProjectionDigest);
        command.Parameters.AddWithValue("correlation_id", NpgsqlDbType.Varchar, change.CorrelationId);
        command.Parameters.Add(new NpgsqlParameter<Guid?>("causation_id", NpgsqlDbType.Uuid)
        {
            TypedValue = change.CausationId,
        });
        command.Parameters.AddWithValue("received_at_utc", NpgsqlDbType.TimestampTz, receivedAtUtc);
        command.Parameters.AddWithValue("processed_at_utc", NpgsqlDbType.TimestampTz, receivedAtUtc);
    }

    private static void AddProjectionParameters(
        NpgsqlCommand command,
        PromotionEligibilityProjectionChange change,
        DateTimeOffset receivedAtUtc)
    {
        var eligibility = change.Eligibility;
        command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Varchar, eligibility.CatalogKey);
        command.Parameters.AddWithValue("listing_id", NpgsqlDbType.Uuid, eligibility.ListingId);
        command.Parameters.Add(new NpgsqlParameter<Guid?>(
            "published_listing_revision_id",
            NpgsqlDbType.Uuid)
        {
            TypedValue = change.PublishedListingRevisionId,
        });
        command.Parameters.AddWithValue("is_published", NpgsqlDbType.Boolean, eligibility.IsPublished);
        command.Parameters.AddWithValue("is_archived", NpgsqlDbType.Boolean, eligibility.IsArchived);
        command.Parameters.AddWithValue("has_blocking_dispute", NpgsqlDbType.Boolean, eligibility.HasBlockingDispute);
        command.Parameters.AddWithValue("has_verified_contact", NpgsqlDbType.Boolean, eligibility.HasVerifiedContact);
        command.Parameters.AddWithValue(
            "contact_capabilities_json",
            NpgsqlDbType.Jsonb,
            PromotionPersistenceJson.SerializeStringSet(eligibility.ContactCapabilities));
        command.Parameters.AddWithValue(
            "category_keys_json",
            NpgsqlDbType.Jsonb,
            PromotionPersistenceJson.SerializeStringSet(eligibility.CategoryKeys));
        command.Parameters.Add(new NpgsqlParameter<string?>("district_key", NpgsqlDbType.Varchar)
        {
            TypedValue = eligibility.DistrictKey,
        });
        command.Parameters.AddWithValue("source_revision", NpgsqlDbType.Bigint, eligibility.SourceRevision);
        command.Parameters.AddWithValue("changed_at_utc", NpgsqlDbType.TimestampTz, eligibility.ChangedAtUtc);
        command.Parameters.AddWithValue("source_message_id", NpgsqlDbType.Uuid, change.MessageId);
        command.Parameters.AddWithValue("source_contract_identity", NpgsqlDbType.Varchar, change.ContractIdentity);
        command.Parameters.AddWithValue("source_payload_digest", NpgsqlDbType.Char, change.PayloadDigest);
        command.Parameters.AddWithValue("projection_digest", NpgsqlDbType.Char, change.ProjectionDigest);
        command.Parameters.AddWithValue("correlation_id", NpgsqlDbType.Varchar, change.CorrelationId);
        command.Parameters.Add(new NpgsqlParameter<Guid?>("causation_id", NpgsqlDbType.Uuid)
        {
            TypedValue = change.CausationId,
        });
        command.Parameters.AddWithValue("received_at_utc", NpgsqlDbType.TimestampTz, receivedAtUtc);
    }

    private static void ValidateChange(
        PromotionEligibilityProjectionChange change,
        DateTimeOffset receivedAtUtc)
    {
        if (change.MessageId == Guid.Empty ||
            string.IsNullOrWhiteSpace(change.ContractIdentity) ||
            string.IsNullOrWhiteSpace(change.CorrelationId) ||
            change.ProjectionDigest is not { Length: 64 } ||
            change.PayloadDigest is not { Length: 64 } ||
            receivedAtUtc.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "PROMOTION_ELIGIBILITY_STORE_INPUT_INVALID",
                500,
                "Promotion eligibility projection store received invalid owner input.",
                "Correct the Promotion application boundary before resuming the consumer.",
                change);
        }

        if (receivedAtUtc < change.Eligibility.ChangedAtUtc)
        {
            throw Failure(
                "PROMOTION_ELIGIBILITY_RECEIVED_BEFORE_EVENT",
                503,
                "Promotion received a Catalog eligibility event timestamped in the future.",
                "Correct clock synchronization before replaying the exact event.",
                change);
        }
    }

    private static PromotionApplicationException RevisionGap(
        PromotionEligibilityProjectionChange change,
        long currentRevision) =>
        Failure(
            "PROMOTION_ELIGIBILITY_REVISION_GAP",
            503,
            $"Catalog eligibility revision '{change.Eligibility.SourceRevision}' is not the next revision after '{currentRevision}'.",
            "Replay the exact missing Catalog listing eligibility revisions before resuming placement mutations.",
            change,
            currentRevision);

    private static PromotionApplicationException PersistenceConcurrencyFailure(
        PromotionEligibilityProjectionChange change) =>
        Failure(
            "PROMOTION_ELIGIBILITY_PERSISTENCE_CONFLICT",
            409,
            "Promotion eligibility checkpoint changed while applying the Catalog event.",
            "Replay the event after reading the exact current eligibility checkpoint.",
            change);

    private static PromotionApplicationException Failure(
        string code,
        int statusCode,
        string detail,
        string requiredAction,
        PromotionEligibilityProjectionChange change,
        long? currentRevision = null) =>
        new(
            "Promotion.EligibilityProjection",
            code,
            statusCode,
            detail,
            requiredAction,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageId"] = change.MessageId,
                ["catalogKey"] = change.Eligibility.CatalogKey,
                ["listingId"] = change.Eligibility.ListingId,
                ["incomingRevision"] = change.Eligibility.SourceRevision,
                ["currentRevision"] = currentRevision,
            });

    private sealed record InboxSnapshot(
        string ContractIdentity,
        string PayloadDigest,
        string CatalogKey,
        Guid ListingId,
        long SourceRevision,
        string ProjectionDigest,
        string CorrelationId,
        Guid? CausationId);

    private sealed record CurrentSnapshot(
        long SourceRevision,
        string ProjectionDigest,
        Guid SourceMessageId);
}
