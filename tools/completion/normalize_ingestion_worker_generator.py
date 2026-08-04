#!/usr/bin/env python3
from pathlib import Path

root = Path(__file__).resolve().parents[2]
path = root / "tools" / "IngestionWorkerGenerator" / "Program.cs"
text = path.read_text(encoding="utf-8")

text = text.replace(
    "    using Microsoft.EntityFrameworkCore.Metadata;\n    using Npgsql;",
    "    using Microsoft.EntityFrameworkCore.Metadata;\n    using Microsoft.EntityFrameworkCore.Storage;\n    using Npgsql;",
)
text = text.replace(
    "        Task<int> RecordFailureAsync(\n            IngestionValidationLease lease,\n            string error,\n            DateTimeOffset failedAtUtc,",
    "        Task<int> RecordFailureAsync(\n            IngestionValidationLease lease,\n            string error,\n            int maximumAttempts,\n            DateTimeOffset failedAtUtc,",
)
text = text.replace(
    "        public async Task<int> RecordFailureAsync(\n            IngestionValidationLease lease,\n            string error,\n            DateTimeOffset failedAtUtc,",
    "        public async Task<int> RecordFailureAsync(\n            IngestionValidationLease lease,\n            string error,\n            int maximumAttempts,\n            DateTimeOffset failedAtUtc,",
)
text = text.replace(
    "            RequireUtc(failedAtUtc, nameof(failedAtUtc));\n            await using var transaction",
    "            RequireUtc(failedAtUtc, nameof(failedAtUtc));\n            if (maximumAttempts is < 1 or > 100)\n            {\n                throw new ArgumentOutOfRangeException(nameof(maximumAttempts));\n            }\n            await using var transaction",
    1,
)
text = text.replace(
    "                    lease_token = NULL,\n                    leased_by = NULL,\n                    lease_expires_at_utc = NULL",
    "                    lease_token = CASE WHEN attempt_count + 1 >= @maximum_attempts THEN lease_token ELSE NULL END,\n                    leased_by = CASE WHEN attempt_count + 1 >= @maximum_attempts THEN leased_by ELSE NULL END,\n                    lease_expires_at_utc = CASE WHEN attempt_count + 1 >= @maximum_attempts THEN lease_expires_at_utc ELSE NULL END",
    1,
)
text = text.replace(
    "            command.Parameters.AddWithValue(\"failed_at_utc\", NpgsqlDbType.TimestampTz, failedAtUtc);",
    "            command.Parameters.AddWithValue(\"failed_at_utc\", NpgsqlDbType.TimestampTz, failedAtUtc);\n            command.Parameters.AddWithValue(\"maximum_attempts\", NpgsqlDbType.Integer, maximumAttempts);",
    1,
)
text = text.replace(
    "                    exception.Message,\n                    failedAtUtc,",
    "                    exception.Message,\n                    options.MaximumAttempts,\n                    failedAtUtc,",
)
text = text.replace(
    "        public required string WorkerIdentity { get; init; }\n\n        public int MaximumAttempts",
    "        public required string WorkerIdentity { get; init; }\n\n        public required Guid SystemActorId { get; init; }\n\n        public int MaximumAttempts",
)
text = text.replace(
    "                WorkerIdentity = configuration[$\"{SectionName}:WorkerIdentity\"]\n                    ?? throw new InvalidOperationException($\"{SectionName}:WorkerIdentity is required.\"),\n                MaximumAttempts",
    "                WorkerIdentity = configuration[$\"{SectionName}:WorkerIdentity\"]\n                    ?? throw new InvalidOperationException($\"{SectionName}:WorkerIdentity is required.\"),\n                SystemActorId = Guid.TryParse(configuration[$\"{SectionName}:SystemActorId\"], out var actorId) && actorId != Guid.Empty\n                    ? actorId\n                    : throw new InvalidOperationException($\"{SectionName}:SystemActorId must be a non-empty UUID.\"),\n                MaximumAttempts",
)
text = text.replace(
    "            if (MaximumAttempts is < 1 or > 100)",
    "            if (SystemActorId == Guid.Empty) throw new ArgumentException(\"Ingestion system actor ID is required.\", nameof(SystemActorId));\n            if (MaximumAttempts is < 1 or > 100)",
)
text = text.replace(
    "        if (parameter.ParameterType == typeof(Guid)) return \"Guid.Empty\";",
    "        if (parameter.ParameterType == typeof(Guid)) return \"options.SystemActorId\";",
)
text = text.replace(
    'var invocation = IsAwaitable(method.ReturnType)\n        ? $"var validation = await validator.{method.Name}(',
    'var validatorTarget = method.IsStatic ? TypeName(method.DeclaringType!) : "validator";\n    var invocation = IsAwaitable(method.ReturnType)\n        ? $"var validation = await {validatorTarget}.{method.Name}(',
)
text = text.replace(
    ': $"var validation = validator.{method.Name}(',
    ': $"var validation = {validatorTarget}.{method.Name}(',
)
text = text.replace(
    "            var options = new IngestionWorkerOptions { WorkerIdentity = \"ingestion-worker-test\" };",
    "            var options = new IngestionWorkerOptions\n            {\n                WorkerIdentity = \"ingestion-worker-test\",\n                SystemActorId = Guid.Parse(\"0198b600-0000-7000-8000-000000000001\"),\n            };",
)
text = text.replace(
    "            var options = new IngestionWorkerOptions { WorkerIdentity = \" \" };",
    "            var options = new IngestionWorkerOptions\n            {\n                WorkerIdentity = \" \",\n                SystemActorId = Guid.Parse(\"0198b600-0000-7000-8000-000000000001\"),\n            };",
)

path.write_text(text, encoding="utf-8")
