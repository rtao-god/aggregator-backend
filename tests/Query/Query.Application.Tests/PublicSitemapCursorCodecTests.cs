using Aggregator.Query.Application;
using Aggregator.Query.Domain;

namespace Query.Application.Tests;

public sealed class PublicSitemapCursorCodecTests
{
    private static readonly Guid PublicReadRevisionId =
        Guid.Parse("01990f10-0000-7000-8000-000000000001");

    [Fact]
    public void ExactCursorRoundTripsEveryPaginationScopeField()
    {
        var cursor = new PublicSitemapCursor(
            PublicReadRevisionId,
            QuerySeoCatalogKey.Create("recording-services"),
            QuerySeoLocale.Create("de-DE"),
            QuerySeoLocale.Create("de-DE"),
            QuerySeoPath.CreateIndexable("/de-DE/studios/exact-studio"));

        var decoded = PublicSitemapCursorCodec.Decode(
            PublicSitemapCursorCodec.Encode(cursor));

        Assert.Equal(PublicReadRevisionId, decoded.PublicReadRevisionId);
        Assert.Equal("recording-services", decoded.CatalogKey.Value);
        Assert.Equal("de-DE", decoded.RequestedLocale!.Value);
        Assert.Equal("de-DE", decoded.LastLocale.Value);
        Assert.Equal("/de-DE/studios/exact-studio", decoded.LastPath.Value);
    }

    [Fact]
    public void CursorCannotContinueUnderAnotherLocaleScope()
    {
        var cursor = new PublicSitemapCursor(
            PublicReadRevisionId,
            QuerySeoCatalogKey.Create("recording-services"),
            QuerySeoLocale.Create("de-DE"),
            QuerySeoLocale.Create("de-DE"),
            QuerySeoPath.CreateIndexable("/de-DE/studios/exact-studio"));

        var exception = Assert.Throws<ArgumentException>(() =>
            PublicSitemapCursorCodec.EnsureScope(
                cursor,
                QuerySeoCatalogKey.Create("recording-services"),
                QuerySeoLocale.Create("en-GB")));

        Assert.Equal("cursor", exception.ParamName);
    }

    [Fact]
    public void UnknownJsonMembersAreRejected()
    {
        const string json = """
            {
              "publicReadRevisionId":"01990f10-0000-7000-8000-000000000001",
              "catalogKey":"recording-services",
              "locale":"de-DE",
              "lastLocale":"de-DE",
              "lastPath":"/de-DE/studios/exact-studio",
              "unownedField":true
            }
            """;
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        var exception = Assert.Throws<ArgumentException>(() =>
            PublicSitemapCursorCodec.Decode(encoded));

        Assert.Equal("cursor", exception.ParamName);
    }

    [Fact]
    public void CursorCannotUseFilterUrlAsLastRoute()
    {
        const string json = """
            {
              "publicReadRevisionId":"01990f10-0000-7000-8000-000000000001",
              "catalogKey":"recording-services",
              "locale":"de-DE",
              "lastLocale":"de-DE",
              "lastPath":"/de-DE/studios?district=mitte"
            }
            """;
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        var exception = Assert.Throws<ArgumentException>(() =>
            PublicSitemapCursorCodec.Decode(encoded));

        Assert.Equal("cursor", exception.ParamName);
    }
}
