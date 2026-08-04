using System.Globalization;
using Aggregator.Catalog.Contracts;

namespace Aggregator.Catalog.Application;

/// <summary>Persists one exact Catalog-owned draft command and its immutable result.</summary>
public interface ICatalogIngestionDraftStore
{
    public Task<CatalogIngestionCommandOutcome> UpsertAsync(
        CatalogIngestionUpsertDraftCommand command,
        string callerIdentity,
        CancellationToken cancellationToken);
}

public sealed class CatalogIngestionDraftException : Exception
{
    public CatalogIngestionDraftException(
        string owner,
        string code,
        int statusCode,
        string message,
        string requiredAction,
        IReadOnlyDictionary<string, object?>? context = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Owner = Require(owner, nameof(owner));
        Code = Require(code, nameof(code));
        RequiredAction = Require(requiredAction, nameof(requiredAction));
        if (statusCode is < 400 or > 599)
        {
            throw new ArgumentOutOfRangeException(
                nameof(statusCode),
                statusCode,
                "Status code must represent an HTTP error.");
        }

        StatusCode = statusCode;
        Context = context ?? new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    public string Owner { get; }

    public string Code { get; }

    public int StatusCode { get; }

    public string RequiredAction { get; }

    public IReadOnlyDictionary<string, object?> Context { get; }

    private static string Require(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value;
}

/// <summary>
/// Validates and persists a typed Ingestion command that can create or advance a Catalog draft only.
/// It has no publication, approval, public-read, or rollback authority.
/// </summary>
public sealed class CatalogIngestionDraftService(ICatalogIngestionDraftStore store)
{
    private const int MaximumFieldCount = 500;
    private const int MaximumCanonicalValueLength = 16_384;

    public async Task<CatalogIngestionCommandOutcome> ExecuteAsync(
        CatalogIngestionUpsertDraftCommand command,
        string callerIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommand(command);
        var caller = RequireCaller(callerIdentity);
        var expectedDigest = CatalogIngestionCommandDigest.Compute(command);
        if (!string.Equals(expectedDigest, command.CommandDigest, StringComparison.Ordinal))
        {
            throw Failure(
                "Catalog.Commands",
                "CATALOG_INGESTION_COMMAND_DIGEST_MISMATCH",
                409,
                "The Catalog ingestion command does not match its declared canonical digest.",
                "Recreate the command from the exact Ingestion item and submit its canonical digest.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["commandId"] = command.CommandId,
                    ["expectedDigest"] = expectedDigest,
                    ["actualDigest"] = command.CommandDigest,
                });
        }

        var outcome = await store.UpsertAsync(
            command,
            caller,
            cancellationToken);
        return ValidateOutcome(command, outcome);
    }

    private static void ValidateCommand(CatalogIngestionUpsertDraftCommand command)
    {
        RequireId(command.CommandId, nameof(command.CommandId));
        RequireId(command.IngestionBatchId, nameof(command.IngestionBatchId));
        RequireId(
            command.ExpectedCatalogConfigurationRevisionId,
            nameof(command.ExpectedCatalogConfigurationRevisionId));
        RequireDigest(command.CommandDigest, nameof(command.CommandDigest));
        RequireSemanticKey(command.SiteKey, nameof(command.SiteKey), 96);
        RequireSemanticKey(command.CatalogKey, nameof(command.CatalogKey), 96);
        RequireBoundedIdentity(
            command.IngestionItemKey,
            nameof(command.IngestionItemKey),
            300);
        RequireEntityKind(command.EntityKind);
        RequireBoundedIdentity(
            command.SubjectNaturalKey,
            nameof(command.SubjectNaturalKey),
            500);
        RequireUtc(command.RequestedAtUtc, nameof(command.RequestedAtUtc));
        RequireCorrelationId(command.CorrelationId);
        ArgumentNullException.ThrowIfNull(command.Fields);
        if (command.Fields.Count is < 1 or > MaximumFieldCount)
        {
            throw ContractFailure(
                "CATALOG_INGESTION_FIELD_COUNT_INVALID",
                $"Catalog ingestion commands require between one and {MaximumFieldCount} typed fields.");
        }

        CatalogDraftFieldValueContract? previous = null;
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in command.Fields)
        {
            ArgumentNullException.ThrowIfNull(field);
            ValidateField(field);
            var identity = $"{field.FieldKey}\u001f{field.Locale}";
            if (!identities.Add(identity))
            {
                throw ContractFailure(
                    "CATALOG_INGESTION_FIELD_DUPLICATE",
                    $"Field '{field.FieldKey}' and locale '{field.Locale}' occur more than once.");
            }

            if (previous is not null && CompareFields(previous, field) >= 0)
            {
                throw ContractFailure(
                    "CATALOG_INGESTION_FIELD_ORDER_INVALID",
                    "Catalog ingestion fields must be strictly ordered by field key and locale.");
            }

            previous = field;
        }
    }

