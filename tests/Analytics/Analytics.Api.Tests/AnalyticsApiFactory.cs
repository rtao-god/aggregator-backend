using Aggregator.Analytics.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Analytics.Api.Tests;

public sealed class AnalyticsApiFactory : WebApplicationFactory<Program>
{
    private static readonly IReadOnlyDictionary<string, string> RequiredEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConnectionStrings__Analytics"] =
                "Host=127.0.0.1;Port=1;Database=analytics;Username=test;Password=test;Timeout=1;Command Timeout=1",
            ["Analytics__SessionHashKey"] =
                "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=",
            ["Analytics__InternalMetricsKey"] =
                "analytics-api-test-internal-key-0001",
        };

    private readonly Dictionary<string, string?> _originalEnvironment = new(StringComparer.Ordinal);

    public AnalyticsApiFactory()
    {
        foreach (var setting in RequiredEnvironment)
        {
            _originalEnvironment[setting.Key] = Environment.GetEnvironmentVariable(setting.Key);
            Environment.SetEnvironmentVariable(setting.Key, setting.Value);
        }
    }

    public RecordingAnalyticsRuntimeStore Store { get; } = new();

    public const string InternalMetricsKey = "analytics-api-test-internal-key-0001";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAnalyticsRuntimeStore>();
            services.AddSingleton<IAnalyticsRuntimeStore>(Store);
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var setting in _originalEnvironment)
            {
                Environment.SetEnvironmentVariable(setting.Key, setting.Value);
            }
        }

        base.Dispose(disposing);
    }
}

public sealed class RecordingAnalyticsRuntimeStore : IAnalyticsRuntimeStore
{
    public AnalyticsInteractionRecord? Interaction { get; private set; }

    public AnalyticsListingMetricsSnapshot? Metrics { get; set; }

    public bool Ready { get; set; } = true;

    public Task<AnalyticsInteractionRegistration> RegisterAsync(
        AnalyticsInteractionRecord interaction,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        return Task.FromResult(
            new AnalyticsInteractionRegistration(interaction.RecordedAtUtc, Replayed: false));
    }

    public Task<AnalyticsListingMetricsSnapshot?> ReadListingMetricsAsync(
        string catalogKey,
        Guid listingId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogKey);
        if (listingId == Guid.Empty)
        {
            throw new ArgumentException("Listing ID is required.", nameof(listingId));
        }

        return Task.FromResult(Metrics);
    }

    public Task<bool> CheckReadinessAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Ready);
    }
}
