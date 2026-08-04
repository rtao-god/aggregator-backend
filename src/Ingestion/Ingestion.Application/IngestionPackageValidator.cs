using Aggregator.Ingestion.Contracts;
using Aggregator.Ingestion.Domain;

namespace Aggregator.Ingestion.Application;

public sealed record ValidatedIngestionManifest(
    AggregatorCandidateIngestionManifest Manifest,
    IngestionPackageArtifactContract PayloadArtifact,
    string ManifestDigest);

public sealed record ValidatedIngestionItem(
    IngestionItemContract Item,
    ImportItemDecisionKind Decision,
    string ReasonCode);

public sealed record IngestionPackageValidationResult(
    ValidatedIngestionManifest Manifest,
    IReadOnlyList<ValidatedIngestionItem> Items,
    string ItemIndexDigest,
    string PayloadDigest);

/// <summary>Owns integrity and item-decision validation for the backend ingestion contract.</summary>
public static class IngestionPackageValidator
{
    public const int MaximumItemCount = 100_000;

    public const long MaximumPayloadBytes = 5L * 1024 * 1024 * 1024;

    private static readonly HashSet<string> PayloadContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/json",
        "application/x-ndjson",
        "application/gzip",
    };

    public static ValidatedIngestionManifest ValidateManifest(
        AggregatorCandidateIngestionManifest manifest,
        string expectedManifestDigest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        RequireDigest(expectedManifestDigest, nameof(expectedManifestDigest));
        if (!string.Equals(
                manifest.ContractIdentity,
                AggregatorCandidateIngestionContract.Identity,
                StringComparison.Ordinal) ||
            manifest.ContractRevision != AggregatorCandidateIngestionContract.Revision)
        {
            throw Failure(
                "Ingestion.Contract",
                "INGESTION_CONTRACT_UNSUPPORTED",
                422,
                $"Ingestion contract '{manifest.ContractIdentity}' revision '{manifest.ContractRevision}' is unsupported.",
                "Generate and use the exact backend-owned ingestion client supported by this deployment.");
        }

        RequireText(manifest.ProducerIdentity, nameof(manifest.ProducerIdentity), 200);
        RequireText(manifest.ProducerBuild, nameof(manifest.ProducerBuild), 200);
        RequireId(manifest.CollectorExportId, nameof(manifest.CollectorExportId));
        RequireDigest(manifest.CollectorExportDigest, nameof(manifest.CollectorExportDigest));
        RequireProductKey(manifest.TargetSiteKey, nameof(manifest.TargetSiteKey));
        RequireProductKey(manifest.TargetCatalogKey, nameof(manifest.TargetCatalogKey));
        RequireId(
            manifest.TargetCatalogConfigurationRevisionId,
            nameof(manifest.TargetCatalogConfigurationRevisionId));
        RequireUtc(manifest.CreatedAtUtc, nameof(manifest.CreatedAtUtc));
        if (manifest.ItemCount is < 1 or > MaximumItemCount)
        {
            throw Failure(
                "Ingestion.Contract",
                "INGESTION_ITEM_COUNT_INVALID",
                422,
                $"Manifest item count must be between 1 and {MaximumItemCount}.",
                "Split the collector export into explicit bounded packages.");
        }

        RequireDigest(manifest.ItemIndexDigest, nameof(manifest.ItemIndexDigest));
        RequireDigest(manifest.PayloadDigest, nameof(manifest.PayloadDigest));
        ArgumentNullException.ThrowIfNull(manifest.SourcePolicies);
        if (manifest.SourcePolicies.Count == 0)
        {
            throw Failure(
                "Ingestion.Policy",
                "INGESTION_SOURCE_POLICY_REQUIRED",
                422,
                "The manifest must identify every source-policy revision represented by the package.",
                "Regenerate the collector export with exact source-policy references.");
        }

        var sourceKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourcePolicy in manifest.SourcePolicies)
        {
            ArgumentNullException.ThrowIfNull(sourcePolicy);
            RequireProductKey(sourcePolicy.SourceKey, nameof(sourcePolicy.SourceKey));
            RequireDigest(sourcePolicy.PolicyDigest, nameof(sourcePolicy.PolicyDigest));
            if (!Enum.IsDefined(sourcePolicy.UsagePolicy))
            {
                throw Failure(
                    "Ingestion.Policy",
                    "INGESTION_SOURCE_POLICY_INVALID",
                    422,
                    $"Source '{sourcePolicy.SourceKey}' has an unsupported usage policy.",
                    "Regenerate the collector export using a supported source-policy contract.");
            }

            if (!sourceKeys.Add(sourcePolicy.SourceKey))
            {
                throw Failure(
                    "Ingestion.Contract",
                    "INGESTION_SOURCE_POLICY_DUPLICATE",
                    422,
                    $"Source policy '{sourcePolicy.SourceKey}' is declared more than once.",
                    "Emit one exact source-policy reference per source key.");
            }

            if (sourcePolicy.UsagePolicy is CandidateUsagePolicyContract.ResearchOnly or CandidateUsagePolicyContract.Forbidden)
            {
                throw Failure(
                    "Ingestion.Policy",
                    "INGESTION_SOURCE_POLICY_NOT_ALLOWED",
                    422,
                    $"Source policy '{sourcePolicy.SourceKey}' cannot authorize a production ingestion package.",
                    "Remove research-only or forbidden source content before producing the collector export.");
            }
        }

        ArgumentNullException.ThrowIfNull(manifest.Artifacts);
        if (manifest.Artifacts.Count == 0)
        {
            throw Failure(
                "Ingestion.Contract",
                "INGESTION_ARTIFACT_REQUIRED",
                422,
                "The manifest must reference a candidate payload artifact.",
                "Upload the exact package payload and register its object metadata.");
        }

        var objectKeys = new HashSet<string>(StringComparer.Ordinal);
        var payloadArtifacts = new List<IngestionPackageArtifactContract>();
        foreach (var artifact in manifest.Artifacts)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            if (!Enum.IsDefined(artifact.Role))
            {
                throw Failure(
                    "Ingestion.Contract",
                    "INGESTION_ARTIFACT_ROLE_INVALID",
                    422,
                    $"Artifact role '{artifact.Role}' is unsupported.",
                    "Use an artifact role declared by the current ingestion contract.");
            }

            RequireText(artifact.ObjectKey, nameof(artifact.ObjectKey), 1024);
            if (artifact.ObjectKey.StartsWith('/') ||
                artifact.ObjectKey.Contains("..", StringComparison.Ordinal) ||
                artifact.ObjectKey.Contains('\\'))
            {
                throw Failure(
                    "Ingestion.ObjectStorage",
                    "INGESTION_ARTIFACT_KEY_INVALID",
                    422,
                    $"Artifact object key '{artifact.ObjectKey}' is outside the allowed opaque-key contract.",
                    "Use an owner-generated object key without rooted or traversal segments.");
            }

            if (!objectKeys.Add(artifact.ObjectKey))
            {
                throw Failure(
                    "Ingestion.Contract",
                    "INGESTION_ARTIFACT_DUPLICATE",
                    422,
                    $"Artifact object key '{artifact.ObjectKey}' is duplicated.",
                    "Reference each uploaded object exactly once.");
            }

            RequireDigest(artifact.ContentDigest, nameof(artifact.ContentDigest));
            if (artifact.Size <= 0 || artifact.Size > MaximumPayloadBytes)
            {
                throw Failure(
                    "Ingestion.Contract",
                    "INGESTION_ARTIFACT_SIZE_INVALID",
                    422,
                    $"Artifact '{artifact.ObjectKey}' has an invalid declared size.",
                    "Upload an artifact within the configured ingestion size limit.");
            }

            RequireText(artifact.ContentType, nameof(artifact.ContentType), 200);
            if (artifact.Role == IngestionArtifactRoleContract.CandidatePayload)
            {
                payloadArtifacts.Add(artifact);
            }
        }

        if (payloadArtifacts.Count != 1)
        {
            throw Failure(
                "Ingestion.Contract",
                "INGESTION_PAYLOAD_ARTIFACT_CARDINALITY_INVALID",
                422,
                "The manifest must contain exactly one candidate payload artifact.",
                "Register one exact payload artifact and move supporting objects to other declared roles.");
        }

        var payloadArtifact = payloadArtifacts[0];
        if (!PayloadContentTypes.Contains(payloadArtifact.ContentType))
        {
            throw Failure(
                "Ingestion.Contract",
                "INGESTION_PAYLOAD_CONTENT_TYPE_UNSUPPORTED",
                422,
                $"Payload content type '{payloadArtifact.ContentType}' is unsupported.",
                "Serialize the package as JSON, NDJSON, or a declared gzip representation.");
        }

        var manifestDigest = ComputeManifestDigest(manifest);
        if (!string.Equals(manifestDigest, expectedManifestDigest, StringComparison.Ordinal))
        {
            throw Failure(
                "Ingestion.Integrity",
                "INGESTION_MANIFEST_DIGEST_MISMATCH",
                422,
                "The registered manifest digest does not match the canonical manifest content.",
                "Regenerate and upload the manifest without modifying it after digest calculation.");
        }

        return new ValidatedIngestionManifest(manifest, payloadArtifact, manifestDigest);
    }

    public static IngestionPackageValidationResult ValidatePackage(
        AggregatorCandidateIngestionPackage package,
        string expectedManifestDigest)
    {
        ArgumentNullException.ThrowIfNull(package);
        var validatedManifest = ValidateManifest(package.Manifest, expectedManifestDigest);
        ArgumentNullException.ThrowIfNull(package.Items);
        if (package.Items.Count != package.Manifest.ItemCount)
        {
            throw Failure(
                "Ingestion.Integrity",
                "INGESTION_ITEM_COUNT_MISMATCH",
                422,
                $"Manifest declares {package.Manifest.ItemCount} items but payload contains {package.Items.Count}.",
                "Regenerate the entire package; partial package acceptance is forbidden.");
        }

        var orderedItems = package.Items.OrderBy(item => item.ItemKey, StringComparer.Ordinal).ToArray();
        var itemKeys = new HashSet<string>(StringComparer.Ordinal);
        var validatedItems = new List<ValidatedIngestionItem>(orderedItems.Length);
        foreach (var item in orderedItems)
        {
            ArgumentNullException.ThrowIfNull(item);
            RequireSemanticKey(item.ItemKey, nameof(item.ItemKey));
            if (!itemKeys.Add(item.ItemKey))
            {
                throw Failure(
                    "Ingestion.Integrity",
                    "INGESTION_ITEM_KEY_DUPLICATE",
                    422,
                    $"Item key '{item.ItemKey}' occurs more than once.",
                    "Regenerate the collector export with unique item keys.");
            }

            RequireId(item.CollectorCandidateId, nameof(item.CollectorCandidateId));
            if (item.CollectorCandidateRevision <= 0)
            {
                throw Failure(
                    "Ingestion.Integrity",
                    "INGESTION_CANDIDATE_REVISION_INVALID",
                    422,
                    $"Item '{item.ItemKey}' has a non-positive collector candidate revision.",
                    "Regenerate the collector export from an immutable positive candidate revision.");
            }

            RequireDigest(item.ContentDigest, nameof(item.ContentDigest));
            var computedItemDigest = ComputeItemContentDigest(item);
            if (!string.Equals(computedItemDigest, item.ContentDigest, StringComparison.Ordinal))
            {
                throw Failure(
                    "Ingestion.Integrity",
                    "INGESTION_ITEM_DIGEST_MISMATCH",
                    422,
                    $"Item '{item.ItemKey}' content digest is invalid.",
                    "Regenerate the entire package from the exact collector candidate revision.");
            }

            validatedItems.Add(ClassifyItem(item, validatedManifest.Manifest));
        }

        var itemIndexDigest = IngestionCanonicalJson.ComputeDigest(
            orderedItems.Select(item => new { item.ItemKey, item.ContentDigest }).ToArray());
        if (!string.Equals(itemIndexDigest, package.Manifest.ItemIndexDigest, StringComparison.Ordinal))
        {
            throw Failure(
                "Ingestion.Integrity",
                "INGESTION_ITEM_INDEX_DIGEST_MISMATCH",
                422,
                "The package item index digest does not match its exact ordered item identities.",
                "Regenerate the entire package; do not repair or reorder it at the backend boundary.");
        }

        var payloadDigest = IngestionCanonicalJson.ComputeDigest(new { items = orderedItems });
        if (!string.Equals(payloadDigest, package.Manifest.PayloadDigest, StringComparison.Ordinal))
        {
            throw Failure(
                "Ingestion.Integrity",
                "INGESTION_PAYLOAD_DIGEST_MISMATCH",
                422,
                "The canonical package payload digest does not match the manifest.",
                "Regenerate and upload the exact complete payload.");
        }

        return new IngestionPackageValidationResult(
            validatedManifest,
            validatedItems.AsReadOnly(),
            itemIndexDigest,
            payloadDigest);
    }

    public static string ComputeManifestDigest(AggregatorCandidateIngestionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return IngestionCanonicalJson.ComputeDigest(new
        {
            manifest.ContractIdentity,
            manifest.ContractRevision,
            manifest.ProducerIdentity,
            manifest.ProducerBuild,
            manifest.CollectorExportId,
            manifest.CollectorExportDigest,
            manifest.TargetSiteKey,
            manifest.TargetCatalogKey,
            manifest.TargetCatalogConfigurationRevisionId,
            manifest.CreatedAtUtc,
            manifest.ItemCount,
            manifest.ItemIndexDigest,
            manifest.PayloadDigest,
            sourcePolicies = manifest.SourcePolicies
                .OrderBy(item => item.SourceKey, StringComparer.Ordinal)
                .ToArray(),
            artifacts = manifest.Artifacts
                .OrderBy(item => item.Role)
                .ThenBy(item => item.ObjectKey, StringComparer.Ordinal)
                .ToArray(),
        });
    }

    public static string ComputeItemContentDigest(IngestionItemContract item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return IngestionCanonicalJson.ComputeDigest(new
        {
            item.ItemKey,
            item.CollectorCandidateId,
            item.CollectorCandidateRevision,
            item.EntityKind,
            item.SubjectProposal,
            item.LocalizedNames,
            item.CategoryProposals,
            item.TypedAttributeProposals,
            item.ContactProposals,
            item.ExternalReferenceProposals,
            item.GeographyProposal,
            item.RelationshipProposals,
            item.ProvenanceReferences,
            item.QualitySummary,
            item.CollectorReviewReferences,
        });
    }

    public static string ComputeItemIndexDigest(IEnumerable<IngestionItemContract> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var orderedItems = items.OrderBy(item => item.ItemKey, StringComparer.Ordinal).ToArray();
        return IngestionCanonicalJson.ComputeDigest(
            orderedItems.Select(item => new { item.ItemKey, item.ContentDigest }).ToArray());
    }

    public static string ComputePayloadDigest(IEnumerable<IngestionItemContract> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var orderedItems = items.OrderBy(item => item.ItemKey, StringComparer.Ordinal).ToArray();
        return IngestionCanonicalJson.ComputeDigest(new { items = orderedItems });
    }

    private static ValidatedIngestionItem ClassifyItem(
        IngestionItemContract item,
        AggregatorCandidateIngestionManifest manifest)
    {
        var rejectionCodes = new SortedSet<string>(StringComparer.Ordinal);
        var reviewCodes = new SortedSet<string>(StringComparer.Ordinal);
        if (!Enum.IsDefined(item.EntityKind))
        {
            rejectionCodes.Add("INGESTION_ITEM_ENTITY_KIND_INVALID");
        }

        ValidateSubjectProposal(item, rejectionCodes);
        ValidateLocalizedNames(item, rejectionCodes);
        ValidateCategories(item, reviewCodes, rejectionCodes);
        ValidateAttributes(item, rejectionCodes, reviewCodes);
        ValidateContacts(item, rejectionCodes, reviewCodes);
        ValidateExternalReferences(item, rejectionCodes);
        ValidateGeography(item, rejectionCodes, reviewCodes);
        ValidateRelationships(item, rejectionCodes);
        ValidateProvenance(item, manifest, rejectionCodes, reviewCodes);
        ValidateQuality(item, rejectionCodes, reviewCodes);
        ValidateReviewReferences(item, rejectionCodes);

        if (rejectionCodes.Count > 0)
        {
            return new ValidatedIngestionItem(
                item,
                ImportItemDecisionKind.Rejected,
                string.Join('+', rejectionCodes));
        }

        if (reviewCodes.Count > 0)
        {
            return new ValidatedIngestionItem(
                item,
                ImportItemDecisionKind.NeedsReview,
                string.Join('+', reviewCodes));
        }

        return new ValidatedIngestionItem(
            item,
            ImportItemDecisionKind.Accepted,
            "INGESTION_ITEM_CONTRACT_ACCEPTED");
    }

    private static void ValidateSubjectProposal(
        IngestionItemContract item,
        ISet<string> rejectionCodes)
    {
        if (item.SubjectProposal is null)
        {
            rejectionCodes.Add("INGESTION_SUBJECT_PROPOSAL_REQUIRED");
            return;
        }

        if (!IsText(item.SubjectProposal.SourceSubjectKey, 200))
        {
            rejectionCodes.Add("INGESTION_SUBJECT_KEY_INVALID");
        }

        if (item.SubjectProposal.OfficialDomain is { } officialDomain &&
            Uri.CheckHostName(officialDomain) == UriHostNameType.Unknown)
        {
            rejectionCodes.Add("INGESTION_OFFICIAL_DOMAIN_INVALID");
        }

        if (item.SubjectProposal.NormalizedPhoneHash is { } phoneHash && !IsDigest(phoneHash))
        {
            rejectionCodes.Add("INGESTION_PHONE_HASH_INVALID");
        }

        if (item.SubjectProposal.NormalizedAddressKey is { } addressKey && !IsText(addressKey, 500))
        {
            rejectionCodes.Add("INGESTION_ADDRESS_KEY_INVALID");
        }

        if (item.SubjectProposal.ExternalIdentityKeys is null ||
            item.SubjectProposal.ExternalIdentityKeys.Any(value => !IsText(value, 500)) ||
            item.SubjectProposal.ExternalIdentityKeys.Distinct(StringComparer.Ordinal).Count() !=
            item.SubjectProposal.ExternalIdentityKeys.Count)
        {
            rejectionCodes.Add("INGESTION_EXTERNAL_IDENTITY_KEYS_INVALID");
        }
    }

    private static void ValidateLocalizedNames(
        IngestionItemContract item,
        ISet<string> rejectionCodes)
    {
        if (item.LocalizedNames is null || item.LocalizedNames.Count == 0)
        {
            rejectionCodes.Add("INGESTION_LOCALIZED_NAME_REQUIRED");
            return;
        }

        var locales = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var observedNameExists = false;
        foreach (var localizedName in item.LocalizedNames)
        {
            if (localizedName is null ||
                !IsText(localizedName.Locale, 35) ||
                !IsText(localizedName.FieldPath, 500) ||
                !Enum.IsDefined(localizedName.State))
            {
                rejectionCodes.Add("INGESTION_LOCALIZED_NAME_INVALID");
                continue;
            }

            if (!locales.Add(localizedName.Locale))
            {
                rejectionCodes.Add("INGESTION_LOCALIZED_NAME_DUPLICATE");
            }

            if (localizedName.State == CandidateFieldStateContract.Observed)
            {
                observedNameExists |= IsText(localizedName.Value, 500);
                if (!IsText(localizedName.Value, 500))
                {
                    rejectionCodes.Add("INGESTION_OBSERVED_NAME_VALUE_REQUIRED");
                }
            }
            else if (localizedName.Value is not null)
            {
                rejectionCodes.Add("INGESTION_NAME_STATE_VALUE_CONFLICT");
            }
        }

        if (!observedNameExists)
        {
            rejectionCodes.Add("INGESTION_OBSERVED_NAME_REQUIRED");
        }
    }

    private static void ValidateCategories(
        IngestionItemContract item,
        ISet<string> reviewCodes,
        ISet<string> rejectionCodes)
    {
        if (item.CategoryProposals is null)
        {
            rejectionCodes.Add("INGESTION_CATEGORY_COLLECTION_REQUIRED");
            return;
        }

        if (item.CategoryProposals.Any(value => !IsProductKey(value)) ||
            item.CategoryProposals.Distinct(StringComparer.Ordinal).Count() != item.CategoryProposals.Count)
        {
            rejectionCodes.Add("INGESTION_CATEGORY_PROPOSALS_INVALID");
        }

        if (item.EntityKind is IngestionEntityKindContract.Place or IngestionEntityKindContract.Provider &&
            item.CategoryProposals.Count == 0)
        {
            reviewCodes.Add("INGESTION_CATEGORY_MAPPING_REQUIRED");
        }
    }

    private static void ValidateAttributes(
        IngestionItemContract item,
        ISet<string> rejectionCodes,
        ISet<string> reviewCodes)
    {
        if (item.TypedAttributeProposals is null)
        {
            rejectionCodes.Add("INGESTION_ATTRIBUTE_COLLECTION_REQUIRED");
            return;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in item.TypedAttributeProposals)
        {
            if (attribute is null ||
                !IsProductKey(attribute.AttributeKey) ||
                !IsText(attribute.FieldPath, 500) ||
                !Enum.IsDefined(attribute.State))
            {
                rejectionCodes.Add("INGESTION_ATTRIBUTE_PROPOSAL_INVALID");
                continue;
            }

            if (!keys.Add(attribute.AttributeKey))
            {
                rejectionCodes.Add("INGESTION_ATTRIBUTE_PROPOSAL_DUPLICATE");
            }

            if (attribute.State == CandidateFieldStateContract.Observed)
            {
                if (attribute.Value is null || !IsValidTypedValue(attribute.Value))
                {
                    rejectionCodes.Add("INGESTION_ATTRIBUTE_VALUE_INVALID");
                }
            }
            else if (attribute.Value is not null)
            {
                rejectionCodes.Add("INGESTION_ATTRIBUTE_STATE_VALUE_CONFLICT");
            }

            if (attribute.State is CandidateFieldStateContract.Disputed or CandidateFieldStateContract.Expired)
            {
                reviewCodes.Add("INGESTION_ATTRIBUTE_REVIEW_REQUIRED");
            }
        }
    }

    private static void ValidateContacts(
        IngestionItemContract item,
        ISet<string> rejectionCodes,
        ISet<string> reviewCodes)
    {
        if (item.ContactProposals is null)
        {
            rejectionCodes.Add("INGESTION_CONTACT_COLLECTION_REQUIRED");
            return;
        }

        foreach (var contact in item.ContactProposals)
        {
            if (contact is null ||
                !Enum.IsDefined(contact.Kind) ||
                !Enum.IsDefined(contact.State) ||
                !IsText(contact.FieldPath, 500))
            {
                rejectionCodes.Add("INGESTION_CONTACT_PROPOSAL_INVALID");
                continue;
            }

            if (contact.State == CandidateFieldStateContract.Observed)
            {
                if (!IsText(contact.Target, 2048))
                {
                    rejectionCodes.Add("INGESTION_CONTACT_TARGET_REQUIRED");
                }
            }
            else if (contact.Target is not null || contact.Label is not null)
            {
                rejectionCodes.Add("INGESTION_CONTACT_STATE_VALUE_CONFLICT");
            }

            if (contact.State is CandidateFieldStateContract.Disputed or CandidateFieldStateContract.Expired)
            {
                reviewCodes.Add("INGESTION_CONTACT_REVIEW_REQUIRED");
            }
        }
    }

    private static void ValidateExternalReferences(
        IngestionItemContract item,
        ISet<string> rejectionCodes)
    {
        if (item.ExternalReferenceProposals is null)
        {
            rejectionCodes.Add("INGESTION_EXTERNAL_REFERENCE_COLLECTION_REQUIRED");
            return;
        }

        foreach (var reference in item.ExternalReferenceProposals)
        {
            if (reference is null ||
                !IsText(reference.SourceSystem, 100) ||
                !IsText(reference.ExternalId, 500) ||
                !IsText(reference.Purpose, 100) ||
                !IsText(reference.FieldPath, 500) ||
                !Enum.IsDefined(reference.UsagePolicy) ||
                !IsHttpUrl(reference.OutboundUrl))
            {
                rejectionCodes.Add("INGESTION_EXTERNAL_REFERENCE_INVALID");
            }
        }
    }

    private static void ValidateGeography(
        IngestionItemContract item,
        ISet<string> rejectionCodes,
        ISet<string> reviewCodes)
    {
        var geography = item.GeographyProposal;
        if (geography is null)
        {
            if (item.EntityKind == IngestionEntityKindContract.Place)
            {
                reviewCodes.Add("INGESTION_PLACE_GEOGRAPHY_REQUIRED");
            }

            return;
        }

        if (!Enum.IsDefined(geography.State) || !IsText(geography.FieldPath, 500))
        {
            rejectionCodes.Add("INGESTION_GEOGRAPHY_PROPOSAL_INVALID");
            return;
        }

        if (geography.State == CandidateGeographyStateContract.ProposedPoint)
        {
            if (geography.Latitude is not (>= -90 and <= 90) ||
                geography.Longitude is not (>= -180 and <= 180))
            {
                rejectionCodes.Add("INGESTION_GEOGRAPHY_POINT_INVALID");
            }
        }
        else if (geography.Latitude is not null || geography.Longitude is not null)
        {
            rejectionCodes.Add("INGESTION_GEOGRAPHY_STATE_VALUE_CONFLICT");
        }

        if (geography.State == CandidateGeographyStateContract.Unresolved)
        {
            reviewCodes.Add("INGESTION_GEOGRAPHY_REVIEW_REQUIRED");
        }
    }

    private static void ValidateRelationships(
        IngestionItemContract item,
        ISet<string> rejectionCodes)
    {
        if (item.RelationshipProposals is null)
        {
            rejectionCodes.Add("INGESTION_RELATIONSHIP_COLLECTION_REQUIRED");
            return;
        }

        foreach (var relationship in item.RelationshipProposals)
        {
            if (relationship is null ||
                !Enum.IsDefined(relationship.Kind) ||
                relationship.RelatedCollectorCandidateId == Guid.Empty ||
                relationship.RelatedCollectorCandidateRevision <= 0 ||
                !IsText(relationship.FieldPath, 500))
            {
                rejectionCodes.Add("INGESTION_RELATIONSHIP_PROPOSAL_INVALID");
            }
        }
    }

    private static void ValidateProvenance(
        IngestionItemContract item,
        AggregatorCandidateIngestionManifest manifest,
        ISet<string> rejectionCodes,
        ISet<string> reviewCodes)
    {
        if (item.ProvenanceReferences is null || item.ProvenanceReferences.Count == 0)
        {
            rejectionCodes.Add("INGESTION_PROVENANCE_REQUIRED");
            return;
        }

        var declaredSources = manifest.SourcePolicies
            .Select(policy => policy.SourceKey)
            .ToHashSet(StringComparer.Ordinal);
        var referenceIds = new HashSet<Guid>();
        foreach (var provenance in item.ProvenanceReferences)
        {
            if (provenance is null ||
                provenance.ReferenceId == Guid.Empty ||
                !referenceIds.Add(provenance.ReferenceId) ||
                !IsText(provenance.FieldPath, 500) ||
                !IsProductKey(provenance.SourceKey) ||
                !IsText(provenance.SourceExternalId, 500) ||
                !IsHttpUrl(provenance.SourceUrl) ||
                provenance.ObservedAtUtc.Offset != TimeSpan.Zero ||
                !IsDigest(provenance.EvidenceDigest) ||
                !Enum.IsDefined(provenance.UsagePolicy) ||
                !declaredSources.Contains(provenance.SourceKey))
            {
                rejectionCodes.Add("INGESTION_PROVENANCE_REFERENCE_INVALID");
                continue;
            }

            if (provenance.UsagePolicy == CandidateUsagePolicyContract.DisplayWithAttribution &&
                !IsText(provenance.Attribution, 500))
            {
                rejectionCodes.Add("INGESTION_ATTRIBUTION_REQUIRED");
            }

            if (provenance.UsagePolicy == CandidateUsagePolicyContract.LinkOnly &&
                !provenance.FieldPath.StartsWith("externalReferences/", StringComparison.Ordinal))
            {
                rejectionCodes.Add("INGESTION_LINK_ONLY_CONTENT_INVALID");
            }

            if (provenance.UsagePolicy is CandidateUsagePolicyContract.ResearchOnly or CandidateUsagePolicyContract.Forbidden)
            {
                rejectionCodes.Add("INGESTION_PROVENANCE_POLICY_FORBIDDEN");
            }
            else if (provenance.UsagePolicy == CandidateUsagePolicyContract.InternalReviewOnly)
            {
                reviewCodes.Add("INGESTION_PROVENANCE_REVIEW_REQUIRED");
            }
        }
    }

    private static void ValidateQuality(
        IngestionItemContract item,
        ISet<string> rejectionCodes,
        ISet<string> reviewCodes)
    {
        if (item.QualitySummary is null ||
            !Enum.IsDefined(item.QualitySummary.State) ||
            item.QualitySummary.Issues is null)
        {
            rejectionCodes.Add("INGESTION_QUALITY_SUMMARY_INVALID");
            return;
        }

        var blockingIssueExists = false;
        foreach (var issue in item.QualitySummary.Issues)
        {
            if (issue is null ||
                !IsText(issue.Code, 150) ||
                !Enum.IsDefined(issue.Severity) ||
                !IsText(issue.RequiredAction, 1000))
            {
                rejectionCodes.Add("INGESTION_QUALITY_ISSUE_INVALID");
                continue;
            }

            blockingIssueExists |= issue.Severity == IngestionQualitySeverityContract.Blocking;
        }

        if (item.QualitySummary.State == IngestionQualityStateContract.Passed && blockingIssueExists)
        {
            rejectionCodes.Add("INGESTION_QUALITY_SUMMARY_INCONSISTENT");
        }
        else if (item.QualitySummary.State == IngestionQualityStateContract.Blocked)
        {
            rejectionCodes.Add("INGESTION_QUALITY_BLOCKED");
        }
        else if (item.QualitySummary.State == IngestionQualityStateContract.ReviewRequired)
        {
            reviewCodes.Add("INGESTION_QUALITY_REVIEW_REQUIRED");
        }
    }

    private static void ValidateReviewReferences(
        IngestionItemContract item,
        ISet<string> rejectionCodes)
    {
        if (item.CollectorReviewReferences is null)
        {
            rejectionCodes.Add("INGESTION_REVIEW_REFERENCE_COLLECTION_REQUIRED");
            return;
        }

        foreach (var review in item.CollectorReviewReferences)
        {
            if (review is null ||
                review.DecisionId == Guid.Empty ||
                !IsText(review.DecisionKind, 100) ||
                !IsDigest(review.DecisionDigest) ||
                review.DecidedAtUtc.Offset != TimeSpan.Zero)
            {
                rejectionCodes.Add("INGESTION_REVIEW_REFERENCE_INVALID");
            }
        }
    }

    private static bool IsValidTypedValue(CandidateTypedValueContract value)
    {
        if (!Enum.IsDefined(value.Kind))
        {
            return false;
        }

        var populatedCount = 0;
        populatedCount += value.BooleanValue is null ? 0 : 1;
        populatedCount += value.DecimalValue is null ? 0 : 1;
        populatedCount += value.TextValue is null ? 0 : 1;
        populatedCount += value.TextSetValue is null ? 0 : 1;
        if (populatedCount != 1)
        {
            return false;
        }

        return value.Kind switch
        {
            CandidateValueKindContract.Boolean => value.BooleanValue is not null,
            CandidateValueKindContract.DecimalNumber => value.DecimalValue is not null,
            CandidateValueKindContract.Text => IsText(value.TextValue, 20_000),
            CandidateValueKindContract.TextSet =>
                value.TextSetValue is { Count: > 0 } values &&
                values.All(item => IsText(item, 500)) &&
                values.Distinct(StringComparer.Ordinal).Count() == values.Count,
            CandidateValueKindContract.DurationMinutes =>
                value.DecimalValue is >= 0 && decimal.Truncate(value.DecimalValue.Value) == value.DecimalValue.Value,
            _ => false,
        };
    }

    private static void RequireId(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw Failure(
                "Ingestion.Contract",
                "INGESTION_IDENTIFIER_REQUIRED",
                422,
                $"'{parameterName}' must be a non-empty UUID.",
                "Regenerate the collector export with stable non-empty identifiers.");
        }
    }

    private static void RequireText(string value, string parameterName, int maximumLength)
    {
        if (!IsText(value, maximumLength))
        {
            throw Failure(
                "Ingestion.Contract",
                "INGESTION_TEXT_INVALID",
                422,
                $"'{parameterName}' must be non-empty and no longer than {maximumLength} characters.",
                "Correct the collector export field before package registration.");
        }
    }

    private static void RequireProductKey(string value, string parameterName)
    {
        if (!IsProductKey(value))
        {
            throw Failure(
                "Ingestion.Contract",
                "INGESTION_PRODUCT_KEY_INVALID",
                422,
                $"'{parameterName}' is not a canonical lowercase product key.",
                "Use the exact site or catalog key published by the Catalog owner.");
        }
    }

    private static void RequireSemanticKey(string value, string parameterName)
    {
        if (!IsText(value, 200) || value.Any(char.IsControl))
        {
            throw Failure(
                "Ingestion.Contract",
                "INGESTION_SEMANTIC_KEY_INVALID",
                422,
                $"'{parameterName}' is not a valid semantic item key.",
                "Emit a stable non-empty key without control characters.");
        }
    }

    private static void RequireDigest(string value, string parameterName)
    {
        if (!IsDigest(value))
        {
            throw Failure(
                "Ingestion.Integrity",
                "INGESTION_DIGEST_INVALID",
                422,
                $"'{parameterName}' must be a lowercase SHA-256 hexadecimal digest.",
                "Regenerate the package digest using the canonical contract serializer.");
        }
    }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "Ingestion.Contract",
                "INGESTION_TIMESTAMP_NOT_UTC",
                422,
                $"'{parameterName}' must be normalized to UTC.",
                "Regenerate the collector export using UTC timestamps.");
        }
    }

    private static bool IsText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength;

    private static bool IsDigest(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsProductKey(string? value)
    {
        if (value is null || value.Length is < 1 or > 96 || !char.IsAsciiLetter(value[0]) || char.IsUpper(value[0]))
        {
            return false;
        }

        return value.All(character =>
            (char.IsAsciiLetterOrDigit(character) || character == '-') && !char.IsUpper(character));
    }

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static IngestionApplicationException Failure(
        string owner,
        string code,
        int statusCode,
        string message,
        string requiredAction) =>
        new(owner, code, statusCode, message, requiredAction);
}
