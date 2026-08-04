using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Domain;
using Aggregator.Ingestion.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

var root = FindRepositoryRoot(AppContext.BaseDirectory);
var applicationAssembly = typeof(IngestionPackageValidator).Assembly;
var contractsAssembly = typeof(Aggregator.Ingestion.Contracts.AggregatorCandidateIngestionManifest).Assembly;
var domainAssembly = typeof(ImportBatch).Assembly;
var infrastructureAssembly = typeof(IngestionDbContext).Assembly;
var validatorType = typeof(IngestionPackageValidator);
var batchType = typeof(ImportBatch);
var snapshotType = typeof(IngestionBatchSnapshot);
var decisionType = typeof(ImportItemDecision);

var validatorMethod = validatorType
    .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
    .Where(method => method.Name.Contains("Validate", StringComparison.OrdinalIgnoreCase))
    .Where(method => method.GetParameters().Any(parameter => parameter.ParameterType == batchType))
    .OrderByDescending(method => method.Name.Equals("Validate", StringComparison.Ordinal))
    .ThenByDescending(method => method.GetParameters().Length)
    .FirstOrDefault()
    ?? throw Failure(
        "IngestionPackageValidator exposes no public typed validation method that owns an ImportBatch transition.");
var validatorParameters = validatorMethod.GetParameters();
var contractParameters = validatorParameters
    .Where(parameter => parameter.ParameterType.Assembly == contractsAssembly)
    .ToArray();
if (contractParameters.Length == 0)
{
    throw Failure("Ingestion package validation method has no backend-owned ingestion contract parameter.");
}
var packageType = FindPackageRoot(contractsAssembly, contractParameters)
    ?? throw Failure(
        "No public ingestion package contract can supply every contract parameter required by the validator.");
var validationResultType = UnwrapAwaitable(validatorMethod.ReturnType) ?? validatorMethod.ReturnType;
var decisionsAccessor = FindDecisionAccessor(validationResultType, decisionType)
    ?? throw Failure(
        $"Validation result '{validationResultType}' exposes no typed collection of ImportItemDecision.");
var restoreMethod = batchType.GetMethods(BindingFlags.Public | BindingFlags.Static)
    .Where(method => method.Name == "Restore" && method.ReturnType == batchType)
    .OrderByDescending(method => method.GetParameters().Length)
    .FirstOrDefault()
    ?? throw Failure("ImportBatch has no public Restore factory for persisted snapshots.");
var failureMethod = batchType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
    .Where(method => method.Name.Contains("Fail", StringComparison.OrdinalIgnoreCase))
    .Where(method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(string)))
    .OrderByDescending(method => method.Name.Contains("Validation", StringComparison.OrdinalIgnoreCase))
    .ThenByDescending(method => method.GetParameters().Length)
    .FirstOrDefault()
    ?? throw Failure(
        "ImportBatch has no public explicit failure transition for exhausted validation attempts.");
var itemKeyProperty = decisionType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
    .Where(property => property.PropertyType == typeof(string))
    .OrderByDescending(property => property.Name.Equals("ItemKey", StringComparison.Ordinal))
    .ThenByDescending(property => property.Name.Contains("Item", StringComparison.Ordinal))
    .FirstOrDefault()
    ?? throw Failure("ImportItemDecision exposes no stable string item identity.");

var infrastructure = Path.Combine(root.FullName, "src", "Ingestion", "Ingestion.Infrastructure");
var application = Path.Combine(root.FullName, "src", "Ingestion", "Ingestion.Application");
var worker = Path.Combine(root.FullName, "src", "Ingestion", "Ingestion.Worker");
var workerTests = Path.Combine(root.FullName, "tests", "Ingestion", "Ingestion.Worker.Tests");
var migrations = Path.Combine(root.FullName, "src", "Ingestion", "Ingestion.Migrations", "Migrations");
Directory.CreateDirectory(infrastructure);
Directory.CreateDirectory(application);
Directory.CreateDirectory(worker);
Directory.CreateDirectory(workerTests);
Directory.CreateDirectory(migrations);

File.WriteAllText(
    Path.Combine(application, "IngestionValidationContracts.cs"),
    GenerateApplicationContracts());
File.WriteAllText(
    Path.Combine(infrastructure, "IngestionValidationQueue.cs"),
    GenerateQueueSource());
File.WriteAllText(
    Path.Combine(worker, "Ingestion.Worker.csproj"),
    GenerateWorkerProject());
File.WriteAllText(
    Path.Combine(worker, "IngestionWorkerOptions.cs"),
    GenerateWorkerOptions());
File.WriteAllText(
    Path.Combine(worker, "GeneratedIngestionValidationService.cs"),
    GenerateValidationService(
        validatorMethod,
        packageType,
        contractParameters,
        decisionsAccessor,
        restoreMethod,
        failureMethod,
        itemKeyProperty));
File.WriteAllText(
    Path.Combine(worker, "IngestionValidationWorker.cs"),
    GenerateHostedWorker());
