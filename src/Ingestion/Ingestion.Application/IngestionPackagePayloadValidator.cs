using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Aggregator.Ingestion.Contracts;

namespace Aggregator.Ingestion.Application;

public enum IngestionPackageDecisionKind
{
    Accepted = 1,
    NeedsReview = 2,
    Rejected = 3,
}

public sealed record IngestionValidatedPackageItem(
    string ItemKey,
    int Ordinal,
    IngestionPackageEntityKindContract EntityKind,
    string ContentDigest,
    byte[] CanonicalDocument,
    IngestionPackageDecisionKind Decision,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<IngestionPackageQualityIssueContract> QualityIssues);

public sealed record IngestionPackageValidationResult(
    Guid CollectorExportId,
    string PayloadDigest,
    string ItemIndexDigest,
    IReadOnlyList<IngestionValidatedPackageItem> Items)
{
    public int AcceptedItemCount => Items.Count(item => item.Decision == IngestionPackageDecisionKind.Accepted);

    public int ReviewRequiredItemCount => Items.Count(item => item.Decision == IngestionPackageDecisionKind.NeedsReview);

    public int RejectedItemCount => Items.Count(item => item.Decision == IngestionPackageDecisionKind.Rejected);
}

public sealed class IngestionPackageIntegrityException : InvalidOperationException
{
    public IngestionPackageIntegrityException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Validates the exact registered payload bytes and classifies every package item. Integrity
/// failures reject the whole package; policy and quality findings produce explicit item decisions.
/// </summary>
public sealed partial class IngestionPackagePayloadValidator
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private static readonly Regex ItemKeyPattern = ItemKeyRegex();
    private static readonly Regex SemanticKeyPattern = SemanticKeyRegex();

    public IngestionPackageValidationResult Validate(
        IngestionBatchSnapshot batch,
        ReadOnlySpan<byte> payloadBytes)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (payloadBytes.IsEmpty)
        {
            throw Integrity("INGESTION_PAYLOAD_EMPTY", "The registered package payload is empty.");
        }

        var payloadDigest = IngestionDocumentDigest.Compute(payloadBytes);
        RequireExactDigest(
            batch.PayloadObjectDigest,
            payloadDigest,
            "INGESTION_PAYLOAD_OBJECT_DIGEST_MISMATCH",
            "The payload bytes do not match the registered object digest.");
        RequireExactDigest(
            batch.PayloadDigest,
            payloadDigest,
            "INGESTION_PAYLOAD_DIGEST_MISMATCH",
            "The payload bytes do not match the registered package digest.");

        AggregatorCandidatePayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<AggregatorCandidatePayload>(payloadBytes, SerializerOptions)
                ?? throw Integrity(
                    "INGESTION_PAYLOAD_NULL",
                    "The package payload deserialized to null.");
        }
        catch (JsonException exception)
        {
            throw new IngestionPackageIntegrityException(
                "INGESTION_PAYLOAD_JSON_INVALID",
                $"The package payload is invalid for '{AggregatorCandidatePayloadContract.Identity}@{AggregatorCandidatePayloadContract.Revision}': {exception.Message}");
        }

        if (!string.Equals(
                payload.ContractIdentity,
                AggregatorCandidatePayloadContract.Identity,
                StringComparison.Ordinal) ||
            payload.ContractRevision != AggregatorCandidatePayloadContract.Revision)
        {
            throw Integrity(
                "INGESTION_PAYLOAD_CONTRACT_UNSUPPORTED",
                "The package payload contract identity or revision is unsupported.");
        }

        if (payload.CollectorExportId != batch.CollectorExportId)
        {
            throw Integrity(
                "INGESTION_PAYLOAD_EXPORT_ID_MISMATCH",
                "The package payload belongs to a different collector export.");
        }

        RequireExactDigest(
            batch.ManifestDigest,
            payload.ManifestDigest,
            "INGESTION_PAYLOAD_MANIFEST_DIGEST_MISMATCH",
            "The package payload does not identify the exact registered manifest.");
        ArgumentNullException.ThrowIfNull(payload.Items);
        if (payload.Items.Count != batch.ExpectedItemCount)
        {
            throw Integrity(
                "INGESTION_PAYLOAD_ITEM_COUNT_MISMATCH",
                $"Expected {batch.ExpectedItemCount} package items, actual {payload.Items.Count}.");
        }

        var ordered = ValidatePackageIdentity(payload.Items);
        var index = ordered
            .Select(item => new IngestionPackageIndexEntryContract(
                item.ItemKey,
                item.Ordinal,
                item.ContentDigest))
            .ToArray();
        var itemIndexDigest = IngestionCanonicalJson.ComputeDigest(index);
        RequireExactDigest(
            batch.ItemIndexDigest,
            itemIndexDigest,
            "INGESTION_ITEM_INDEX_DIGEST_MISMATCH",
            "The package item index does not match the registered digest.");

        var validatedItems = ordered.Select(ValidateItem).ToArray();
        if (validatedItems.Count != batch.ExpectedItemCount)
        {
            throw Integrity(
                "INGESTION_DECISION_COVERAGE_INVALID",
                "Validation did not produce one explicit decision per registered package item.");
        }

        return new IngestionPackageValidationResult(
            payload.CollectorExportId,
            payloadDigest,
            itemIndexDigest,
            validatedItems);
    }

    private static IReadOnlyList<AggregatorCandidatePayloadItem> ValidatePackageIdentity(
        IReadOnlyList<AggregatorCandidatePayloadItem> items)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var ordinals = new HashSet<int>();
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            RequireItemKey(item.ItemKey);
            RequireDigest(item.ContentDigest, nameof(item.ContentDigest));
            if (!keys.Add(item.ItemKey))
            {
                throw Integrity(
                    "INGESTION_ITEM_KEY_DUPLICATE",
                    $"Package item key '{item.ItemKey}' occurs more than once.");
            }

            if (item.Ordinal < 0 || !ordinals.Add(item.Ordinal))
            {
                throw Integrity(
                    "INGESTION_ITEM_ORDINAL_INVALID",
                    $"Package item ordinal '{item.Ordinal}' is negative or duplicated.");
            }

            if (!Enum.IsDefined(typeof(IngestionPackageEntityKindContract), item.EntityKind))
            {
                throw Integrity(
                    "INGESTION_ITEM_ENTITY_KIND_UNSUPPORTED",
                    $"Package item '{item.ItemKey}' has an unsupported entity kind.");
            }

            if (item.Candidate.ValueKind != JsonValueKind.Object)
            {
                throw Integrity(
                    "INGESTION_ITEM_CANDIDATE_INVALID",
                    $"Package item '{item.ItemKey}' candidate must be a JSON object.");
            }
        }

        var ordered = items.OrderBy(item => item.Ordinal).ToArray();
        var firstOrdinal = ordered.Length == 0 ? 0 : ordered[0].Ordinal;
        if (firstOrdinal is not 0 and not 1)
        {
            throw Integrity(
                "INGESTION_ITEM_ORDINAL_BASE_INVALID",
                "Package item ordinals must be contiguous and start at zero or one.");
        }

        for (var index = 0; index < ordered.Length; index++)
        {
            if (ordered[index].Ordinal != firstOrdinal + index)
            {
                throw Integrity(
                    "INGESTION_ITEM_ORDINAL_GAP",
                    "Package item ordinals must be contiguous with no gaps.");
            }
        }

        return ordered;
    }

    private static IngestionValidatedPackageItem ValidateItem(AggregatorCandidatePayloadItem item)
    {
        ArgumentNullException.ThrowIfNull(item.Evidence);
        ArgumentNullException.ThrowIfNull(item.QualityIssues);
        var canonicalEvidence = item.Evidence
            .Select(NormalizeEvidence)
            .OrderBy(value => value.Field, StringComparer.Ordinal)
            .ThenBy(value => value.SourceKey, StringComparer.Ordinal)
            .ThenBy(value => value.Locator, StringComparer.Ordinal)
            .ThenBy(value => value.EvidenceDigest, StringComparer.Ordinal)
            .ToArray();
        var canonicalIssues = item.QualityIssues
            .Select(NormalizeIssue)
            .OrderBy(value => value.Code, StringComparer.Ordinal)
            .ThenBy(value => value.Detail, StringComparer.Ordinal)
            .ToArray();
        var canonicalDocument = IngestionCanonicalJson.Serialize(new
        {
            item.ItemKey,
            item.Ordinal,
            item.EntityKind,
            Candidate = item.Candidate,
            Evidence = canonicalEvidence,
            QualityIssues = canonicalIssues,
        });
        var actualContentDigest = IngestionDocumentDigest.Compute(canonicalDocument);
        RequireExactDigest(
            item.ContentDigest,
            actualContentDigest,
            "INGESTION_ITEM_DIGEST_MISMATCH",
            $"Package item '{item.ItemKey}' content digest is invalid.");

        var rejected = new SortedSet<string>(StringComparer.Ordinal);
        var review = new SortedSet<string>(StringComparer.Ordinal);
        if (canonicalEvidence.Length == 0)
        {
            review.Add("provenance.missing");
        }

        foreach (var evidence in canonicalEvidence)
        {
            switch (evidence.UsagePolicy)
            {
                case IngestionPackageUsagePolicyContract.PublicAllowed:
                    break;
                case IngestionPackageUsagePolicyContract.LinkOnly:
                    if (!string.Equals(evidence.Field, "externalReference", StringComparison.Ordinal))
                    {
                        rejected.Add("provenance.link_only_field_forbidden");
                    }
                    break;
                case IngestionPackageUsagePolicyContract.InternalReviewOnly:
                    review.Add("provenance.internal_review_only");
                    break;
                case IngestionPackageUsagePolicyContract.ResearchOnly:
                    review.Add("provenance.research_only");
                    break;
                case IngestionPackageUsagePolicyContract.Forbidden:
                    rejected.Add("provenance.forbidden");
                    break;
                default:
                    throw Integrity(
                        "INGESTION_EVIDENCE_USAGE_POLICY_UNSUPPORTED",
                        $"Package item '{item.ItemKey}' contains an unsupported usage policy.");
            }
        }

        foreach (var issue in canonicalIssues)
        {
            switch (issue.Severity)
            {
                case IngestionPackageQualitySeverityContract.Information:
                    break;
                case IngestionPackageQualitySeverityContract.Warning:
                    review.Add($"quality.{issue.Code}");
                    break;
                case IngestionPackageQualitySeverityContract.Blocking:
                    rejected.Add($"quality.{issue.Code}");
                    break;
                default:
                    throw Integrity(
                        "INGESTION_QUALITY_SEVERITY_UNSUPPORTED",
                        $"Package item '{item.ItemKey}' contains an unsupported quality severity.");
            }
        }

        var decision = rejected.Count > 0
            ? IngestionPackageDecisionKind.Rejected
            : review.Count > 0
                ? IngestionPackageDecisionKind.NeedsReview
                : IngestionPackageDecisionKind.Accepted;
        IReadOnlyList<string> reasons = decision switch
        {
            IngestionPackageDecisionKind.Rejected => rejected.Concat(review).ToArray(),
            IngestionPackageDecisionKind.NeedsReview => review.ToArray(),
            IngestionPackageDecisionKind.Accepted => ["validation.accepted"],
            _ => throw new InvalidOperationException("Unsupported package decision."),
        };
        return new IngestionValidatedPackageItem(
            item.ItemKey,
            item.Ordinal,
            item.EntityKind,
            item.ContentDigest,
            canonicalDocument,
            decision,
            reasons,
            canonicalIssues);
    }

    private static IngestionPackageEvidenceContract NormalizeEvidence(
        IngestionPackageEvidenceContract evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        RequireSemanticKey(evidence.Field, nameof(evidence.Field), 200, allowCamelCase: true);
        RequireSemanticKey(evidence.SourceKey, nameof(evidence.SourceKey), 96, allowCamelCase: false);
        if (!Enum.IsDefined(typeof(IngestionPackageUsagePolicyContract), evidence.UsagePolicy))
        {
            throw Integrity(
                "INGESTION_EVIDENCE_USAGE_POLICY_UNSUPPORTED",
                "Evidence contains an unsupported usage policy.");
        }

        if (string.IsNullOrWhiteSpace(evidence.Locator) || evidence.Locator.Length > 2048 ||
            evidence.Locator.Any(char.IsControl))
        {
            throw Integrity(
                "INGESTION_EVIDENCE_LOCATOR_INVALID",
                "Evidence locator must be non-empty, bounded and contain no control characters.");
        }

        if (evidence.ObservedAtUtc.Offset != TimeSpan.Zero)
        {
            throw Integrity(
                "INGESTION_EVIDENCE_TIME_INVALID",
                "Evidence observation timestamps must be UTC.");
        }

        RequireDigest(evidence.EvidenceDigest, nameof(evidence.EvidenceDigest));
        return evidence with
        {
            Field = evidence.Field.Trim(),
            SourceKey = evidence.SourceKey.Trim(),
            Locator = evidence.Locator.Trim(),
        };
    }

    private static IngestionPackageQualityIssueContract NormalizeIssue(
        IngestionPackageQualityIssueContract issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        RequireSemanticKey(issue.Code, nameof(issue.Code), 200, allowCamelCase: false);
        if (!Enum.IsDefined(typeof(IngestionPackageQualitySeverityContract), issue.Severity))
        {
            throw Integrity(
                "INGESTION_QUALITY_SEVERITY_UNSUPPORTED",
                "Quality issue contains an unsupported severity.");
        }

        if (string.IsNullOrWhiteSpace(issue.Detail) || issue.Detail.Length > 2000 ||
            issue.Detail.Any(char.IsControl))
        {
            throw Integrity(
                "INGESTION_QUALITY_DETAIL_INVALID",
                "Quality issue detail must be non-empty, bounded and contain no control characters.");
        }

        return issue with
        {
            Code = issue.Code.Trim(),
            Detail = issue.Detail.Trim(),
        };
    }

    private static void RequireExactDigest(
        string expected,
        string actual,
        string code,
        string message)
    {
        RequireDigest(expected, nameof(expected));
        RequireDigest(actual, nameof(actual));
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw Integrity(code, message);
        }
    }

    private static void RequireDigest(string digest, string parameterName)
    {
        if (digest is not { Length: 64 } ||
            digest.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw Integrity(
                "INGESTION_DIGEST_INVALID",
                $"'{parameterName}' must be a lowercase SHA-256 hex digest.");
        }
    }

    private static void RequireItemKey(string itemKey)
    {
        if (string.IsNullOrWhiteSpace(itemKey) || itemKey.Length > 300 ||
            !ItemKeyPattern.IsMatch(itemKey))
        {
            throw Integrity(
                "INGESTION_ITEM_KEY_INVALID",
                "Item keys must be stable printable identifiers of at most 300 characters.");
        }
    }

    private static void RequireSemanticKey(
        string value,
        string parameterName,
        int maximumLength,
        bool allowCamelCase)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            !SemanticKeyPattern.IsMatch(value) ||
            (!allowCamelCase && value.Any(char.IsUpper)))
        {
            throw Integrity(
                "INGESTION_SEMANTIC_KEY_INVALID",
                $"'{parameterName}' is not a valid semantic key.");
        }
    }

    private static IngestionPackageIntegrityException Integrity(string code, string message) =>
        new(code, message);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
        return options;
    }

    [GeneratedRegex("^[^\\p{C}\\s][^\\p{C}]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ItemKeyRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticKeyRegex();
}
