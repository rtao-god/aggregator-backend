#!/usr/bin/env python3
from pathlib import Path

root = Path(__file__).resolve().parents[2]
path = root / "tools" / "IngestionWorkerGenerator" / "Program.cs"
text = path.read_text(encoding="utf-8")

text = text.replace(
    "            string error,\n            int maximumAttempts,\n            DateTimeOffset failedAtUtc,",
    "            string error,\n            bool retainLease,\n            int maximumAttempts,\n            DateTimeOffset failedAtUtc,",
)
text = text.replace(
    "                    lease_token = CASE WHEN attempt_count + 1 >= @maximum_attempts THEN lease_token ELSE NULL END,\n"
    "                    leased_by = CASE WHEN attempt_count + 1 >= @maximum_attempts THEN leased_by ELSE NULL END,\n"
    "                    lease_expires_at_utc = CASE WHEN attempt_count + 1 >= @maximum_attempts THEN lease_expires_at_utc ELSE NULL END",
    "                    lease_token = CASE WHEN @retain_lease OR attempt_count + 1 >= @maximum_attempts THEN lease_token ELSE NULL END,\n"
    "                    leased_by = CASE WHEN @retain_lease OR attempt_count + 1 >= @maximum_attempts THEN leased_by ELSE NULL END,\n"
    "                    lease_expires_at_utc = CASE WHEN @retain_lease OR attempt_count + 1 >= @maximum_attempts THEN lease_expires_at_utc ELSE NULL END",
)
text = text.replace(
    "            command.Parameters.AddWithValue(\"maximum_attempts\", NpgsqlDbType.Integer, maximumAttempts);",
    "            command.Parameters.AddWithValue(\"retain_lease\", NpgsqlDbType.Boolean, retainLease);\n"
    "            command.Parameters.AddWithValue(\"maximum_attempts\", NpgsqlDbType.Integer, maximumAttempts);",
)
text = text.replace(
    "                var attempts = await queue.RecordFailureAsync(\n"
    "                    lease,\n"
    "                    exception.Message,\n"
    "                    options.MaximumAttempts,",
    "                var terminal = exception is IngestionApplicationException or IngestionDomainException ||\n"
    "                    lease.AttemptCount + 1 >= options.MaximumAttempts;\n"
    "                var attempts = await queue.RecordFailureAsync(\n"
    "                    lease,\n"
    "                    exception.Message,\n"
    "                    terminal,\n"
    "                    options.MaximumAttempts,",
)
text = text.replace(
    "                if (attempts >= options.MaximumAttempts ||\n"
    "                    exception is IngestionApplicationException or IngestionDomainException)",
    "                if (terminal)",
)

path.write_text(text, encoding="utf-8")