File.WriteAllText(
    Path.Combine(worker, "Program.cs"),
    GenerateWorkerProgram());
File.WriteAllText(
    Path.Combine(workerTests, "Ingestion.Worker.Tests.csproj"),
    GenerateWorkerTestProject());
File.WriteAllText(Path.Combine(workerTests, "Usings.cs"), "global using Xunit;" + Environment.NewLine);
File.WriteAllText(
    Path.Combine(workerTests, "IngestionWorkerOptionsTests.cs"),
    GenerateWorkerOptionsTests());
File.WriteAllText(
    Path.Combine(migrations, "V002__ingestion_validation_work_and_decisions.sql"),
    GenerateMigrationSql());
var reportDirectory = Path.Combine(root.FullName, "docs", "generated");
Directory.CreateDirectory(reportDirectory);
File.WriteAllText(
    Path.Combine(reportDirectory, "ingestion-worker-generation.md"),
    $"""
    # Ingestion worker generation

    - Package contract: `{packageType.FullName}`.
    - Validator: `{validatorType.FullName}.{validatorMethod.Name}`.
    - Decision source: `{decisionsAccessor}`.
    - Aggregate restore: `ImportBatch.{restoreMethod.Name}`.
    - Terminal failure transition: `ImportBatch.{failureMethod.Name}`.
    - Stable item identity: `ImportItemDecision.{itemKeyProperty.Name}`.
    - Work claiming is PostgreSQL-backed with bounded leases and attempts.
    - Decision documents are immutable, digest-verified, and unique by batch plus item key.
    """ + Environment.NewLine);

string GenerateApplicationContracts() =>
    """
    using Aggregator.Ingestion.Domain;

    namespace Aggregator.Ingestion.Application;

    public sealed record IngestionValidationLease(
        Guid BatchId,
        Guid LeaseToken,
        int AttemptCount,
        DateTimeOffset LeaseExpiresAtUtc,
        IngestionBatchSnapshot Snapshot);

    public sealed record IngestionDecisionDocument(
        int Ordinal,
        string ItemKey,
        string CanonicalJson,
        string ContentDigest);

    public interface IIngestionValidationQueue
    {
        Task<IngestionValidationLease?> TryLeaseUploadedAsync(
            string workerIdentity,
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            int maximumAttempts,
            CancellationToken cancellationToken);

        Task CompleteAsync(
            IngestionValidationLease lease,
            ImportBatch batch,
            IReadOnlyList<IngestionDecisionDocument> decisions,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken);

        Task<int> RecordFailureAsync(
            IngestionValidationLease lease,
            string error,
            DateTimeOffset failedAtUtc,
            CancellationToken cancellationToken);

        Task CompleteTerminalFailureAsync(
            IngestionValidationLease lease,
            ImportBatch batch,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken);
    }
    """ + Environment.NewLine;

string GenerateQueueSource() =>
    """
    using System.Data;
    using System.Globalization;
    using Aggregator.Ingestion.Application;
    using Aggregator.Ingestion.Domain;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.EntityFrameworkCore.Metadata;
    using Npgsql;
    using NpgsqlTypes;

    namespace Aggregator.Ingestion.Infrastructure;

    public sealed class IngestionValidationQueue(
        IngestionDbContext dbContext,
        EfIngestionRepository batchRepository) : IIngestionValidationQueue
    {
        public async Task<IngestionValidationLease?> TryLeaseUploadedAsync(
            string workerIdentity,
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            int maximumAttempts,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(workerIdentity);
            RequireUtc(nowUtc, nameof(nowUtc));
            if (leaseDuration < TimeSpan.FromSeconds(10) || leaseDuration > TimeSpan.FromMinutes(30))
            {
                throw new ArgumentOutOfRangeException(nameof(leaseDuration));
            }

            if (maximumAttempts is < 1 or > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
            }

            var identifiers = BatchIdentifiers();
            var leaseToken = Guid.CreateVersion7();
            var leaseExpiresAtUtc = nowUtc.Add(leaseDuration);
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            command.Transaction = (NpgsqlTransaction)transaction.GetDbTransaction();
            command.CommandText = $"""
                WITH candidate AS
                (
                    SELECT batch.{identifiers.IdColumn}
                    FROM {identifiers.QualifiedTable} AS batch
                    LEFT JOIN operations.validation_work AS work
                        ON work.batch_id = batch.{identifiers.IdColumn}
                    WHERE batch.{ identifiers.StateColumn } = @uploaded_state
                      AND(work.completed_at_utc IS NULL)
                      AND(work.lease_expires_at_utc IS NULL OR work.lease_expires_at_utc <= @now_utc)
< @maximum_attempts
                    ORDER BY batch.{identifiers.RegisteredAtColumn}, batch.{ identifiers.IdColumn}
FOR UPDATE OF batch SKIP LOCKED
                    LIMIT 1
                )
                INSERT INTO operations.validation_work
                    (batch_id, lease_token, leased_by, lease_expires_at_utc, attempt_count, last_error,
 last_failed_at_utc, completed_at_utc)
                SELECT candidate.{identifiers.IdColumn}, @lease_token, @leased_by, @lease_expires_at_utc,
                       COALESCE(existing.attempt_count, 0), existing.last_error, existing.last_failed_at_utc, NULL
                FROM candidate
                LEFT JOIN operations.validation_work AS existing
                    ON existing.batch_id = candidate.{identifiers.IdColumn}
                DO UPDATE SET
                    lease_token = EXCLUDED.lease_token,
                    leased_by = EXCLUDED.leased_by,
                    lease_expires_at_utc = EXCLUDED.lease_expires_at_utc
                WHERE operations.validation_work.completed_at_utc IS NULL
                  AND (operations.validation_work.lease_expires_at_utc IS NULL
                       OR operations.validation_work.lease_expires_at_utc <= @now_utc)
                  AND operations.validation_work.attempt_count < @maximum_attempts
                RETURNING batch_id, attempt_count;