    private static void ValidateField(CatalogDraftFieldValueContract field)
    {
        RequireSemanticFieldKey(field.FieldKey, nameof(field.FieldKey));
        if (!Enum.IsDefined(field.Kind))
        {
            throw ContractFailure(
                "CATALOG_INGESTION_FIELD_KIND_UNSUPPORTED",
                $"Field '{field.FieldKey}' has unsupported value kind '{field.Kind}'.");
        }

        RequireCanonicalValue(field);
        RequireLocale(field.Locale);
        RequireSemanticKey(field.SourceKey, nameof(field.SourceKey), 96);
        RequireDigest(field.EvidenceDigest, nameof(field.EvidenceDigest));
        ValidateUsagePolicy(field);
    }

    private static void RequireCanonicalValue(CatalogDraftFieldValueContract field)
    {
        var value = field.CanonicalValue;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumCanonicalValueLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            !string.Equals(value, value.Normalize(), StringComparison.Ordinal))
        {
            throw ContractFailure(
                "CATALOG_INGESTION_FIELD_VALUE_INVALID",
                $"Field '{field.FieldKey}' has an empty, non-canonical, or over-limit value.");
        }

        switch (field.Kind)
        {
            case CatalogDraftValueKindContract.Text:
                if (value.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
                {
                    throw InvalidTypedValue(field);
                }
                break;
            case CatalogDraftValueKindContract.Integer:
                if (!long.TryParse(
                        value,
                        NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture,
                        out var integer) ||
                    !string.Equals(
                        integer.ToString(CultureInfo.InvariantCulture),
                        value,
                        StringComparison.Ordinal))
                {
                    throw InvalidTypedValue(field);
                }
                break;
            case CatalogDraftValueKindContract.Decimal:
                if (!decimal.TryParse(
                        value,
                        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture,
                        out var decimalValue) ||
                    !string.Equals(
                        decimalValue.ToString(CultureInfo.InvariantCulture),
                        value,
                        StringComparison.Ordinal))
                {
                    throw InvalidTypedValue(field);
                }
                break;
            case CatalogDraftValueKindContract.Boolean:
                if (value is not "true" and not "false")
                {
                    throw InvalidTypedValue(field);
                }
                break;
            case CatalogDraftValueKindContract.Date:
                if (!DateOnly.TryParseExact(
                        value,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out _))
                {
                    throw InvalidTypedValue(field);
                }
                break;
            case CatalogDraftValueKindContract.DateTime:
                if (!DateTimeOffset.TryParse(
                        value,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var dateTime) ||
                    dateTime.Offset != TimeSpan.Zero)
                {
                    throw InvalidTypedValue(field);
                }
                break;
            case CatalogDraftValueKindContract.Uri:
            case CatalogDraftValueKindContract.ExternalReference:
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                    uri.Scheme is not ("http" or "https") ||
                    string.IsNullOrWhiteSpace(uri.Host))
                {
                    throw InvalidTypedValue(field);
                }
                break;
            default:
                throw ContractFailure(
                    "CATALOG_INGESTION_FIELD_KIND_UNSUPPORTED",
                    $"Field '{field.FieldKey}' has unsupported value kind '{field.Kind}'.");
        }
    }

    private static void ValidateUsagePolicy(CatalogDraftFieldValueContract field)
    {
        switch (field.UsagePolicy)
        {
            case "public_allowed":
            case "publishable":
            case "display_with_attribution":
            case "owner_authorized":
                return;
            case "link_only":
                if (field.Kind == CatalogDraftValueKindContract.ExternalReference)
                {
                    return;
                }

                throw Failure(
                    "Catalog.Provenance",
                    "CATALOG_INGESTION_USAGE_POLICY_BLOCKED",
                    422,
                    $"Link-only evidence cannot support field '{field.FieldKey}'.",
                    "Submit link-only evidence only as an external-reference field.");
            case "internal_review_only":
            case "research_only":
            case "forbidden":
                throw Failure(
                    "Catalog.Provenance",
                    "CATALOG_INGESTION_USAGE_POLICY_BLOCKED",
                    422,
                    $"Usage policy '{field.UsagePolicy}' cannot enter a Catalog draft.",
                    "Remove the blocked field or replace it with evidence authorized for Catalog use.",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["fieldKey"] = field.FieldKey,
                        ["sourceKey"] = field.SourceKey,
                        ["usagePolicy"] = field.UsagePolicy,
                    });
            default:
                throw ContractFailure(
                    "CATALOG_INGESTION_USAGE_POLICY_UNSUPPORTED",
                    $"Field '{field.FieldKey}' contains unsupported usage policy '{field.UsagePolicy}'.");
        }
    }

    private static CatalogIngestionCommandOutcome ValidateOutcome(
        CatalogIngestionUpsertDraftCommand command,
        CatalogIngestionCommandOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.CommandId != command.CommandId ||
            outcome.IngestionBatchId != command.IngestionBatchId ||
            !string.Equals(
                outcome.IngestionItemKey,
                command.IngestionItemKey,
                StringComparison.Ordinal))
        {
            throw Failure(
                "Catalog.Persistence",
                "CATALOG_INGESTION_RESULT_IDENTITY_CORRUPT",
                500,
                "Catalog ingestion persistence returned a result for another command identity.",
                "Stop Catalog ingestion and restore the command-result identity invariant.");
        }

        if (!Enum.IsDefined(outcome.State))
        {
            throw Failure(
                "Catalog.Persistence",
                "CATALOG_INGESTION_RESULT_STATE_CORRUPT",
                500,
                $"Catalog ingestion persistence returned unsupported state '{outcome.State}'.",
                "Stop Catalog ingestion and repair the persisted result state.");
        }

        RequireUtc(outcome.CompletedAtUtc, nameof(outcome.CompletedAtUtc));
        if (outcome.State is CatalogIngestionOutcomeStateContract.DraftCreated or
            CatalogIngestionOutcomeStateContract.DraftUpdated)
        {
            if (outcome.ListingId is not Guid listingId || listingId == Guid.Empty ||
                outcome.ListingRevisionId is not Guid revisionId || revisionId == Guid.Empty ||
                outcome.FailureCode is not null ||
                outcome.FailureDetail is not null)
            {
                throw InvalidOutcomeShape(outcome.State);
            }
        }
        else if (outcome.ListingId is not null ||
            outcome.ListingRevisionId is not null ||
            string.IsNullOrWhiteSpace(outcome.FailureCode) ||
            string.IsNullOrWhiteSpace(outcome.FailureDetail))
        {
            throw InvalidOutcomeShape(outcome.State);
        }

        return outcome;
    }

    private static int CompareFields(
        CatalogDraftFieldValueContract left,
        CatalogDraftFieldValueContract right)
    {
        var keyComparison = StringComparer.Ordinal.Compare(left.FieldKey, right.FieldKey);
        return keyComparison != 0
            ? keyComparison
            : StringComparer.OrdinalIgnoreCase.Compare(left.Locale, right.Locale);
    }

    private static void RequireId(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw ContractFailure(
                "CATALOG_INGESTION_IDENTITY_REQUIRED",
                $"Catalog ingestion field '{parameterName}' requires a non-empty UUID.");
        }
    }

    private static void RequireSemanticKey(string value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            value[0] is < 'a' or > 'z' ||
            value.Any(character =>
                character is not (>= 'a' and <= 'z') and
                not (>= '0' and <= '9') and
                not '-' and
                not '_' and
                not '.'))
        {
            throw ContractFailure(
                "CATALOG_INGESTION_SEMANTIC_KEY_INVALID",
                $"Catalog ingestion field '{parameterName}' must be a bounded lowercase semantic key.");
        }
    }

    private static void RequireSemanticFieldKey(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 200 ||
            value[0] is < 'a' or > 'z' ||
            value.Any(character =>
                character is not (>= 'a' and <= 'z') and
                not (>= 'A' and <= 'Z') and
                not (>= '0' and <= '9') and
                not '-' and
                not '_' and
                not '.'))
        {
            throw ContractFailure(
                "CATALOG_INGESTION_FIELD_KEY_INVALID",
                $"Catalog ingestion field '{parameterName}' must be a bounded semantic field key.");
        }
    }

    private static void RequireEntityKind(string value)
    {
        if (value is not "place" and not "provider")
        {
            throw ContractFailure(
                "CATALOG_INGESTION_ENTITY_KIND_UNSUPPORTED",
                "Catalog ingestion can create drafts only for place or provider items.");
        }
    }

    private static void RequireBoundedIdentity(
        string value,
        string parameterName,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw ContractFailure(
                "CATALOG_INGESTION_IDENTITY_INVALID",
                $"Catalog ingestion field '{parameterName}' is missing, over-limit, or contains control characters.");
        }
    }

    private static void RequireLocale(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 35 ||
            value[0] == '-' ||
            value[^1] == '-' ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw ContractFailure(
                "CATALOG_INGESTION_LOCALE_INVALID",
                "Catalog ingestion field locale must be a bounded BCP-47-style token.");
        }
    }

    private static void RequireDigest(string value, string parameterName)
    {
        if (value is not { Length: 64 } ||
            value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw ContractFailure(
                "CATALOG_INGESTION_DIGEST_INVALID",
                $"Catalog ingestion field '{parameterName}' requires a lowercase SHA-256 digest.");
        }
    }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw ContractFailure(
                "CATALOG_INGESTION_TIME_INVALID",
                $"Catalog ingestion field '{parameterName}' must be UTC.");
        }
    }

    private static void RequireCorrelationId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length is < 8 or > 128 ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '_' and not '-' and not '.' and not ':'))
        {
            throw ContractFailure(
                "CATALOG_INGESTION_CORRELATION_ID_INVALID",
                "Catalog ingestion requires a bounded correlation identity.");
        }
    }

    private static string RequireCaller(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 200 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw Failure(
                "Catalog.Access",
                "CATALOG_INGESTION_CALLER_REQUIRED",
                403,
                "A valid authenticated Ingestion workload identity is required.",
                "Authenticate with the Catalog ingestion audience, scope and workload subject.");
        }

        return value;
    }

    private static CatalogIngestionDraftException InvalidTypedValue(
        CatalogDraftFieldValueContract field) =>
        ContractFailure(
            "CATALOG_INGESTION_FIELD_VALUE_INVALID",
            $"Field '{field.FieldKey}' is not canonical for value kind '{field.Kind}'.");

    private static CatalogIngestionDraftException InvalidOutcomeShape(
        CatalogIngestionOutcomeStateContract state) =>
        Failure(
            "Catalog.Persistence",
            "CATALOG_INGESTION_RESULT_SHAPE_CORRUPT",
            500,
            $"Catalog ingestion result state '{state}' has an invalid payload shape.",
            "Stop Catalog ingestion and repair the persisted command result.");

    private static CatalogIngestionDraftException ContractFailure(
        string code,
        string detail) =>
        Failure(
            "Catalog.Contracts",
            code,
            400,
            detail,
            "Correct the typed Catalog ingestion command and retry.");

    private static CatalogIngestionDraftException Failure(
        string owner,
        string code,
        int statusCode,
        string detail,
        string requiredAction,
        IReadOnlyDictionary<string, object?>? context = null) =>
        new(
            owner,
            code,
            statusCode,
            detail,
            requiredAction,
            context);
}
