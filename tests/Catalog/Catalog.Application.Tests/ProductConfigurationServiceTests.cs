using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Catalog.Application.Tests;

public sealed class ProductConfigurationServiceTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SemanticListOrderDoesNotChangeContentDigest()
    {
        var request = CreateRequest();
        var reordered = request with
        {
            Site = request.Site with
            {
                SupportedLocales = request.Site.SupportedLocales.Reverse().ToArray(),
                HostMappings = request.Site.HostMappings.Reverse().ToArray(),
            },
            Categories = request.Categories.Reverse().ToArray(),
            Attributes = request.Attributes.Reverse().ToArray(),
            CategoryAttributes = request.CategoryAttributes.Reverse().ToArray(),
        };
        var firstService = new ProductConfigurationService(
            new InMemoryProductConfigurationStore(),
            new FixedTimeProvider(Timestamp));
        var secondService = new ProductConfigurationService(
            new InMemoryProductConfigurationStore(),
            new FixedTimeProvider(Timestamp));
        var actor = ActorId.New();

        var first = await firstService.ImportAsync(request, actor, "first", CancellationToken.None);
        var second = await secondService.ImportAsync(reordered, actor, "second", CancellationToken.None);

        Assert.Equal(first.ContentDigest, second.ContentDigest);
    }

    [Fact]
    public async Task ActivationRequiresRevisionOwnedByTargetCatalog()
    {
        var store = new InMemoryProductConfigurationStore();
        var service = new ProductConfigurationService(store, new FixedTimeProvider(Timestamp));
        var actor = ActorId.New();
        var imported = await service.ImportAsync(CreateRequest(), actor, "import", CancellationToken.None);

        var exception = await Assert.ThrowsAsync<CatalogCommandException>(() =>
            service.ActivateAsync(
                CatalogId.New(),
                new ProductConfigurationRevisionId(imported.RevisionId),
                null,
                "activate",
                actor,
                "activation",
                CancellationToken.None));

        Assert.Equal("PRODUCT_CONFIGURATION_CATALOG_MISMATCH", exception.Code);
    }

    internal static ImportProductConfigurationRequest CreateRequest()
    {
        var catalogId = Guid.CreateVersion7();
        var taxonomyRevisionId = Guid.CreateVersion7();
        var attributeRevisionId = Guid.CreateVersion7();
        var marketAreaRevisionId = Guid.CreateVersion7();
        return new ImportProductConfigurationRequest(
            "test-product",
            "commit-0123456789",
            new SiteConfigurationDto(
                "test-site",
                "de-DE",
                ["de-DE", "en-GB"],
                "EUR",
                "Europe/Berlin",
                "test-brand",
                ["catalog.example.test", "www.catalog.example.test"],
                new Dictionary<string, string>
                {
                    ["privacy"] = "/privacy",
                    ["imprint"] = "/imprint",
                }),
            new CatalogConfigurationDto(
                catalogId,
                "test-catalog",
                "test-site",
                new Dictionary<string, string>
                {
                    ["de-DE"] = "Testkatalog",
                    ["en-GB"] = "Test catalog",
                },
                "test-market",
                taxonomyRevisionId,
                attributeRevisionId,
                marketAreaRevisionId,
                "EUR",
                "Europe/Berlin",
                [ListingKindContract.Place, ListingKindContract.Provider],
                "default-seo",
                "default-publication",
                "default-contact",
                "default-claim",
                "default-promotion"),
            [
                new CategoryDefinitionDto(
                    "podcast-studio",
                    "recording-studio",
                    new Dictionary<string, string>
                    {
                        ["de-DE"] = "Podcaststudio",
                        ["en-GB"] = "Podcast studio",
                    },
                    new Dictionary<string, string>
                    {
                        ["de-DE"] = "podcaststudio",
                        ["en-GB"] = "podcast-studio",
                    },
                    [ListingKindContract.Place],
                    true,
                    true,
                    2),
                new CategoryDefinitionDto(
                    "recording-studio",
                    null,
                    new Dictionary<string, string>
                    {
                        ["de-DE"] = "Tonstudio",
                        ["en-GB"] = "Recording studio",
                    },
                    new Dictionary<string, string>
                    {
                        ["de-DE"] = "tonstudio",
                        ["en-GB"] = "recording-studio",
                    },
                    [ListingKindContract.Place],
                    true,
                    true,
                    1),
            ],
            [
                new AttributeDefinitionDto(
                    "parking",
                    AttributeDataTypeContract.Boolean,
                    false,
                    true,
                    false,
                    false,
                    true,
                    Array.Empty<string>(),
                    new Dictionary<string, string>
                    {
                        ["de-DE"] = "Parkplatz",
                        ["en-GB"] = "Parking",
                    }),
                new AttributeDefinitionDto(
                    "room-kind",
                    AttributeDataTypeContract.SingleOption,
                    false,
                    true,
                    true,
                    false,
                    true,
                    ["live-room", "vocal-booth"],
                    new Dictionary<string, string>
                    {
                        ["de-DE"] = "Raumart",
                        ["en-GB"] = "Room kind",
                    }),
            ],
            [
                new CategoryAttributeDefinitionDto(
                    "recording-studio",
                    "parking",
                    false,
                    false,
                    true,
                    false,
                    true,
                    [ListingKindContract.Place],
                    "access",
                    1),
                new CategoryAttributeDefinitionDto(
                    "recording-studio",
                    "room-kind",
                    true,
                    true,
                    true,
                    true,
                    true,
                    [ListingKindContract.Place],
                    "rooms",
                    2),
            ]);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _timestamp;

        public FixedTimeProvider(DateTimeOffset timestamp)
        {
            _timestamp = timestamp;
        }

        public override DateTimeOffset GetUtcNow() => _timestamp;
    }

    private sealed class InMemoryProductConfigurationStore : IProductConfigurationStore
    {
        private readonly Dictionary<ProductConfigurationRevisionId, ProductConfigurationRevisionEnvelope> _revisions = [];
        private readonly Dictionary<CatalogId, ProductConfigurationRevisionId> _active = [];
        private readonly Dictionary<(string Scope, string Key), (string Digest, object Value)> _commands = [];

        public Task<CommandPersistenceResult<ProductConfigurationRevisionEnvelope>> SaveRevisionAsync(
            ProductConfigurationRevisionEnvelope revision,
            CatalogCommandIdentity command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = Execute(command, revision);
            if (!result.Replayed)
            {
                _revisions.Add(revision.Revision.Id, revision);
            }

            return Task.FromResult(result);
        }

        public Task<ProductConfigurationRevisionEnvelope?> GetRevisionAsync(
            ProductConfigurationRevisionId revisionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _revisions.TryGetValue(revisionId, out var revision);
            return Task.FromResult(revision);
        }

        public Task<ProductConfigurationRevisionEnvelope?> GetActiveRevisionAsync(
            CatalogId catalogId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_active.TryGetValue(catalogId, out var revisionId) || !_revisions.TryGetValue(revisionId, out var revision))
            {
                return Task.FromResult<ProductConfigurationRevisionEnvelope?>(null);
            }

            return Task.FromResult<ProductConfigurationRevisionEnvelope?>(revision with { Active = true });
        }

        public Task<CommandPersistenceResult<ProductConfigurationActivationRecord>> ActivateAsync(
            CatalogId catalogId,
            ProductConfigurationRevisionId revisionId,
            ProductConfigurationRevisionId? expectedActiveRevisionId,
            ActorId actorId,
            DateTimeOffset activatedAtUtc,
            string reason,
            CatalogCommandIdentity command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _active.TryGetValue(catalogId, out var current);
            ProductConfigurationRevisionId? previous = current == default ? null : current;
            if (previous != expectedActiveRevisionId)
            {
                throw new CatalogCommandException(
                    "Catalog.ProductConfiguration",
                    "PRODUCT_CONFIGURATION_POINTER_CONFLICT",
                    409,
                    "The active configuration pointer changed.",
                    "Reload the active pointer.");
            }

            var activation = new ProductConfigurationActivationRecord(
                catalogId,
                revisionId,
                previous,
                actorId,
                activatedAtUtc,
                reason);
            var result = Execute(command, activation);
            if (!result.Replayed)
            {
                _active[catalogId] = revisionId;
            }

            return Task.FromResult(result);
        }

        private CommandPersistenceResult<T> Execute<T>(CatalogCommandIdentity command, T value)
        {
            var key = (command.Scope, command.Key);
            if (_commands.TryGetValue(key, out var existing))
            {
                if (!string.Equals(existing.Digest, command.RequestDigest, StringComparison.Ordinal))
                {
                    throw new CatalogCommandException(
                        "Catalog.Commands",
                        "IDEMPOTENCY_DIGEST_CONFLICT",
                        409,
                        "The idempotency key was already used for different content.",
                        "Use the original content or a new idempotency key.");
                }

                return new CommandPersistenceResult<T>((T)existing.Value, Replayed: true);
            }

            _commands.Add(key, (command.RequestDigest, value!));
            return new CommandPersistenceResult<T>(value, Replayed: false);
        }
    }
}