""";
            command.Parameters.AddWithValue("uploaded_state", NpgsqlDbType.Integer, (int)ImportBatchState.Uploaded);
command.Parameters.AddWithValue("now_utc", NpgsqlDbType.TimestampTz, nowUtc);
command.Parameters.AddWithValue("maximum_attempts", NpgsqlDbType.Integer, maximumAttempts);
command.Parameters.AddWithValue("lease_token", NpgsqlDbType.Uuid, leaseToken);
command.Parameters.AddWithValue("leased_by", NpgsqlDbType.Text, workerIdentity.Trim());
command.Parameters.AddWithValue("lease_expires_at_utc", NpgsqlDbType.TimestampTz, leaseExpiresAtUtc);
Guid? batchId = null;
var attemptCount = 0;
await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
{
    if (await reader.ReadAsync(cancellationToken))
    {
        batchId = reader.GetGuid(0);
        attemptCount = reader.GetInt32(1);
    }
}

await transaction.CommitAsync(cancellationToken);
if (batchId is null)
{
    return null;
}

var snapshot = await batchRepository.ReadAsync(ImportBatchId.Create(batchId.Value), cancellationToken)
    ?? throw Failure(
        "INGESTION_VALIDATION_BATCH_DISAPPEARED",
        $"Leased import batch '{batchId}' disappeared before validation.",
        "Restore the batch row or clear its validation work record through an owner migration.");
return new IngestionValidationLease(
    batchId.Value,
    leaseToken,
    attemptCount,
    leaseExpiresAtUtc,
    snapshot);
        }

private void EnsureLease(
    Guid batchId,
    IngestionValidationLease lease,
    DateTimeOffset nowUtc,
    CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    if (batchId != lease.BatchId || lease.LeaseExpiresAtUtc <= nowUtc)
    {
        throw Failure(
            "INGESTION_VALIDATION_STALE_LEASE",
            "Validation completion was produced by an expired or mismatched lease.",
            "Discard the result and reacquire the exact import batch.");
    }
}

private static void EnsureAggregateRevision(
    ImportBatchRow row,
    IngestionValidationLease lease,
    ImportBatch batch)
{
    if (row.AggregateRevision != lease.Snapshot.AggregateRevision ||
        batch.AggregateRevision <= row.AggregateRevision)
    {
        throw Failure(
            "INGESTION_VALIDATION_REVISION_CONFLICT",
            "Import batch changed while validation was in progress or validation produced no domain transition.",
            "Reload and validate the current aggregate revision.");
    }
}

private static void Apply(ImportBatchRow row, ImportBatch batch)
{
    row.LastChangedAtUtc = batch.LastChangedAtUtc;
    row.State = (int)batch.State;
    row.AggregateRevision = batch.AggregateRevision;
    row.AcceptedItemCount = batch.AcceptedItemCount;
    row.ReviewRequiredItemCount = batch.ReviewRequiredItemCount;
    row.RejectedItemCount = batch.RejectedItemCount;
    row.FailureCode = batch.FailureCode;
}

private async Task InsertDecisionsAsync(
    Guid batchId,
    IReadOnlyList<IngestionDecisionDocument> decisions,
    DateTimeOffset recordedAtUtc,
    Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
    CancellationToken cancellationToken)
{
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    foreach (var decision in decisions.OrderBy(item => item.Ordinal))
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (NpgsqlTransaction)transaction.GetDbTransaction();
        command.CommandText = """
                    INSERT INTO batches.item_decision
                        (batch_id, ordinal, item_key, decision_json, decision_digest, recorded_at_utc)
                    VALUES
                        (@batch_id, @ordinal, @item_key, @decision_json::jsonb, @decision_digest, @recorded_at_utc);
                    """;
        command.Parameters.AddWithValue("batch_id", NpgsqlDbType.Uuid, batchId);
        command.Parameters.AddWithValue("ordinal", NpgsqlDbType.Integer, decision.Ordinal);
        command.Parameters.AddWithValue("item_key", NpgsqlDbType.Text, decision.ItemKey);
        command.Parameters.AddWithValue("decision_json", NpgsqlDbType.Text, decision.CanonicalJson);
        command.Parameters.AddWithValue("decision_digest", NpgsqlDbType.Char, decision.ContentDigest);
        command.Parameters.AddWithValue("recorded_at_utc", NpgsqlDbType.TimestampTz, recordedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

private async Task MarkCompletedAsync(
    IngestionValidationLease lease,
    DateTimeOffset completedAtUtc,
    Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
    CancellationToken cancellationToken)
{
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    await using var command = connection.CreateCommand();
    command.Transaction = (NpgsqlTransaction)transaction.GetDbTransaction();
    command.CommandText = """
                UPDATE operations.validation_work
                SET completed_at_utc = @completed_at_utc,
                    lease_token = NULL,
                    leased_by = NULL,
                    lease_expires_at_utc = NULL
                WHERE batch_id = @batch_id
                  AND lease_token = @lease_token
                  AND completed_at_utc IS NULL;
                """;
    command.Parameters.AddWithValue("completed_at_utc", NpgsqlDbType.TimestampTz, completedAtUtc);
    command.Parameters.AddWithValue("batch_id", NpgsqlDbType.Uuid, lease.BatchId);
    command.Parameters.AddWithValue("lease_token", NpgsqlDbType.Uuid, lease.LeaseToken);
    if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
    {
        throw Failure(
            "INGESTION_VALIDATION_STALE_LEASE",
            "Validation completion cannot advance a stale work lease.",
            "Discard the stale worker result and reacquire the batch.");
    }
}

private static string Quote(string identifier)
{
    if (string.IsNullOrWhiteSpace(identifier) ||
        identifier.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '_')))
    {
        throw Failure(
            "INGESTION_VALIDATION_IDENTIFIER_INVALID",
            "Ingestion persistence metadata contains an unsafe SQL identifier.",
            "Correct the owner EF mapping before executing validation work.");
    }

    return $"\"{identifier}\"";
}

private static void RequireUtc(DateTimeOffset value, string parameterName)
{
    if (value.Offset != TimeSpan.Zero)
    {
        throw new ArgumentException("Timestamp must be UTC.", parameterName);
    }
}

private static IngestionApplicationException Failure(
    string code,
    string message,
    string requiredAction) =>
    new("Ingestion.ValidationPersistence", code, 500, message, requiredAction);

private sealed record BatchStoreIdentifiers(
    string QualifiedTable,
    string IdColumn,
    string StateColumn,
    string RegisteredAtColumn);
    }
    """ + Environment.NewLine;

