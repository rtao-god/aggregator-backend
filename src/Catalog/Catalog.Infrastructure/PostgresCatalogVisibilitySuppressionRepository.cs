using System.Data;
using Aggregator.Catalog.Application;
using Aggregator.Catalog.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Aggregator.Catalog.Infrastructure;

/// <summary>
/// Stores the suppression aggregate, immutable transition revisions, and its outbox event
/// in one Catalog database transaction.
/// </summary>
public sealed partial class PostgresCatalogVisibilitySuppressionRepository :
    ICatalogVisibilitySuppressionRepository
{
    private readonly CatalogDbContext _dbContext;

    public PostgresCatalogVisibilitySuppressionRepository(CatalogDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task EnsureTargetExistsAsync(
        CatalogKey catalogKey,
        PublicVisibilitySuppressionTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalogKey);
        ArgumentNullException.ThrowIfNull(target);
        await _dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var catalogExists = await ExistsAsync(
                """
                SELECT EXISTS
                (
                    SELECT 1
                    FROM catalog.active_configuration
                    WHERE catalog_key = @catalog_key
                );
                """,
                catalogKey,
                targetId: null,
                cancellationToken);
            if (!catalogExists)
            {
                throw new CatalogNotFoundException("active-catalog", catalogKey.Value);
            }

            var targetExists = target.Kind switch
            {
                PublicVisibilitySuppressionTargetKind.Listing => await ExistsAsync(
                    """
                    SELECT EXISTS
                    (
                        SELECT 1
                        FROM catalog.listing
                        WHERE id = @target_id
                          AND catalog_key = @catalog_key
                    );
                    """,
                    catalogKey,
                    target.ListingId,
                    cancellationToken),
                PublicVisibilitySuppressionTargetKind.Media => await ExistsAsync(
                    """
                    SELECT EXISTS
                    (
                        SELECT 1
                        FROM catalog.current_publication current
                        JOIN catalog.publication_entry entry
                          ON entry.publication_id = current.publication_id
                        JOIN catalog.media media
                          ON media.listing_revision_id = entry.listing_revision_id
                        WHERE current.catalog_key = @catalog_key
                          AND media.media_id = @target_id
                    );
                    """,
                    catalogKey,
                    ParseTargetId(target),
                    cancellationToken),
                PublicVisibilitySuppressionTargetKind.Contact => await ExistsAsync(
                    """
                    SELECT EXISTS
                    (
                        SELECT 1
                        FROM catalog.current_publication current
                        JOIN catalog.publication_entry entry
                          ON entry.publication_id = current.publication_id
                        JOIN catalog.contact contact
                          ON contact.listing_revision_id = entry.listing_revision_id
                        WHERE current.catalog_key = @catalog_key
                          AND contact.id = @target_id
                    );
                    """,
                    catalogKey,
                    ParseTargetId(target),
                    cancellationToken),
                PublicVisibilitySuppressionTargetKind.Route => await ExistsAsync(
                    """
                    SELECT EXISTS
                    (
                        SELECT 1
                        FROM catalog.current_publication
                        WHERE catalog_key = @catalog_key
                    );
                    """,
                    catalogKey,
                    targetId: null,
                    cancellationToken),
                PublicVisibilitySuppressionTargetKind.ExternalReference =>
                    throw new CatalogContractException(
                        "catalog.visibility_external_reference_not_supported",
                        "External-reference suppression requires stable external-reference identities in Catalog publication artifacts."),
                _ => throw new CatalogContractException(
                    "catalog.visibility_target_kind_unsupported",
                    $"Visibility suppression target kind '{target.Kind}' is unsupported."),
            };

            if (!targetExists)
            {
                throw new CatalogNotFoundException(
                    $"public-{target.Kind.ToString().ToLowerInvariant()}",
                    target.TargetKey);
            }
        }
        finally
        {
            await _dbContext.Database.CloseConnectionAsync();
        }
    }

    public async Task<PublicVisibilitySuppression?> GetAsync(
        Guid suppressionId,
        CancellationToken cancellationToken)
    {
        if (suppressionId == Guid.Empty)
        {
            throw new ArgumentException("Suppression ID is required.", nameof(suppressionId));
        }

        await _dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = _dbContext.Database.GetDbConnection().CreateCommand();
            command.CommandText = SelectSuppressionSql;
            command.Parameters.Add(new NpgsqlParameter<Guid>("suppression_id", suppressionId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken)
                ? ReadSuppression(reader)
                : null;
        }
        finally
        {
            await _dbContext.Database.CloseConnectionAsync();
        }
    }

    public Task CreateActiveAsync(
        PublicVisibilitySuppression requested,
        PublicVisibilitySuppression active,
        CatalogOutboxMessage outboxMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(outboxMessage);
        if (requested.Id != active.Id ||
            requested.State != PublicVisibilitySuppressionState.Requested ||
            requested.Revision != 1 ||
            active.State != PublicVisibilitySuppressionState.Active ||
            active.Revision != 2)
        {
            throw new CatalogInvariantException(
                "Creating an active suppression requires the exact requested revision and its active successor.");
        }

        return ExecuteInTransactionAsync(async (connection, transaction, innerCancellationToken) =>
        {
            await InsertCurrentAsync(connection, transaction, active, innerCancellationToken);
            await InsertRevisionAsync(connection, transaction, requested, innerCancellationToken);
            await InsertRevisionAsync(connection, transaction, active, innerCancellationToken);
            await InsertOutboxAsync(connection, transaction, outboxMessage, innerCancellationToken);
        }, cancellationToken);
    }

    public Task ResolveAsync(
        PublicVisibilitySuppression resolved,
        CatalogOutboxMessage outboxMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(outboxMessage);
        if (resolved.State != PublicVisibilitySuppressionState.Resolved || resolved.Revision != 3)
        {
            throw new CatalogInvariantException(
                "Resolving a suppression requires its exact resolved revision.");
        }

        return ExecuteInTransactionAsync(async (connection, transaction, innerCancellationToken) =>
        {
            var expectedRevision = checked(resolved.Revision - 1);
            await using var command = CreateCommand(connection, transaction, """
                UPDATE catalog.public_visibility_suppression
                SET state = @state,
                    revision = @revision,
                    changed_by_actor_id = @changed_by_actor_id,
                    transition_reason = @transition_reason,
                    changed_at_utc = @changed_at_utc
                WHERE id = @id
                  AND revision = @expected_revision
                  AND state = 2;
                """);
            command.Parameters.AddWithValue("state", NpgsqlDbType.Integer, (int)resolved.State);
            command.Parameters.AddWithValue("revision", NpgsqlDbType.Bigint, resolved.Revision);
            command.Parameters.AddWithValue("changed_by_actor_id", NpgsqlDbType.Uuid, resolved.ChangedByActorId);
            command.Parameters.AddWithValue("transition_reason", NpgsqlDbType.Varchar, resolved.TransitionReason);
            command.Parameters.AddWithValue("changed_at_utc", NpgsqlDbType.TimestampTz, resolved.ChangedAtUtc);
            command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, resolved.Id);
            command.Parameters.AddWithValue("expected_revision", NpgsqlDbType.Bigint, expectedRevision);
            var affected = await command.ExecuteNonQueryAsync(innerCancellationToken);
            if (affected != 1)
            {
                var actualRevision = await ReadActualRevisionAsync(
                    connection,
                    transaction,
                    resolved.Id,
                    innerCancellationToken);
                if (actualRevision is null)
                {
                    throw new CatalogNotFoundException(
                        "public-visibility-suppression",
                        resolved.Id);
                }

                throw new CatalogSuppressionConcurrencyException(
                    resolved.Id,
                    expectedRevision,
                    actualRevision.Value);
            }

            await InsertRevisionAsync(connection, transaction, resolved, innerCancellationToken);
            await InsertOutboxAsync(connection, transaction, outboxMessage, innerCancellationToken);
        }, cancellationToken);
    }

    private async Task ExecuteInTransactionAsync(
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        await _dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = (NpgsqlConnection)_dbContext.Database.GetDbConnection();
            await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            try
            {
                await action(connection, transaction, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (PostgresException exception) when (
                exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new CatalogConflictException(
                    "An active suppression already owns the same Catalog target or suppression identity.")
                {
                    Source = exception.Source,
                };
            }
        }
        finally
        {
            await _dbContext.Database.CloseConnectionAsync();
        }
    }
}
