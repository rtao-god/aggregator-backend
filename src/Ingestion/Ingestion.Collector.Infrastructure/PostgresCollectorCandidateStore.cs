using System.Data;
using Aggregator.Ingestion.Collector.Application;
using Aggregator.Ingestion.Collector.Domain;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Aggregator.Ingestion.Collector.Infrastructure;

public static class CollectorCandidateInfrastructureExtensions
{
    public static IServiceCollection AddCollectorCandidateInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddScoped<ICollectorCandidateStore, PostgresCollectorCandidateStore>();
        services.AddSingleton<ICollectorCandidateIdSource, UuidV7CollectorCandidateIdSource>();
        return services;
    }
}

public sealed class PostgresCollectorCandidateStore : ICollectorCandidateStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresCollectorCandidateStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<CollectorCandidateRegistration> RegisterAsync(
        Guid commandId,
        string commandDigest,
        CollectorCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (commandId == Guid.Empty)
        {
            throw new ArgumentException("Command ID is required.", nameof(commandId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(commandDigest);
        ArgumentNullException.ThrowIfNull(candidate);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var replay = await ReadCommandAsync(
            connection,
            transaction,
            commandId,
            cancellationToken);
        if (replay is not null)
        {
            if (!string.Equals(replay.Value.CommandDigest, commandDigest, StringComparison.Ordinal))
            {
                throw Failure(
                    "COLLECTOR_COMMAND_ID_REUSED",
                    409,
                    $"Collector command '{commandId}' was already committed with different content.",
                    "Generate a new command ID for changed content; replay only the exact original command.");
            }

            var existing = await ReadCandidateAsync(
                connection,
                transaction,
                replay.Value.CandidateId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CollectorCandidateRegistration(existing, Replayed: true);
        }

        var duplicate = await ReadSourceCandidateAsync(
            connection,
            transaction,
            candidate.SourceSystem,
            candidate.ExternalId,
            cancellationToken);
        if (duplicate is not null)
        {
            if (!string.Equals(
                    duplicate.ContentDigest,
                    candidate.ContentDigest,
                    StringComparison.Ordinal))
            {
                throw Failure(
                    "COLLECTOR_SOURCE_IDENTITY_CONFLICT",
                    409,
                    $"Source identity '{candidate.SourceSystem}/{candidate.ExternalId}' already resolves to different content.",
                    "Submit the source update through an explicit candidate revision workflow.");
            }

            await InsertCommandAsync(
                connection,
                transaction,
                commandId,
                commandDigest,
                duplicate.CandidateId,
                candidate.AcceptedAtUtc,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CollectorCandidateRegistration(duplicate, Replayed: true);
        }

        await InsertCandidateAsync(connection, transaction, candidate, cancellationToken);
        await InsertCommandAsync(
            connection,
            transaction,
            commandId,
            commandDigest,
            candidate.CandidateId,
            candidate.AcceptedAtUtc,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CollectorCandidateRegistration(candidate, Replayed: false);
    }

    public async Task<bool> CheckReadinessAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var command = _dataSource.CreateCommand(
                "SELECT EXISTS (SELECT 1 FROM ingestion.collector_candidate LIMIT 1);");
            _ = await command.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return false;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }

    private static async Task<(string CommandDigest, Guid CandidateId)?> ReadCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT command_digest, candidate_id
            FROM ingestion.collector_command
            WHERE command_id = @command_id
            FOR SHARE;
            """, connection, transaction);
        command.Parameters.AddWithValue("command_id", NpgsqlDbType.Uuid, commandId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (reader.GetString(0), reader.GetGuid(1));
    }

    private static async Task<CollectorCandidate?> ReadSourceCandidateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sourceSystem,
        string externalId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT candidate_id,
                   subject_id,
                   subject_revision_id,
                   source_system,
                   source_reference,
                   observed_at_utc,
                   kind,
                   external_id,
                   title,
                   website,
                   hourly_price,
                   evidence_digest,
                   content_digest,
                   accepted_at_utc
            FROM ingestion.collector_candidate
            WHERE source_system = @source_system
              AND external_id = @external_id
            FOR SHARE;
            """, connection, transaction);
        command.Parameters.AddWithValue("source_system", NpgsqlDbType.Varchar, sourceSystem);
        command.Parameters.AddWithValue("external_id", NpgsqlDbType.Varchar, externalId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? MapCandidate(reader)
            : null;
    }

    private static async Task<CollectorCandidate> ReadCandidateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid candidateId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT candidate_id,
                   subject_id,
                   subject_revision_id,
                   source_system,
                   source_reference,
                   observed_at_utc,
                   kind,
                   external_id,
                   title,
                   website,
                   hourly_price,
                   evidence_digest,
                   content_digest,
                   accepted_at_utc
            FROM ingestion.collector_candidate
            WHERE candidate_id = @candidate_id;
            """, connection, transaction);
        command.Parameters.AddWithValue("candidate_id", NpgsqlDbType.Uuid, candidateId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"Collector command references missing candidate '{candidateId}'.");
        }

        return MapCandidate(reader);
    }

    private static CollectorCandidate MapCandidate(NpgsqlDataReader reader) =>
        CollectorCandidate.Create(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            (CollectorCandidateKind)reader.GetInt32(6),
            reader.GetString(7),
            reader.GetString(8),
            new Uri(reader.GetString(9), UriKind.Absolute),
            reader.IsDBNull(10) ? null : reader.GetDecimal(10),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetFieldValue<DateTimeOffset>(13));

    private static async Task InsertCandidateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CollectorCandidate candidate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO ingestion.collector_candidate
            (
                candidate_id,
                subject_id,
                subject_revision_id,
                source_system,
                source_reference,
                observed_at_utc,
                kind,
                external_id,
                title,
                website,
                hourly_price,
                evidence_digest,
                content_digest,
                accepted_at_utc
            )
            VALUES
            (
                @candidate_id,
                @subject_id,
                @subject_revision_id,
                @source_system,
                @source_reference,
                @observed_at_utc,
                @kind,
                @external_id,
                @title,
                @website,
                @hourly_price,
                @evidence_digest,
                @content_digest,
                @accepted_at_utc
            );
            """, connection, transaction);
        command.Parameters.AddWithValue("candidate_id", NpgsqlDbType.Uuid, candidate.CandidateId);
        command.Parameters.AddWithValue("subject_id", NpgsqlDbType.Uuid, candidate.SubjectId);
        command.Parameters.AddWithValue("subject_revision_id", NpgsqlDbType.Uuid, candidate.SubjectRevisionId);
        command.Parameters.AddWithValue("source_system", NpgsqlDbType.Varchar, candidate.SourceSystem);
        command.Parameters.AddWithValue("source_reference", NpgsqlDbType.Varchar, candidate.SourceReference);
        command.Parameters.AddWithValue("observed_at_utc", NpgsqlDbType.TimestampTz, candidate.ObservedAtUtc);
        command.Parameters.AddWithValue("kind", NpgsqlDbType.Integer, (int)candidate.Kind);
        command.Parameters.AddWithValue("external_id", NpgsqlDbType.Varchar, candidate.ExternalId);
        command.Parameters.AddWithValue("title", NpgsqlDbType.Varchar, candidate.Title);
        command.Parameters.AddWithValue("website", NpgsqlDbType.Varchar, candidate.Website.AbsoluteUri);
        command.Parameters.AddWithValue(
            "hourly_price",
            NpgsqlDbType.Numeric,
            candidate.HourlyPrice is { } hourlyPrice ? hourlyPrice : DBNull.Value);
        command.Parameters.AddWithValue("evidence_digest", NpgsqlDbType.Char, candidate.EvidenceDigest);
        command.Parameters.AddWithValue("content_digest", NpgsqlDbType.Char, candidate.ContentDigest);
        command.Parameters.AddWithValue("accepted_at_utc", NpgsqlDbType.TimestampTz, candidate.AcceptedAtUtc);
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid commandId,
        string commandDigest,
        Guid candidateId,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO ingestion.collector_command
            (command_id, command_digest, candidate_id, committed_at_utc)
            VALUES
            (@command_id, @command_digest, @candidate_id, @committed_at_utc);
            """, connection, transaction);
        command.Parameters.AddWithValue("command_id", NpgsqlDbType.Uuid, commandId);
        command.Parameters.AddWithValue("command_digest", NpgsqlDbType.Char, commandDigest);
        command.Parameters.AddWithValue("candidate_id", NpgsqlDbType.Uuid, candidateId);
        command.Parameters.AddWithValue("committed_at_utc", NpgsqlDbType.TimestampTz, committedAtUtc);
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static CollectorCandidateException Failure(
        string code,
        int statusCode,
        string message,
        string requiredAction) =>
        new(code, statusCode, message, requiredAction);
}
