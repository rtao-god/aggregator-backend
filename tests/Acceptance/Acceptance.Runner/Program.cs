using System.Text.Json;
using Aggregator.Acceptance.Runner;

var options = new AcceptanceOptions
{
    IdentityBaseUrl = RequireUri("Acceptance__IdentityBaseUrl"),
    CollectorBaseUrl = RequireUri("Acceptance__CollectorBaseUrl"),
    CatalogControlBaseUrl = RequireUri("Acceptance__CatalogControlBaseUrl"),
    AnalyticsControlBaseUrl = RequireUri("Acceptance__AnalyticsControlBaseUrl"),
    QueryBaseUrl = RequireUri("Acceptance__QueryBaseUrl"),
    AnalyticsBaseUrl = RequireUri("Acceptance__AnalyticsBaseUrl"),
    PromotionOverlayBaseUrl = RequireUri("Acceptance__PromotionOverlayBaseUrl"),
    AcceptanceKey = RequireSetting("Acceptance__InternalKey"),
    Timeout = ReadTimeSpan("Acceptance__Timeout", TimeSpan.FromMinutes(3)),
};
options.Validate();
var reportPath = Environment.GetEnvironmentVariable("Acceptance__ReportPath")
    ?? "/artifacts/acceptance-report.json";
var startedAtUtc = DateTimeOffset.UtcNow;
using var cancellationSource = new CancellationTokenSource(options.Timeout + TimeSpan.FromSeconds(30));
try
{
    var report = await new AcceptanceScenario(options).RunAsync(cancellationSource.Token);
    var serializerOptions = new JsonSerializerOptions(AcceptanceHttp.SerializerOptions)
    {
        WriteIndented = true,
    };
    var json = JsonSerializer.Serialize(report, serializerOptions);
    var directory = Path.GetDirectoryName(reportPath);
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    await File.WriteAllTextAsync(reportPath, json, cancellationSource.Token);
    Console.WriteLine(json);
}
catch (Exception exception)
{
    var failure = new
    {
        state = "failed",
        startedAtUtc,
        failedAtUtc = DateTimeOffset.UtcNow,
        exception = exception.GetType().FullName,
        exception.Message,
        exception.StackTrace,
    };
    var json = JsonSerializer.Serialize(
        failure,
        new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        });
    var directory = Path.GetDirectoryName(reportPath);
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    await File.WriteAllTextAsync(reportPath, json, CancellationToken.None);
    Console.Error.WriteLine(json);
    Environment.ExitCode = 1;
}

static Uri RequireUri(string name)
{
    var value = RequireSetting(name);
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
        uri.Scheme is not ("http" or "https"))
    {
        throw new InvalidOperationException(
            $"Environment value '{name}' must be an absolute HTTP URI.");
    }

    return uri;
}

static string RequireSetting(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    return !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new InvalidOperationException(
            $"Environment value '{name}' is required.");
}

static TimeSpan ReadTimeSpan(string name, TimeSpan defaultValue)
{
    var value = Environment.GetEnvironmentVariable(name);
    return value is null
        ? defaultValue
        : TimeSpan.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Environment value '{name}' must be a TimeSpan.");
}
