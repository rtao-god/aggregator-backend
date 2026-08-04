using System.Text.Json;
using Aggregator.Catalog.Domain;

namespace Catalog.Domain.Tests;

public sealed class AttributeValueTests
{
    [Fact]
    public void UnknownAndObservedFalseAreDifferentDomainStates()
    {
        var unknown = ListingAttributeValue.Create(
            "parking",
            AttributeDataType.Boolean,
            AttributeValueState.Unknown,
            null);
        using var document = JsonDocument.Parse("false");
        var observedFalse = ListingAttributeValue.Create(
            "parking",
            AttributeDataType.Boolean,
            AttributeValueState.Observed,
            document.RootElement);

        Assert.Equal(AttributeValueState.Unknown, unknown.State);
        Assert.Null(unknown.Value);
        Assert.Equal(AttributeValueState.Observed, observedFalse.State);
        Assert.False(observedFalse.Value!.Value.GetBoolean());
    }

    [Fact]
    public void MissingStateCannotHideAValue()
    {
        using var document = JsonDocument.Parse("true");

        var exception = Assert.Throws<CatalogDomainException>(() =>
            ListingAttributeValue.Create(
                "parking",
                AttributeDataType.Boolean,
                AttributeValueState.Unknown,
                document.RootElement));

        Assert.Equal("ATTRIBUTE_MISSING_STATE_HAS_VALUE", exception.Code);
    }

    [Fact]
    public void MoneyRequiresAmountAndUppercaseCurrency()
    {
        using var invalid = JsonDocument.Parse("{\"amount\":15.5,\"currency\":\"eur\"}");

        var exception = Assert.Throws<CatalogDomainException>(() =>
            ListingAttributeValue.Create(
                "hourly-price",
                AttributeDataType.Money,
                AttributeValueState.Observed,
                invalid.RootElement));

        Assert.Equal("ATTRIBUTE_VALUE_TYPE_MISMATCH", exception.Code);
    }
}
