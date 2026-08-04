using System.Security.Cryptography;
using System.Text;
using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;

namespace Catalog.Application.Tests;

public sealed class CatalogOutboxMessageFactoryTests
{
    [Fact]
    public void CanonicalPayloadCarriesExactCorrelationAndDigest()
    {
        var messageId = Guid.Parse("0192f5f0-0000-7000-8000-000000000101");
        var causationId = Guid.Parse("0192f5f0-0000-7000-8000-000000000102");
        var occurredAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var payload = new CatalogPublicationActivated(
            messageId,
            Guid.Parse("0192f5f0-0000-7000-8000-000000000103"),
            "catalog",
            Guid.Parse("0192f5f0-0000-7000-8000-000000000104"),
            PublicationSequence: 7,
            "catalog/catalog/publications/publication.json",
            new string('a', 64),
            PublicationActivationKindContract.Publication,
            PreviousPublicationId: null,
            occurredAtUtc);
        var context = CatalogEventContext.Create("corr.catalog-test:0001", causationId);

        var message = CatalogOutboxMessageFactory.Create(
            messageId,
            CatalogIntegrationEventTypes.PublicationActivated,
            CatalogIntegrationEventContracts.PublicationActivated,
            payload,
            occurredAtUtc,
            context);

        Assert.Equal(context.CorrelationId, message.CorrelationId);
        Assert.Equal(causationId, message.CausationId);
        Assert.Equal(CatalogIntegrationEventContracts.PublicationActivated, message.ContractIdentity);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message.Payload))).ToLowerInvariant(),
            message.PayloadDigest);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("correlation id with spaces")]
    public void InvalidCorrelationIdentityIsRejected(string correlationId)
    {
        var exception = Assert.Throws<CatalogContractException>(() =>
            CatalogEventContext.Create(correlationId));

        Assert.Equal("catalog.correlation_id_invalid", exception.Code);
    }
}
