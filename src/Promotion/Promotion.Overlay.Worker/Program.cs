using Aggregator.Promotion.Overlay.Infrastructure;
using Aggregator.Promotion.Overlay.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Observability;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Promotion");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:Promotion is required.");
}

var section = PromotionOverlayWorkerOptions.SectionName;
var brokerUriText = builder.Configuration[$"{section}:BrokerUri"];
if (string.IsNullOrWhiteSpace(brokerUriText))
{
    throw new InvalidOperationException($"{section}:BrokerUri is required.");
}

var options = new PromotionOverlayWorkerOptions
{
    BrokerUri = new Uri(brokerUriText, UriKind.Absolute),
    Exchange = builder.Configuration[$"{section}:Exchange"] ?? "aggregator.events",
    WorkerId = builder.Configuration[$"{section}:WorkerId"] ?? "promotion-overlay-outbox",
    PollInterval = ParseTimeSpan(
        builder.Configuration[$"{section}:PollInterval"],
        TimeSpan.FromSeconds(1),
        $"{section}:PollInterval"),
    LeaseDuration = ParseTimeSpan(
        builder.Configuration[$"{section}:LeaseDuration"],
        TimeSpan.FromSeconds(30),
        $"{section}:LeaseDuration"),
    MaximumAttempts = ParseInteger(
        builder.Configuration[$"{section}:MaximumAttempts"],
        10,
        $"{section}:MaximumAttempts"),
};
options.Validate();

builder.Services.AddPromotionOverlayInfrastructure(connectionString);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddPlatformObservability(builder.Configuration, "promotion-overlay-worker");
builder.Services.AddHostedService<PromotionOverlayOutboxWorker>();

await builder.Build().RunAsync();

static TimeSpan ParseTimeSpan(string? value, TimeSpan defaultValue, string path) =>
    value is null
        ? defaultValue
        : TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Configuration value '{path}' must be a TimeSpan.");

static int ParseInteger(string? value, int defaultValue, string path) =>
    value is null
        ? defaultValue
        : int.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Configuration value '{path}' must be an integer.");
