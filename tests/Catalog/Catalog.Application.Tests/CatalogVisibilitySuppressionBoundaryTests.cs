using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Catalog.Application.Tests;

public sealed class CatalogVisibilitySuppressionBoundaryTests
{
    [Theory]
    [InlineData(PublicVisibilitySuppressionTargetKindContract.Contact)]
    [InlineData(PublicVisibilitySuppressionTargetKindContract.ExternalReference)]
    public async Task UnsupportedChildIdentityIsRejectedBeforePersistence(
        PublicVisibilitySuppressionTargetKindContract targetKind)
    {
        var repository = new RejectUnexpectedRepositoryCall();
        var service = new CatalogVisibilitySuppressionService(
            repository,
            new SingleIdSource(Guid.Parse("0198fd00-0000-7000-8000-000000000001")),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 6, 8, 30, 0, TimeSpan.Zero)));

        var exception = await Assert.ThrowsAsync<CatalogContractException>(() =>
            service.CreateActiveAsync(
                "berlin-recording-services",
                new CreatePublicVisibilitySuppressionRequest(
                    new PublicVisibilitySuppressionTargetContract(
                        targetKind,
                        null,
                        Guid.Parse("0198fd00-0000-7000-8000-000000000002").ToString("D")),
                    "privacy-request",
                    "catalog/privacy/private/request-002",
                    PublicVisibilitySuppressionResponseModeContract.OmitChildElement,
                    null,
                    "Suppress one exact child identity."),
                CatalogActor.Create(Guid.Parse("0198fd00-0000-7000-8000-000000000003")),
                CancellationToken.None));

        Assert.Contains("identity_unsupported", exception.Code, StringComparison.Ordinal);
        Assert.False(repository.Called);
    }

    private sealed class RejectUnexpectedRepositoryCall :
        ICatalogVisibilitySuppressionRepository
    {
        public bool Called { get; private set; }

        public Task EnsureTargetExistsAsync(
            CatalogKey catalogKey,
            PublicVisibilitySuppressionTarget target,
            CancellationToken cancellationToken)
        {
            Called = true;
            throw new InvalidOperationException("Persistence must not receive unsupported target identities.");
        }

        public Task<PublicVisibilitySuppression?> GetAsync(
            Guid suppressionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CreateActiveAsync(
            PublicVisibilitySuppression requested,
            PublicVisibilitySuppression active,
            CatalogOutboxMessage outboxMessage,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ResolveAsync(
            PublicVisibilitySuppression resolved,
            CatalogOutboxMessage outboxMessage,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class SingleIdSource(Guid value) : ICatalogIdSource
    {
        public Guid CreateId() => value;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
