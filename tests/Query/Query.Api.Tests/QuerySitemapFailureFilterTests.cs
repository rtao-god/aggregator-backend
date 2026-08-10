using Aggregator.Query.Api;
using Aggregator.Query.Application;
using Aggregator.Query.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

namespace Query.Api.Tests;

public sealed class QuerySitemapFailureFilterTests
{
    [Fact]
    public void DomainFailureBecomesTypedBadRequest()
    {
        var context = CreateContext(new QueryDomainException(
            "QUERY_SEO_LOCALE_INVALID",
            "Locale is invalid."));

        new QuerySitemapFailureFilterAttribute().OnException(context);

        Assert.True(context.ExceptionHandled);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal("QUERY_SEO_LOCALE_INVALID", problem.Extensions["code"]);
    }

    [Fact]
    public void ProjectionCorruptionBecomesTypedServiceUnavailable()
    {
        var context = CreateContext(new QuerySitemapProjectionException(
            "QUERY_SITEMAP_PERSISTED_STATE_INVALID",
            "Persisted state is invalid.",
            "Rebuild the exact revision."));

        new QuerySitemapFailureFilterAttribute().OnException(context);

        Assert.True(context.ExceptionHandled);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal("Query.SitemapProjection", problem.Extensions["owner"]);
        Assert.Equal("QUERY_SITEMAP_PERSISTED_STATE_INVALID", problem.Extensions["code"]);
    }

    [Fact]
    public void UnknownFailureRemainsUnhandledForGlobalMiddleware()
    {
        var context = CreateContext(new InvalidOperationException("Unknown host defect."));

        new QuerySitemapFailureFilterAttribute().OnException(context);

        Assert.False(context.ExceptionHandled);
        Assert.Null(context.Result);
    }

    private static ExceptionContext CreateContext(Exception exception)
    {
        var httpContext = new DefaultHttpContext();
        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor());
        return new ExceptionContext(actionContext, Array.Empty<IFilterMetadata>())
        {
            Exception = exception,
        };
    }
}
