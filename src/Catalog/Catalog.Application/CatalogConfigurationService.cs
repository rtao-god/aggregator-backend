using System.Security.Cryptography;
using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

public sealed class CatalogConfigurationService(
    ICatalogRepository repository,
    ICatalogIdSource idSource,
    TimeProvider timeProvider)
{
    public async Task<ProductConfigurationRevisionResponse> ImportAsync(
        ImportProductConfigurationRequest request,
        CatalogActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        EnsureConfigurationContract(request.ContractIdentity, request.ContractRevision);

        var canonicalDocument = CatalogCanonicalJson.SerializeConfiguration(request.Configuration);
        var computedDigest = CatalogCanonicalJson.ComputeSha256(canonicalDocument);
        var expectedDigest = NormalizeDigest(request.ExpectedContentDigest);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(computedDigest),
                Convert.FromHexString(expectedDigest)))
        {
            throw new CatalogContractException(
                "catalog.configuration_digest_mismatch",
                $"Configuration digest mismatch. Expected '{expectedDigest}', computed '{computedDigest}'.");
        }

        var configuration = CatalogContractMapper.ToDomain(request.Configuration, computedDigest);
        var importedAtUtc = timeProvider.GetUtcNow();
        await repository.AddConfigurationAsync(
            configuration,
            canonicalDocument,
            actor.Id,
            importedAtUtc,
            cancellationToken);

        return new ProductConfigurationRevisionResponse(
            configuration.RevisionId,
            configuration.Site.Key.Value,
            configuration.Catalog.Key.Value,
            configuration.Digest,
            configuration.CreatedAtUtc,
            importedAtUtc,
            IsActive: false);
    }

    /// <summary>Starts a new correlation root for a direct application or operator activation command.</summary>
    public Task<ProductConfigurationRevisionResponse> ActivateAsync(
        string catalogKeyValue,
        ActivateProductConfigurationRequest request,
        CatalogActor actor,
        CancellationToken cancellationToken) =>
        ActivateAsync(
            catalogKeyValue,
            request,
            actor,
            CatalogEventContext.StartRoot(),
            cancellationToken);

    public async Task<ProductConfigurationRevisionResponse> ActivateAsync(
        string catalogKeyValue,
        ActivateProductConfigurationRequest request,
        CatalogActor actor,
        CatalogEventContext eventContext,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogKeyValue);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(eventContext);
        var catalogKey = CatalogKey.Create(catalogKeyValue);

        var configuration = await repository.GetConfigurationAsync(
                request.TargetConfigurationRevisionId,
                cancellationToken)
            ?? throw new CatalogNotFoundException(
                "product-configuration-revision",
                request.TargetConfigurationRevisionId);

        if (configuration.Catalog.Key != catalogKey)
        {
            throw new CatalogConflictException(
                $"Configuration '{configuration.RevisionId}' belongs to catalog '{configuration.Catalog.Key}', not '{catalogKey}'.");
        }

        var expectedCurrentRevisionId = ToInternalExpectation(request.ExpectedCurrent);
        var activatedAtUtc = timeProvider.GetUtcNow();
        var eventId = idSource.CreateId();
        var supportedListingKinds = configuration.Catalog.AllowedListingKinds
            .OrderBy(kind => kind)
            .Select(ToContractListingKind)
            .ToArray();

        CatalogOutboxMessage CreateOutbox(
            Guid? previousConfigurationRevisionId,
            long aggregateRevision)
        {
            var integrationEvent = new CatalogConfigurationActivated(
                eventId,
                configuration.Site.Key.Value,
                configuration.Catalog.Key.Value,
                configuration.RevisionId,
                previousConfigurationRevisionId,
                configuration.Digest,
                configuration.Catalog.MarketAreaKey,
                supportedListingKinds,
                aggregateRevision,
                activatedAtUtc);
            return CatalogOutboxMessageFactory.Create(
                integrationEvent.EventId,
                CatalogIntegrationEventTypes.ConfigurationActivated,
                CatalogIntegrationEventContracts.ConfigurationActivated,
                integrationEvent,
                activatedAtUtc,
                eventContext);
        }

        await repository.ActivateConfigurationAsync(
            catalogKey,
            configuration.RevisionId,
            expectedCurrentRevisionId,
            actor.Id,
            activatedAtUtc,
            CreateOutbox,
            cancellationToken);

        return new ProductConfigurationRevisionResponse(
            configuration.RevisionId,
            configuration.Site.Key.Value,
            configuration.Catalog.Key.Value,
            configuration.Digest,
            configuration.CreatedAtUtc,
            activatedAtUtc,
            IsActive: true);
    }

    private static SubjectKindContract ToContractListingKind(SubjectKind kind)
    {
        var contract = (SubjectKindContract)(int)kind;
        if (!Enum.IsDefined(contract) || contract == SubjectKindContract.Organization)
        {
            throw new CatalogContractException(
                "catalog.configuration_listing_kind_invalid",
                $"Catalog listing kind '{kind}' has no supported wire identity.");
        }

        return contract;
    }

    private static Guid ToInternalExpectation(ConfigurationPointerExpectationContract expectation)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        return expectation.Kind switch
        {
            PointerExpectationKindContract.Absent when expectation.ConfigurationRevisionId is null => Guid.Empty,
            PointerExpectationKindContract.Exact when expectation.ConfigurationRevisionId is { } revisionId && revisionId != Guid.Empty => revisionId,
            _ => throw new CatalogContractException(
                "catalog.configuration_pointer_expectation_invalid",
                "Configuration pointer expectation must be either explicit absence or an exact non-empty revision ID."),
        };
    }

    private static void EnsureConfigurationContract(string identity, int revision)
    {
        if (!string.Equals(identity, CatalogContractIdentity.ProductConfiguration, StringComparison.Ordinal))
        {
            throw new CatalogContractException(
                "catalog.configuration_contract_unknown",
                $"Unsupported configuration contract identity '{identity}'.");
        }

        if (revision != CatalogContractIdentity.ProductConfigurationRevision)
        {
            throw new CatalogContractException(
                "catalog.configuration_contract_revision_unknown",
                $"Unsupported configuration contract revision '{revision}'.");
        }
    }

    private static string NormalizeDigest(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new CatalogContractException(
                "catalog.configuration_digest_invalid",
                "Expected configuration digest must be a SHA-256 hexadecimal value.");
        }

        return normalized;
    }
}
