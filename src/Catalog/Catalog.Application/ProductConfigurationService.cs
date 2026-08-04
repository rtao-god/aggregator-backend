using System.Collections.Immutable;
using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

public sealed class ProductConfigurationService
{
    private readonly IProductConfigurationStore _store;
    private readonly TimeProvider _timeProvider;

    public ProductConfigurationService(IProductConfigurationStore store, TimeProvider timeProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<ProductConfigurationRevisionDto> ImportAsync(
        ImportProductConfigurationRequest request,
        ActorId actorId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureIdentifier(actorId.Value, nameof(actorId));
        var normalized = Normalize(request);
        var definition = BuildDefinition(normalized);
        var canonicalSnapshot = CatalogCanonicalJson.SerializeToString(normalized);
        var contentDigest = CatalogCanonicalJson.ComputeDigest(normalized);
        var revision = ProductConfigurationRevision.Create(
            ProductConfigurationRevisionId.New(),
            normalized.SemanticIdentity,
            contentDigest,
            normalized.SourceCommitIdentity,
            actorId,
            _timeProvider.GetUtcNow(),
            definition);
        var catalogId = new CatalogId(normalized.Catalog.CatalogId);
        var envelope = new ProductConfigurationRevisionEnvelope(
            revision,
            catalogId,
            new TaxonomyRevisionId(normalized.Catalog.TaxonomyRevisionId),
            new AttributeRevisionId(normalized.Catalog.AttributeRevisionId),
            new MarketAreaRevisionId(normalized.Catalog.MarketAreaRevisionId),
            canonicalSnapshot,
            Active: false);
        var command = CatalogCommandIdentity.Create(
            $"product-configuration/import/{catalogId.Value:D}",
            idempotencyKey,
            contentDigest);
        var result = await _store.SaveRevisionAsync(envelope, command, cancellationToken);
        return ToDto(result.Value);
    }

    public async Task<ProductConfigurationActivationDto> ActivateAsync(
        CatalogId catalogId,
        ProductConfigurationRevisionId revisionId,
        ProductConfigurationRevisionId? expectedActiveRevisionId,
        string reason,
        ActorId actorId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        EnsureIdentifier(catalogId.Value, nameof(catalogId));
        EnsureIdentifier(revisionId.Value, nameof(revisionId));
        EnsureIdentifier(actorId.Value, nameof(actorId));
        if (expectedActiveRevisionId is { } expected)
        {
            EnsureIdentifier(expected.Value, nameof(expectedActiveRevisionId));
        }

        RequireReason(reason);
        var revision = await _store.GetRevisionAsync(revisionId, cancellationToken)
            ?? throw NotFound(
                "Catalog.ProductConfiguration",
                "PRODUCT_CONFIGURATION_REVISION_NOT_FOUND",
                "The requested product configuration revision does not exist.",
                new Dictionary<string, object?> { ["revisionId"] = revisionId.Value });
        if (revision.CatalogId != catalogId)
        {
            throw new CatalogCommandException(
                "Catalog.ProductConfiguration",
                "PRODUCT_CONFIGURATION_CATALOG_MISMATCH",
                409,
                "The configuration revision belongs to a different catalog.",
                "Select a revision owned by the target catalog.",
                new Dictionary<string, object?>
                {
                    ["catalogId"] = catalogId.Value,
                    ["revisionCatalogId"] = revision.CatalogId.Value,
                    ["revisionId"] = revisionId.Value,
                });
        }

        var canonicalCommand = new ActivationCommand(
            catalogId.Value,
            revisionId.Value,
            expectedActiveRevisionId?.Value,
            reason,
            actorId.Value);
        var command = CatalogCommandIdentity.Create(
            $"product-configuration/activate/{catalogId.Value:D}",
            idempotencyKey,
            CatalogCanonicalJson.ComputeDigest(canonicalCommand));
        var result = await _store.ActivateAsync(
            catalogId,
            revisionId,
            expectedActiveRevisionId,
            actorId,
            _timeProvider.GetUtcNow(),
            reason,
            command,
            cancellationToken);
        return new ProductConfigurationActivationDto(
            result.Value.CatalogId.Value,
            result.Value.RevisionId.Value,
            result.Value.PreviousRevisionId?.Value,
            result.Value.ActivatedBy.Value,
            result.Value.ActivatedAtUtc,
            result.Value.Reason);
    }

    private static ProductConfigurationDefinition BuildDefinition(ImportProductConfigurationRequest request)
    {
        var site = new SiteConfigurationDefinition(
            request.Site.Key,
            request.Site.DefaultLocale,
            request.Site.SupportedLocales.ToImmutableArray(),
            request.Site.DefaultCurrency,
            request.Site.TimeZone,
            request.Site.BrandKey,
            request.Site.HostMappings.ToImmutableArray(),
            request.Site.LegalPageReferences.ToImmutableDictionary(StringComparer.Ordinal));
        var catalog = new CatalogConfigurationDefinition(
            request.Catalog.Key,
            request.Catalog.SiteKey,
            request.Catalog.Titles.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            request.Catalog.MarketAreaKey,
            request.Catalog.Currency,
            request.Catalog.TimeZone,
            request.Catalog.SupportedListingKinds
                .Select(CatalogContractMapper.ToConfiguredDomain)
                .ToImmutableHashSet(),
            request.Catalog.SeoPolicyKey,
            request.Catalog.PublicationPolicyKey,
            request.Catalog.ContactPolicyKey,
            request.Catalog.ClaimPolicyKey,
            request.Catalog.PromotionEligibilityPolicyKey);
        var categories = request.Categories.Select(item => new CategoryDefinition(
            item.Key,
            item.ParentKey,
            item.Names.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            item.Slugs.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            item.AllowedListingKinds.Select(CatalogContractMapper.ToConfiguredDomain).ToImmutableHashSet(),
            item.PrimaryAllowed,
            item.SeoIndexable,
            item.SortOrder));
        var attributes = request.Attributes.Select(item => new AttributeDefinition(
            item.Key,
            CatalogContractMapper.ToConfiguredDomain(item.DataType),
            item.Multiple,
            item.Filterable,
            item.Comparable,
            item.Sortable,
            item.Public,
            item.AllowedOptions.ToImmutableArray(),
            item.Labels.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase)));
        var categoryAttributes = request.CategoryAttributes.Select(item => new CategoryAttributeDefinition(
            item.CategoryKey,
            item.AttributeKey,
            item.RequiredForDraft,
            item.RequiredForPublication,
            item.FilterableInCategory,
            item.Comparable,
            item.VisibleInCard,
            item.AllowedListingKinds.Select(CatalogContractMapper.ToConfiguredDomain).ToImmutableHashSet(),
            item.DisplayGroup,
            item.DisplayOrder));
        return ProductConfigurationDefinition.Create(site, catalog, categories, attributes, categoryAttributes);
    }

