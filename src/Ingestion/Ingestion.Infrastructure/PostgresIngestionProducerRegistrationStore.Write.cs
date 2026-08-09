using Aggregator.Ingestion.Application;
using Npgsql;

namespace Aggregator.Ingestion.Infrastructure;

public sealed partial class PostgresIngestionProducerRegistrationStore
{
    private static async Task InsertRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IngestionProducerRegistrationSnapshot registration,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO contracts.producer_registration_revision
            (
                producer_identity, aggregate_revision, active, supported_contract_revisions,
                content_digest, changed_by_service_identity, reason, changed_at_utc
            )
            VALUES
            (
                @producer_identity, @aggregate_revision, @active, @supported_contract_revisions,
                @content_digest, @changed_by_service_identity, @reason, @changed_at_utc
            );
            """,
            connection,
            transaction);
        AddRegistrationParameters(command, registration);
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IngestionProducerRegistrationSnapshot registration,
        bool create,
        long expectedAggregateRevision,
        CancellationToken cancellationToken)
    {
        var sql = create
            ? """
              INSERT INTO contracts.producer_registration
              (
                  identity, active, supported_contract_revisions, aggregate_revision,
                  content_digest, updated_by_service_identity, reason, updated_at_utc
              )
              VALUES
              (
                  @producer_identity, @active, @supported_contract_revisions, @aggregate_revision,
                  @content_digest, @changed_by_service_identity, @reason, @changed_at_utc
              );
              """
            : """
              UPDATE contracts.producer_registration
              SET active = @active,
                  supported_contract_revisions = @supported_contract_revisions,
                  aggregate_revision = @aggregate_revision,
                  content_digest = @content_digest,
                  updated_by_service_identity = @changed_by_service_identity,
                  reason = @reason,
                  updated_at_utc = @changed_at_utc
              WHERE identity = @producer_identity
                AND aggregate_revision = @expected_aggregate_revision;
              """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddRegistrationParameters(command, registration);
        if (!create)
        {
            command.Parameters.Add(new NpgsqlParameter<long>(
                "expected_aggregate_revision",
                expectedAggregateRevision));
        }

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw RevisionConflict(registration.ProducerIdentity, expectedAggregateRevision, null);
        }
    }

    private static async Task InsertCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IngestionProducerRegistrationMutation mutation,
        byte[] resultDocument,
        string resultDigest,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO operations.producer_registration_command
            (
                scope, key, request_digest, producer_identity, result_document,
                result_digest, caller_service_identity, created_at_utc
            )
            VALUES
            (
                @scope, @key, @request_digest, @producer_identity, @result_document,
                @result_digest, @caller_service_identity, @created_at_utc
            );
            """,
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<string>("scope", mutation.CommandIdentity.Scope));
        command.Parameters.Add(new NpgsqlParameter<string>("key", mutation.CommandIdentity.Key));
        command.Parameters.Add(new NpgsqlParameter<string>(
            "request_digest",
            mutation.CommandIdentity.RequestDigest));
        command.Parameters.Add(new NpgsqlParameter<string>(
            "producer_identity",
            mutation.Registration.ProducerIdentity));
        command.Parameters.Add(new NpgsqlParameter<byte[]>("result_document", resultDocument));
        command.Parameters.Add(new NpgsqlParameter<string>("result_digest", resultDigest));
        command.Parameters.Add(new NpgsqlParameter<string>(
            "caller_service_identity",
            mutation.CallerServiceIdentity));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>(
            "created_at_utc",
            mutation.Registration.UpdatedAtUtc));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddRegistrationParameters(
        NpgsqlCommand command,
        IngestionProducerRegistrationSnapshot registration)
    {
        command.Parameters.Add(new NpgsqlParameter<string>(
            "producer_identity",
            registration.ProducerIdentity));
        command.Parameters.Add(new NpgsqlParameter<bool>("active", registration.Active));
        command.Parameters.Add(new NpgsqlParameter<int[]>(
            "supported_contract_revisions",
            registration.SupportedContractRevisions.ToArray()));
        command.Parameters.Add(new NpgsqlParameter<long>(
            "aggregate_revision",
            registration.AggregateRevision));
        command.Parameters.Add(new NpgsqlParameter<string>("content_digest", registration.ContentDigest));
        command.Parameters.Add(new NpgsqlParameter<string>(
            "changed_by_service_identity",
            registration.UpdatedByServiceIdentity));
        command.Parameters.Add(new NpgsqlParameter<string>("reason", registration.Reason));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>(
            "changed_at_utc",
            registration.UpdatedAtUtc));
    }
}
