using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Catalog.Contracts;
using Aggregator.Ingestion.Contracts;

namespace Ingestion.Processing.Tests;

public sealed class IngestionValueKindContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [Theory]
    [InlineData(IngestionCandidateFieldValueKindContract.Integer, "integer")]
    [InlineData(IngestionCandidateFieldValueKindContract.Decimal, "decimal")]
    public void IngestionValueKindsKeepCanonicalStringTokens(
        IngestionCandidateFieldValueKindContract value,
        string expectedToken)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);

        Assert.Equal($"\"{expectedToken}\"", json);
    }

    [Theory]
    [InlineData(CatalogDraftValueKindContract.Integer, "integer")]
    [InlineData(CatalogDraftValueKindContract.Decimal, "decimal")]
    public void CatalogDraftValueKindsKeepCanonicalStringTokens(
        CatalogDraftValueKindContract value,
        string expectedToken)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);

        Assert.Equal($"\"{expectedToken}\"", json);
    }

    [Fact]
    public void NumericEnumTokensAreRejectedAtTheWireBoundary()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<IngestionCandidateFieldValueKindContract>("2", JsonOptions));
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<CatalogDraftValueKindContract>("2", JsonOptions));
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }
}
