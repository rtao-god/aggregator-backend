using System.Net;

namespace Catalog.Api.Tests;

public sealed class CatalogCorrelationContractTests(CatalogApiFactory factory) : IClassFixture<CatalogApiFactory>
{
    [Fact]
    public async Task AcceptedCorrelationIdentityIsEchoedWithoutReplacement()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Correlation-Id", "corr.catalog-api:0001");

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Correlation-Id", out var values));
        Assert.Equal("corr.catalog-api:0001", Assert.Single(values));
    }
}
