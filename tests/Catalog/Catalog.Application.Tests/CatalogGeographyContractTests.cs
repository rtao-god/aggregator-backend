using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Catalog.Application.Tests;

public sealed class CatalogGeographyContractTests
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    [Theory]
    [InlineData(GeographyStateContract.PrimaryMarket, "\"primaryMarket\"")]
    [InlineData(GeographyStateContract.NearbyMarket, "\"nearbyMarket\"")]
    public void GenericGeographyStateHasStableCanonicalWireToken(
        GeographyStateContract state,
        string expectedJson)
    {
        Assert.Equal(expectedJson, JsonSerializer.Serialize(state, SerializerOptions));
        Assert.Equal(state, JsonSerializer.Deserialize<GeographyStateContract>(expectedJson, SerializerOptions));
    }

    [Theory]
    [InlineData("\"berlinCore\"")]
    [InlineData("\"berlinNearby\"")]
    [InlineData("1")]
    public void ProductSpecificAndNumericGeographyTokensAreRejected(string json)
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<GeographyStateContract>(json, SerializerOptions));
    }

    [Fact]
    public void GenericRenamePreservesStoredNumericIdentity()
    {
        Assert.Equal(1, (int)GeographyStateContract.PrimaryMarket);
        Assert.Equal(2, (int)GeographyStateContract.NearbyMarket);
        Assert.Equal(1, (int)GeographyState.PrimaryMarket);
        Assert.Equal(2, (int)GeographyState.NearbyMarket);
    }

    private static JsonSerializerOptions CreateOptions()
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
}