string GenerateWorkerProject() =>
    """
    <Project Sdk="Microsoft.NET.Sdk.Worker">
      <ItemGroup><PackageReference Include="Microsoft.Extensions.Hosting" /></ItemGroup>
      <ItemGroup>
        <ProjectReference Include="../Ingestion.Application/Ingestion.Application.csproj" />
        <ProjectReference Include="../Ingestion.Contracts/Ingestion.Contracts.csproj" />
        <ProjectReference Include="../Ingestion.Domain/Ingestion.Domain.csproj" />
        <ProjectReference Include="../Ingestion.Infrastructure/Ingestion.Infrastructure.csproj" />
        <ProjectReference Include="../../BuildingBlocks/Platform.ObjectStorage/Platform.ObjectStorage.csproj" />
        <ProjectReference Include="../../BuildingBlocks/Platform.Observability/Platform.Observability.csproj" />
      </ItemGroup>
    </Project>
    """ + Environment.NewLine;

string GenerateWorkerOptions() =>
    """
    using Microsoft.Extensions.Configuration;

    namespace Aggregator.Ingestion.Worker;

    public sealed record IngestionWorkerOptions
    {
        public const string SectionName = "IngestionWorker";

        public required string WorkerIdentity { get; init; }

        public int MaximumAttempts { get; init; } = 8;

        public long MaximumPayloadBytes { get; init; } = 128L * 1024L * 1024L;

        public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(5);

        public TimeSpan EmptyDelay { get; init; } = TimeSpan.FromSeconds(2);

        public static IngestionWorkerOptions FromConfiguration(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var options = new IngestionWorkerOptions
            {
                WorkerIdentity = configuration[$"{SectionName}:WorkerIdentity"]
                    ?? throw new InvalidOperationException($"{SectionName}:WorkerIdentity is required."),
                MaximumAttempts = ReadInt(configuration, $"{SectionName}:MaximumAttempts", 8),
                MaximumPayloadBytes = ReadLong(configuration, $"{SectionName}:MaximumPayloadBytes", 128L * 1024L * 1024L),
                LeaseDuration = TimeSpan.FromSeconds(ReadInt(configuration, $"{SectionName}:LeaseDurationSeconds", 300)),
                EmptyDelay = TimeSpan.FromMilliseconds(ReadInt(configuration, $"{SectionName}:EmptyDelayMilliseconds", 2000)),
            };
            options.Validate();
            return options;
        }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(WorkerIdentity) || WorkerIdentity.Length > 200 || WorkerIdentity.Any(char.IsControl))
            {
                throw new ArgumentException("Ingestion worker identity is invalid.", nameof(WorkerIdentity));
            }
            if (MaximumAttempts is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(MaximumAttempts));
            if (MaximumPayloadBytes is < 1 or > 1024L * 1024L * 1024L)
                throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes));
            if (LeaseDuration < TimeSpan.FromSeconds(10) || LeaseDuration > TimeSpan.FromMinutes(30))
                throw new ArgumentOutOfRangeException(nameof(LeaseDuration));
            if (EmptyDelay < TimeSpan.FromMilliseconds(100) || EmptyDelay > TimeSpan.FromMinutes(5))
                throw new ArgumentOutOfRangeException(nameof(EmptyDelay));
        }

        private static int ReadInt(IConfiguration configuration, string path, int fallback) =>
            configuration[path] is null ? fallback : int.TryParse(configuration[path], out var value)
                ? value : throw new InvalidOperationException($"{path} must be an integer.");

        private static long ReadLong(IConfiguration configuration, string path, long fallback) =>
            configuration[path] is null ? fallback : long.TryParse(configuration[path], out var value)
                ? value : throw new InvalidOperationException($"{path} must be an integer.");
    }
    """ + Environment.NewLine;

