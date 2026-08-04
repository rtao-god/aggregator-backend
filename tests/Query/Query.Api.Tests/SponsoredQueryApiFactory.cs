using Aggregator.Promotion.Contracts;
using Aggregator.Query.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Query.Api.Tests;

public sealed class SponsoredQueryApiFactory : WebApplicationFactory<Program>
{
    private const string ConnectionSetting = "ConnectionStrings__Query";
    private readonly string? _originalConnectionString;

    public SponsoredQueryApiFactory()
    {
        _originalConnectionString = Environment.GetEnvironmentVariable(ConnectionSetting);
        Environment.SetEnvironmentVariable(
            ConnectionSetting,
            "Host=127.0.0.1;Port=1;Database=query;Username=test;Password=test;Timeout=1;Command Timeout=1");
    }

    public RecordingSponsoredStore Store { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPublicSponsoredListingStore>();
            services.AddSingleton<IPublicSponsoredListingStore>(Store);
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Environment.SetEnvironmentVariable(ConnectionSetting, _originalConnectionString);
        }

        base.Dispose(disposing);
    }
}

public sealed class RecordingSponsoredStore : IPublicSponsoredListingStore
{
    public SponsoredListingSearchResponse? Response { get; set; }

    public int ReadCount { get; private set; }

    public Task<SponsoredListingSearchResponse?> ReadAsync(
        string catalogKey,
        Guid sourcePublicReadRevisionId,
        string locale,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        if (sourcePublicReadRevisionId == Guid.Empty)
        {
            throw new ArgumentException(
                "Source public read revision ID is required.",
                nameof(sourcePublicReadRevisionId));
        }

        ReadCount++;
        return Task.FromResult(Response);
    }
}
