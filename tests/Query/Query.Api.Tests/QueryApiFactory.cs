using Aggregator.Query.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Query.Api.Tests;

public sealed class QueryApiFactory : WebApplicationFactory<Program>
{
    private const string ConnectionStringKey = "ConnectionStrings__Query";
    private readonly string? _originalConnectionString;

    public QueryApiFactory()
    {
        _originalConnectionString = Environment.GetEnvironmentVariable(ConnectionStringKey);
        Environment.SetEnvironmentVariable(
            ConnectionStringKey,
            "Host=127.0.0.1;Port=1;Database=query;Username=test;Password=test;Timeout=1;Command Timeout=1");
    }

    public StubPublicQueryStore Store { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPublicQueryStore>();
            services.AddSingleton<IPublicQueryStore>(Store);
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Environment.SetEnvironmentVariable(ConnectionStringKey, _originalConnectionString);
        }

        base.Dispose(disposing);
    }
}

public sealed class StubPublicQueryStore : IPublicQueryStore
{
    public PublicReadPageSnapshot? Page { get; set; }

    public PublicReadDocumentSnapshot? Document { get; set; }

    public int PageReadCount { get; private set; }

    public int RouteReadCount { get; private set; }

    public Task<PublicReadPageSnapshot?> ReadPageAsync(
        string catalogKey,
        Guid? afterListingId,
        int maximumDocuments,
        string? categoryKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PageReadCount++;
        return Task.FromResult(Page);
    }

    public Task<PublicReadDocumentSnapshot?> ReadByRouteAsync(
        string catalogKey,
        string routePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RouteReadCount++;
        return Task.FromResult(Document);
    }
}