    private static ImportProductConfigurationRequest Normalize(ImportProductConfigurationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Site);
        ArgumentNullException.ThrowIfNull(request.Catalog);
        ArgumentNullException.ThrowIfNull(request.Categories);
        ArgumentNullException.ThrowIfNull(request.Attributes);
        ArgumentNullException.ThrowIfNull(request.CategoryAttributes);
        ArgumentNullException.ThrowIfNull(request.Site.SupportedLocales);
        ArgumentNullException.ThrowIfNull(request.Site.HostMappings);
        ArgumentNullException.ThrowIfNull(request.Site.LegalPageReferences);
        ArgumentNullException.ThrowIfNull(request.Catalog.Titles);
        ArgumentNullException.ThrowIfNull(request.Catalog.SupportedListingKinds);
        EnsureIdentifier(request.Catalog.CatalogId, nameof(request.Catalog.CatalogId));
        EnsureIdentifier(request.Catalog.TaxonomyRevisionId, nameof(request.Catalog.TaxonomyRevisionId));
        EnsureIdentifier(request.Catalog.AttributeRevisionId, nameof(request.Catalog.AttributeRevisionId));
        EnsureIdentifier(request.Catalog.MarketAreaRevisionId, nameof(request.Catalog.MarketAreaRevisionId));

        var site = request.Site with
        {
            SupportedLocales = request.Site.SupportedLocales.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            HostMappings = request.Site.HostMappings.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray(),
        };
        var catalog = request.Catalog with
        {
            SupportedListingKinds = request.Catalog.SupportedListingKinds.Distinct().OrderBy(item => item).ToArray(),
        };
        var categories = request.Categories
            .Select(item => item with
            {
                AllowedListingKinds = item.AllowedListingKinds.Distinct().OrderBy(kind => kind).ToArray(),
            })
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
        var attributes = request.Attributes
            .Select(item => item with
            {
                AllowedOptions = item.AllowedOptions.OrderBy(option => option, StringComparer.Ordinal).ToArray(),
            })
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
        var relations = request.CategoryAttributes
            .Select(item => item with
            {
                AllowedListingKinds = item.AllowedListingKinds.Distinct().OrderBy(kind => kind).ToArray(),
            })
            .OrderBy(item => item.CategoryKey, StringComparer.Ordinal)
            .ThenBy(item => item.DisplayOrder)
            .ThenBy(item => item.AttributeKey, StringComparer.Ordinal)
            .ToArray();
        return request with
        {
            Site = site,
            Catalog = catalog,
            Categories = categories,
            Attributes = attributes,
            CategoryAttributes = relations,
        };
    }

    private static ProductConfigurationRevisionDto ToDto(ProductConfigurationRevisionEnvelope envelope) =>
        new(
            envelope.Revision.Id.Value,
            envelope.CatalogId.Value,
            envelope.Revision.SemanticIdentity,
            envelope.Revision.ContentDigest,
            envelope.Revision.SourceCommitIdentity,
            envelope.Revision.CreatedBy.Value,
            envelope.Revision.CreatedAtUtc,
            envelope.Active);

    private static void EnsureIdentifier(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new CatalogCommandException(
                "Catalog.Contracts",
                "IDENTIFIER_REQUIRED",
                400,
                $"'{parameterName}' must be a non-empty UUID.",
                "Provide the exact owner identity in UUID form.");
        }
    }

    private static void RequireReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1_000)
        {
            throw new CatalogCommandException(
                "Catalog.ProductConfiguration",
                "ACTIVATION_REASON_INVALID",
                400,
                "Activation reason must contain between 1 and 1000 characters.",
                "Provide a concise auditable activation reason.");
        }
    }

    private static CatalogCommandException NotFound(
        string owner,
        string code,
        string message,
        IReadOnlyDictionary<string, object?> context) =>
        new(owner, code, 404, message, "Use an existing exact owner identity.", context);

    private sealed record ActivationCommand(
        Guid CatalogId,
        Guid RevisionId,
        Guid? ExpectedActiveRevisionId,
        string Reason,
        Guid ActorId);
}
