using Aggregator.Catalog.Domain;

namespace Catalog.Domain.Tests;

public sealed class CatalogContactIdentityTests
{
    [Fact]
    public void ContactIdentityMustBeExplicitAndNonEmpty()
    {
        var exception = Assert.Throws<ArgumentException>(() => ContactValue.Create(
            Guid.Empty,
            ContactKind.Website,
            new Uri("https://example.test"),
            label: null,
            assertionId: Guid.Parse("0198fb10-0000-7000-8000-000000000001")));

        Assert.Equal("id", exception.ParamName);
    }
}