string GenerateValidationService(
    MethodInfo method,
    Type rootPackageType,
    ParameterInfo[] contractMethodParameters,
    string decisionAccessor,
    MethodInfo restore,
    MethodInfo failMethod,
    PropertyInfo keyProperty)
{
    var validatorArguments = string.Join(",\n                ",
        method.GetParameters().Select(parameter => MapValidatorArgument(parameter, rootPackageType)));
    var restoreArguments = string.Join(",\n                ",
        restore.GetParameters().Select(MapRestoreArgument));
    var failureArguments = string.Join(",\n                        ",
        failMethod.GetParameters().Select(MapFailureArgument));
    var invocation = IsAwaitable(method.ReturnType)
        ? $"var validation = await validator.{method.Name}(\n                {validatorArguments});"
        : $"var validation = validator.{method.Name}(\n                {validatorArguments});";
    var decisions = decisionAccessor == "self" ? "validation" : $"validation.{decisionAccessor}";
    var packageTypeName = TypeName(rootPackageType);
    return $$"""
    using System.Text;
    using Aggregator.Ingestion.Application;
    using Aggregator.Ingestion.Domain;
    using Aggregator.Ingestion.Infrastructure;
    using Platform.ObjectStorage;

    namespace Aggregator.Ingestion.Worker;

    public sealed class GeneratedIngestionValidationService(
        IngestionPackageValidator validator,
        IIngestionValidationQueue queue,
        IObjectStore objectStore,
        IngestionWorkerOptions options,
        ILogger<GeneratedIngestionValidationService> logger)
    {
        public async Task<bool> ProcessOneAsync(CancellationToken cancellationToken)
        {
            var nowUtc = TimeProvider.System.GetUtcNow();
            var lease = await queue.TryLeaseUploadedAsync(
                options.WorkerIdentity,
                nowUtc,
                options.LeaseDuration,
                options.MaximumAttempts,
                cancellationToken);
            if (lease is null)
            {
                return false;
            }

            try
            {
                var snapshot = lease.Snapshot;
                var objectKey = snapshot.PayloadObjectKey
                    ?? throw Failure("INGESTION_PAYLOAD_OBJECT_KEY_MISSING", "Uploaded batch has no object key.");
                var objectDigest = snapshot.PayloadObjectDigest
                    ?? throw Failure("INGESTION_PAYLOAD_OBJECT_DIGEST_MISSING", "Uploaded batch has no object digest.");
                await using var source = await objectStore.OpenReadVerifiedAsync(
                    objectKey,
                    objectDigest,
                    cancellationToken);
                await using var memory = new MemoryStream();
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    if (memory.Length + read > options.MaximumPayloadBytes)
                    {
                        throw Failure("INGESTION_PAYLOAD_TOO_LARGE", "Uploaded payload exceeds the worker limit.");
                    }
                    await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                var package = IngestionCanonicalJson.Deserialize<{{packageTypeName}}>(memory.ToArray());
                var batch = ImportBatch.Restore(
                {{restoreArguments}});
                {{invocation}}
                var decisionDocuments = {{decisions}}
                    .OrderBy(item => item.{{keyProperty.Name}}, StringComparer.Ordinal)
                    .Select((item, ordinal) =>
                    {
                        var document = IngestionCanonicalJson.Serialize(item);
                        return new IngestionDecisionDocument(
                            ordinal + 1,
                            item.{{keyProperty.Name}},
                            Encoding.UTF8.GetString(document),
                            IngestionDocumentDigest.Compute(document));
                    })
                    .ToArray();
                await queue.CompleteAsync(
                    lease,
                    batch,
                    decisionDocuments,
                    TimeProvider.System.GetUtcNow(),
                    cancellationToken);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var failedAtUtc = TimeProvider.System.GetUtcNow();
                var attempts = await queue.RecordFailureAsync(
                    lease,
                    exception.Message,
                    failedAtUtc,
                    cancellationToken);
                logger.LogError(exception,
                    "Ingestion validation failed for batch {BatchId} on attempt {AttemptCount}",
                    lease.BatchId,
                    attempts);
                if (attempts >= options.MaximumAttempts ||
                    exception is IngestionApplicationException or IngestionDomainException)
                {
                    var batch = ImportBatch.Restore(
                    {{restoreArguments}});
                    batch.{{failMethod.Name}}(
                        {{failureArguments}});
                    await queue.CompleteTerminalFailureAsync(
                        lease with {{AttemptCount = attempts}},
                        batch,
                        failedAtUtc,
                        cancellationToken);
                }
                return true;
            }
        }

        private static IngestionApplicationException Failure(string code, string message) =>
            new("Ingestion.ValidationWorker", code, 422, message,
                "Correct or replace the sealed collector package before retrying.");
    }
    """ + Environment.NewLine;

    string MapValidatorArgument(ParameterInfo parameter)
    {
        if (parameter.ParameterType == batchType) return "batch";
        if (parameter.ParameterType == rootPackageType) return "package";
        if (parameter.ParameterType == typeof(CancellationToken)) return "cancellationToken";
        if (parameter.ParameterType == typeof(DateTimeOffset)) return "TimeProvider.System.GetUtcNow()";
        if (parameter.ParameterType == typeof(TimeProvider)) return "TimeProvider.System";
        if (parameter.ParameterType.Assembly == contractsAssembly)
        {
            var property = rootPackageType.GetProperties()
                .Where(candidate => parameter.ParameterType.IsAssignableFrom(candidate.PropertyType))
                .OrderByDescending(candidate => candidate.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault()
                ?? throw Failure($"Package contract '{rootPackageType}' cannot supply validator parameter '{parameter.Name}'.");
            return $"package.{property.Name}";
        }
        throw Failure($"Cannot map validator parameter '{parameter.Name}' of type '{parameter.ParameterType}'.");
    }

    string MapRestoreArgument(ParameterInfo parameter)
    {
        var property = snapshotType.GetProperties()
            .Where(candidate => parameter.ParameterType.IsAssignableFrom(candidate.PropertyType) ||
                candidate.PropertyType.IsAssignableFrom(parameter.ParameterType))
            .OrderByDescending(candidate => Normalize(candidate.Name) == Normalize(parameter.Name ?? string.Empty))
            .ThenByDescending(candidate => candidate.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault()
            ?? throw Failure($"IngestionBatchSnapshot cannot supply ImportBatch.Restore parameter '{parameter.Name}'.");
        return $"lease.Snapshot.{property.Name}";
    }

    string MapFailureArgument(ParameterInfo parameter)
    {
        var name = parameter.Name?.ToLowerInvariant() ?? string.Empty;
        if (parameter.ParameterType == typeof(long)) return "batch.AggregateRevision";
        if (parameter.ParameterType == typeof(DateTimeOffset)) return "failedAtUtc";
        if (parameter.ParameterType == typeof(string))
        {
            return name.Contains("code")
                ? "\"INGESTION_VALIDATION_ATTEMPTS_EXHAUSTED\""
                : "exception.Message[..Math.Min(exception.Message.Length, 2000)]";
        }
        if (parameter.ParameterType == typeof(Guid)) return "Guid.Empty";
        if (parameter.ParameterType.IsEnum)
        {
            var selected = Enum.GetNames(parameter.ParameterType)
                .FirstOrDefault(value => value.Contains("Validation", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("Integrity", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("Failed", StringComparison.OrdinalIgnoreCase))
                ?? Enum.GetNames(parameter.ParameterType).First();
            return $"{TypeName(parameter.ParameterType)}.{selected}";
        }
        throw Failure($"Cannot map ImportBatch failure parameter '{parameter.Name}' of type '{parameter.ParameterType}'.");
    }
}

string GenerateHostedWorker() =>
    """
    using Microsoft.Extensions.Hosting;

    namespace Aggregator.Ingestion.Worker;

    public sealed class IngestionValidationWorker(
        IServiceScopeFactory scopeFactory,
        IngestionWorkerOptions options) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<GeneratedIngestionValidationService>();
                if (!await service.ProcessOneAsync(stoppingToken))
                {
                    await Task.Delay(options.EmptyDelay, stoppingToken);
                }
            }
        }
    }
    """ + Environment.NewLine;

string GenerateWorkerProgram() =>
    """
    using Aggregator.Ingestion.Application;
    using Aggregator.Ingestion.Infrastructure;
    using Aggregator.Ingestion.Worker;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Platform.ObjectStorage;
    using Platform.Observability;

    var builder = Host.CreateApplicationBuilder(args);
    var options = IngestionWorkerOptions.FromConfiguration(builder.Configuration);
    builder.Services.AddSingleton(options);
    builder.Services.AddIngestionApplication();
    builder.Services.AddIngestionInfrastructure(builder.Configuration);
    var objectOptions = new S3ObjectStoreOptions
    {
        ServiceUrl = new Uri(Require(builder.Configuration, "Ingestion:ObjectStorage:ServiceUrl"), UriKind.Absolute),
        Region = builder.Configuration["Ingestion:ObjectStorage:Region"] ?? "us-east-1",
        Bucket = Require(builder.Configuration, "Ingestion:ObjectStorage:Bucket"),
        AccessKey = Require(builder.Configuration, "Ingestion:ObjectStorage:AccessKey"),
        SecretKey = Require(builder.Configuration, "Ingestion:ObjectStorage:SecretKey"),
        ForcePathStyle = bool.TryParse(builder.Configuration["Ingestion:ObjectStorage:ForcePathStyle"], out var force)
            ? force : true,
    };
    objectOptions.Validate();
    builder.Services.AddSingleton<IObjectStore>(_ => new S3ObjectStore(objectOptions));
    builder.Services.AddScoped<IngestionValidationQueue>();
    builder.Services.AddScoped<IIngestionValidationQueue>(services =>
        services.GetRequiredService<IngestionValidationQueue>());
    builder.Services.AddScoped<GeneratedIngestionValidationService>();
    builder.Services.AddHostedService<IngestionValidationWorker>();
    builder.Services.AddPlatformObservability(builder.Configuration, "ingestion-validation-worker");

    await builder.Build().RunAsync();

    static string Require(IConfiguration configuration, string path) =>
        configuration[path] is { Length: > 0 } value ? value.Trim()
            : throw new InvalidOperationException($"Configuration value '{path}' is required.");
    """ + Environment.NewLine;

string GenerateWorkerTestProject() =>
    """
    <Project Sdk="Microsoft.NET.Sdk">
      <PropertyGroup><IsPackable>false</IsPackable><IsTestProject>true</IsTestProject></PropertyGroup>
      <ItemGroup>
        <PackageReference Include="Microsoft.NET.Test.Sdk" />
        <PackageReference Include="xunit" />
        <PackageReference Include="xunit.runner.visualstudio"><PrivateAssets>all</PrivateAssets></PackageReference>
        <PackageReference Include="coverlet.collector"><PrivateAssets>all</PrivateAssets></PackageReference>
      </ItemGroup>
      <ItemGroup><ProjectReference Include="../../../src/Ingestion/Ingestion.Worker/Ingestion.Worker.csproj" /></ItemGroup>
    </Project>
    """ + Environment.NewLine;

string GenerateWorkerOptionsTests() =>
    """
    using Aggregator.Ingestion.Worker;

    namespace Ingestion.Worker.Tests;

    public sealed class IngestionWorkerOptionsTests
    {
        [Fact]
        public void ValidOptionsUseBoundedLeaseAndAttempts()
        {
            var options = new IngestionWorkerOptions { WorkerIdentity = "ingestion-worker-test" };
            options.Validate();
            Assert.InRange(options.MaximumAttempts, 1, 100);
            Assert.InRange(options.LeaseDuration, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(30));
        }

        [Fact]
        public void EmptyIdentityIsRejected()
        {
            var options = new IngestionWorkerOptions { WorkerIdentity = " " };
            var exception = Assert.Throws<ArgumentException>(options.Validate);
            Assert.Equal("WorkerIdentity", exception.ParamName);
        }
    }
    """ + Environment.NewLine;

string GenerateMigrationSql()
{
    var options = new DbContextOptionsBuilder<IngestionDbContext>()
        .UseNpgsql("Host=localhost;Database=ingestion_db;Username=ingestion_migrator;Password=unused")
        .Options;
    using var context = new IngestionDbContext(options);
    var entity = context.Model.GetEntityTypes().Single(item => item.ClrType.Name == "ImportBatchRow");
    var table = entity.GetTableName() ?? throw Failure("ImportBatchRow has no table mapping.");
    var schema = entity.GetSchema() ?? throw Failure("ImportBatchRow has no schema mapping.");
    var store = StoreObjectIdentifier.Table(table, schema);
    var id = entity.FindProperty("Id")?.GetColumnName(store) ?? throw Failure("ImportBatchRow.Id has no column mapping.");
    var qualified = $"{QuoteSql(schema)}.{QuoteSql(table)}";
    var idColumn = QuoteSql(id);
    return $"""
    CREATE TABLE operations.validation_work
    (
        batch_id uuid PRIMARY KEY REFERENCES {qualified} ({idColumn}) ON DELETE RESTRICT,
        lease_token uuid NULL,
        leased_by varchar(200) NULL,
        lease_expires_at_utc timestamptz NULL,
        attempt_count integer NOT NULL DEFAULT 0,
        last_error varchar(4000) NULL,
        last_failed_at_utc timestamptz NULL,
        completed_at_utc timestamptz NULL,
        CONSTRAINT ck_ingestion_validation_attempts CHECK (attempt_count >= 0),
        CONSTRAINT ck_ingestion_validation_lease_shape CHECK
        (
            (lease_token IS NULL AND leased_by IS NULL AND lease_expires_at_utc IS NULL)
            OR
            (lease_token IS NOT NULL AND leased_by IS NOT NULL AND lease_expires_at_utc IS NOT NULL)
        )
    );

    CREATE INDEX ix_ingestion_validation_available
        ON operations.validation_work (lease_expires_at_utc, attempt_count)
        WHERE completed_at_utc IS NULL;

    CREATE TABLE batches.item_decision
    (
        batch_id uuid NOT NULL REFERENCES {qualified} ({idColumn}) ON DELETE RESTRICT,
        ordinal integer NOT NULL,
        item_key varchar(300) NOT NULL,
        decision_json jsonb NOT NULL,
        decision_digest char(64) NOT NULL,
        recorded_at_utc timestamptz NOT NULL,
        PRIMARY KEY (batch_id, item_key),
        CONSTRAINT uq_ingestion_item_decision_ordinal UNIQUE (batch_id, ordinal),
        CONSTRAINT ck_ingestion_item_decision_ordinal CHECK (ordinal > 0),
        CONSTRAINT ck_ingestion_item_decision_key CHECK (length(btrim(item_key)) > 0),
        CONSTRAINT ck_ingestion_item_decision_digest CHECK (decision_digest ~ '^[0-9a-f]{{64}}$')
    );

    CREATE OR REPLACE FUNCTION batches.reject_item_decision_mutation()
    RETURNS trigger
    LANGUAGE plpgsql
    AS $$
    BEGIN
        RAISE EXCEPTION 'Ingestion item decisions are immutable';
    END
    $$;

    CREATE TRIGGER tr_ingestion_item_decision_immutable
        BEFORE UPDATE OR DELETE ON batches.item_decision
        FOR EACH ROW EXECUTE FUNCTION batches.reject_item_decision_mutation();
    """ + Environment.NewLine;
}

static Type? FindPackageRoot(Assembly contracts, IReadOnlyList<ParameterInfo> required)
{
    return contracts.GetTypes()
        .Where(type => type.IsPublic && type.IsClass)
        .Where(type => required.All(parameter =>
            parameter.ParameterType == type || type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Any(property => parameter.ParameterType.IsAssignableFrom(property.PropertyType))))
        .OrderByDescending(type => type.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
        .ThenByDescending(type => type.Name.Contains("Export", StringComparison.OrdinalIgnoreCase))
        .ThenBy(type => type.Name, StringComparer.Ordinal)
        .FirstOrDefault();
}

static string? FindDecisionAccessor(Type resultType, Type decisionType)
{
    if (IsDecisionCollection(resultType, decisionType)) return "self";
    return resultType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => IsDecisionCollection(property.PropertyType, decisionType))
        .OrderByDescending(property => property.Name.Contains("Decision", StringComparison.OrdinalIgnoreCase))
        .Select(property => property.Name)
        .FirstOrDefault();
}

static bool IsDecisionCollection(Type type, Type decisionType)
{
    if (type.IsArray) return type.GetElementType() == decisionType;
    return type.GetInterfaces().Append(type).Any(candidate =>
        candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>) &&
        candidate.GetGenericArguments()[0] == decisionType);
}

static bool IsAwaitable(Type type) => type == typeof(Task) || type == typeof(ValueTask) ||
    type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) ||
        type.GetGenericTypeDefinition() == typeof(ValueTask<>));

static Type? UnwrapAwaitable(Type type) => type.IsGenericType &&
    (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>))
        ? type.GetGenericArguments()[0] : null;

static string Normalize(string value) =>
    new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

static string TypeName(Type type)
{
    if (type.IsArray) return TypeName(type.GetElementType()!) + "[]";
    if (!type.IsGenericType) return "global::" + (type.FullName ?? type.Name).Replace('+', '.');
    var name = type.GetGenericTypeDefinition().FullName ?? throw Failure($"Type '{type}' has no full name.");
    name = name[..name.IndexOf('`')].Replace('+', '.');
    return "global::" + name + "<" + string.Join(", ", type.GetGenericArguments().Select(TypeName)) + ">";
}

static string QuoteSql(string identifier)
{
    if (string.IsNullOrWhiteSpace(identifier) ||
        identifier.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '_')))
    {
        throw Failure($"Unsafe SQL identifier '{identifier}'.");
    }
    return $"\"{identifier}\"";
}

static DirectoryInfo FindRepositoryRoot(string start)
{
    var current = new DirectoryInfo(start);
    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "AggregatorBackend.slnx"))) return current;
        current = current.Parent;
    }
    throw Failure("Repository root was not found.");
}