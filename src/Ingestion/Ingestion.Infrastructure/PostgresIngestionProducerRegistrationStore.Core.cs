using System.Data;
using Aggregator.Ingestion.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aggregator.Ingestion.Infrastructure;

/// <summary>PostgreSQL owner for current producer registration, immutable history, and idempotent command results.</summary>
public sealed partial class PostgresIngestionProducerRegistrationStore(IngestionDbContext dbContext)
    : IIngestionProducerRegistrationStore
{
    private const long CommandLockSeed = 8_071_001_111;
    private const long ProducerLockSeed = 8_071_001_112;

    public async Task<IngestionProducerRegistrationMutationResult> PutAsync(
        IngestionProducerRegistrationMutation mutation,
        CancellationToken cancellationToken)
    {
        ValidateMutation(mutation);
        var resultDocument = IngestionCanonicalJson.Serialize(mutation.Registration);
        var resultDigest = IngestionCanonicalJson.ComputeDocumentDigest(resultDocument);
        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            await AcquireLockAsync(
                connection,
                transaction,
                $"{mutation.CommandIdentity.Scope}:{mutation.CommandIdentity.Key}",
                CommandLockSeed,
                cancellationToken);
            var replay = await ReadCommandAsync(
                connection,
                transaction,
                mutation.CommandIdentity.Scope,
                mutation.CommandIdentity.Key,
                cancellationToken);
            if (replay is not null)
            {
                var result = RestoreReplay(replay, mutation);
                await transaction.CommitAsync(cancellationToken);
                return new IngestionProducerRegistrationMutationResult(result, Replayed: true);
            }

            await AcquireLockAsync(
                connection,
                transaction,
                mutation.Registration.ProducerIdentity,
                ProducerLockSeed,
                cancellationToken);
            var current = await ReadCurrentAsync(
                connection,
                transaction,
                mutation.Registration.ProducerIdentity,
                forUpdate: true,
                cancellationToken);
            EnsureExpectedRevision(current, mutation);
            await InsertRevisionAsync(
                connection,
                transaction,
                mutation.Registration,
                cancellationToken);
            await UpsertCurrentAsync(
                connection,
                transaction,
                mutation.Registration,
                current is null,
                mutation.ExpectedAggregateRevision,
                cancellationToken);
            await InsertCommandAsync(
                connection,
                transaction,
                mutation,
                resultDocument,
                resultDigest,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new IngestionProducerRegistrationMutationResult(
                mutation.Registration,
                Replayed: false);
        }
        finally
        {
            if (shouldClose)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }

    public async Task<IngestionProducerRegistrationSnapshot?> ReadAsync(
        string producerIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(producerIdentity);
        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            return await ReadCurrentAsync(
                connection,
                transaction: null,
                producerIdentity,
                forUpdate: false,
                cancellationToken);
        }
        finally
        {
            if (shouldClose)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }

    private static async Task AcquireLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string identity,
        long seed,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@identity, @seed));",
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<string>("identity", identity));
        command.Parameters.Add(new NpgsqlParameter<long>("seed", seed));
        _ = await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task<StoredProducerCommand?> ReadCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string scope,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT request_digest, producer_identity, result_document, result_digest, caller_service_identity
            FROM operations.producer_registration_command
            WHERE scope = @scope AND key = @key;
            """,
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<string>("scope", scope));
        command.Parameters.Add(new NpgsqlParameter<string>("key", key));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StoredProducerCommand(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetFieldValue<byte[]>(2),
                reader.GetString(3),
                reader.GetString(4))
            : null;
    }

    private static async Task<IngestionProducerRegistrationSnapshot?> ReadCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string producerIdentity,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var sql = """
            SELECT identity, active, supported_contract_revisions, aggregate_revision,
                   content_digest, updated_by_service_identity, reason, updated_at_utc
            FROM contracts.producer_registration
            WHERE identity = @producer_identity
            """ + (forUpdate ? " FOR UPDATE;" : ";");
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<string>("producer_identity", producerIdentity));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? RestoreSnapshot(
                reader.GetString(0),
                reader.GetBoolean(1),
                reader.GetFieldValue<int[]>(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7))
            : null;
    }
}
