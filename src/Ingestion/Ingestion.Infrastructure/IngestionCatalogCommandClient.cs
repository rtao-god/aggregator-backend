using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Catalog.Contracts;
using Aggregator.Ingestion.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aggregator.Ingestion.Infrastructure;

/// <summary>Authenticated HTTP adapter for the producer-owned Catalog ingestion command contract.</summary>
public sealed class IngestionCatalogCommandClient(
    IHttpClientFactory httpClientFactory,
    IngestionCatalogAccessTokenProvider tokenProvider,
    TimeProvider timeProvider) : IIngestionCatalogCommandClient
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public async Task<CatalogIngestionCommandOutcome> SendAsync(
        CatalogIngestionUpsertDraftCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommand(command);
        var payload = JsonSerializer.SerializeToUtf8Bytes(command, SerializerOptions);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "api/catalog-command/ingestion/drafts");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await tokenProvider.GetAsync(cancellationToken));
        request.Headers.TryAddWithoutValidation("Idempotency-Key", command.CommandId.ToString("D"));
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", command.CorrelationId);
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = Encoding.UTF8.WebName,
        };

        var client = httpClientFactory.CreateClient(IngestionCatalogCommandClientOptions.CommandClientName);
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var responseBytes = await ReadBoundedAsync(response.Content, MaximumResponseBytes, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw IngestionCatalogCommandTransportException.FromResponse(
                "INGESTION_CATALOG_COMMAND_REJECTED",
                response.StatusCode,
                responseBytes,
                response.Headers.RetryAfter,
                RequireUtc(timeProvider.GetUtcNow()));
        }

        CatalogIngestionCommandOutcome outcome;
        try
        {
            outcome = JsonSerializer.Deserialize<CatalogIngestionCommandOutcome>(
                responseBytes,
                SerializerOptions)
                ?? throw new JsonException("Catalog returned an empty ingestion outcome.");
        }
        catch (JsonException exception)
        {
            throw new IngestionCatalogCommandTransportException(
                "INGESTION_CATALOG_OUTCOME_JSON_INVALID",
                HttpStatusCode.BadGateway,
                "Catalog returned an invalid ingestion outcome document.",
                retryAfter: null,
                innerException: exception);
        }

        if (outcome.CommandId != command.CommandId ||
            outcome.IngestionBatchId != command.IngestionBatchId ||
            !string.Equals(outcome.IngestionItemKey, command.IngestionItemKey, StringComparison.Ordinal))
        {
            throw new IngestionCatalogCommandTransportException(
                "INGESTION_CATALOG_OUTCOME_IDENTITY_MISMATCH",
                HttpStatusCode.BadGateway,
                "Catalog returned an outcome for a different command identity.");
        }

        return outcome;
    }

    private static void ValidateCommand(CatalogIngestionUpsertDraftCommand command)
    {
        if (command.CommandId == Guid.Empty ||
            command.IngestionBatchId == Guid.Empty ||
            string.IsNullOrWhiteSpace(command.IngestionItemKey) ||
            string.IsNullOrWhiteSpace(command.CorrelationId) ||
            command.CorrelationId.Length > 128 ||
            command.CorrelationId.Any(char.IsControl) ||
            !string.Equals(
                CatalogIngestionCommandDigest.Compute(command),
                command.CommandDigest,
                StringComparison.Ordinal))
        {
            throw new IngestionApplicationException(
                "Ingestion.Delivery",
                "INGESTION_CATALOG_COMMAND_INVALID",
                500,
                "The persisted Catalog command failed its canonical identity contract.",
                "Restore the delivery from a verified Ingestion database backup.");
        }
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new IngestionApplicationException(
                "Ingestion.Delivery",
                "INGESTION_CATALOG_COMMAND_CLOCK_NOT_UTC",
                500,
                "The Catalog command clock returned a non-UTC timestamp.",
                "Correct the Ingestion worker clock configuration.");
        }

        return value;
    }

    internal static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Headers.ContentLength is { } length && length > maximumBytes)
        {
            throw new IngestionCatalogCommandTransportException(
                "INGESTION_CATALOG_RESPONSE_TOO_LARGE",
                HttpStatusCode.BadGateway,
                $"Catalog response exceeded the {maximumBytes}-byte limit.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream(Math.Min(maximumBytes, 16 * 1024));
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > maximumBytes)
            {
                throw new IngestionCatalogCommandTransportException(
                    "INGESTION_CATALOG_RESPONSE_TOO_LARGE",
                    HttpStatusCode.BadGateway,
                    $"Catalog response exceeded the {maximumBytes}-byte limit.");
            }

            buffer.Write(chunk, 0, read);
        }
    }

    internal static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }
}

/// <summary>OAuth client-credentials token cache for the Catalog workload audience.</summary>
public sealed class IngestionCatalogAccessTokenProvider(
    IHttpClientFactory httpClientFactory,
    IngestionCatalogCommandClientOptions options,
    TimeProvider timeProvider)
{
    private const int MaximumTokenResponseBytes = 64 * 1024;
    private static readonly JsonSerializerOptions TokenSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _expiresAtUtc;

    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = RequireUtc(timeProvider.GetUtcNow());
            if (_accessToken is not null && now.Add(options.RefreshSkew) < _expiresAtUtc)
            {
                return _accessToken;
            }

            var client = httpClientFactory.CreateClient(IngestionCatalogCommandClientOptions.TokenClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, options.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = options.ClientId,
                    ["client_secret"] = options.ClientSecret,
                    ["scope"] = options.Scope,
                }),
            };
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var responseBytes = await IngestionCatalogCommandClient.ReadBoundedAsync(
                response.Content,
                MaximumTokenResponseBytes,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw IngestionCatalogCommandTransportException.FromResponse(
                    "INGESTION_CATALOG_TOKEN_REQUEST_FAILED",
                    response.StatusCode,
                    responseBytes,
                    response.Headers.RetryAfter,
                    now);
            }

            OAuthTokenResponse token;
            try
            {
                token = JsonSerializer.Deserialize<OAuthTokenResponse>(
                    responseBytes,
                    TokenSerializerOptions)
                    ?? throw new JsonException("Token endpoint returned an empty response.");
            }
            catch (JsonException exception)
            {
                throw new IngestionCatalogCommandTransportException(
                    "INGESTION_CATALOG_TOKEN_JSON_INVALID",
                    HttpStatusCode.BadGateway,
                    "The OAuth token endpoint returned an invalid response document.",
                    retryAfter: null,
                    innerException: exception);
            }

            if (string.IsNullOrWhiteSpace(token.AccessToken) ||
                !string.Equals(token.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase) ||
                token.ExpiresIn <= options.RefreshSkew.TotalSeconds + 5 ||
                token.ExpiresIn > 86_400)
            {
                throw new IngestionCatalogCommandTransportException(
                    "INGESTION_CATALOG_TOKEN_CONTRACT_INVALID",
                    HttpStatusCode.BadGateway,
                    "The OAuth token response violates the Catalog workload token contract.");
            }

            _accessToken = token.AccessToken;
            _expiresAtUtc = now.AddSeconds(token.ExpiresIn);
            return _accessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new IngestionApplicationException(
                "Ingestion.Delivery",
                "INGESTION_CATALOG_TOKEN_CLOCK_NOT_UTC",
                500,
                "The workload token clock returned a non-UTC timestamp.",
                "Correct the Ingestion worker clock configuration.");
        }

        return value;
    }

    private sealed class OAuthTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }
    }
}

/// <summary>Bounded delivery failure classifier shared by the Catalog HTTP adapter and store.</summary>
public sealed class IngestionCatalogDeliveryFailureClassifier : IIngestionCatalogDeliveryFailureClassifier
{
    public IngestionCatalogDeliveryFailureDecision Classify(
        Exception exception,
        int attempt,
        int maximumAttempts,
        DateTimeOffset failedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumAttempts);
        if (failedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Failure timestamp must be UTC.", nameof(failedAtUtc));
        }

        var transport = Find<IngestionCatalogCommandTransportException>(exception);
        var transient = transport?.IsTransient == true || IsTransientInfrastructure(exception);
        if (transient && attempt < maximumAttempts)
        {
            var delay = ComputeRetryDelay(attempt);
            if (transport?.RetryAfter is { } retryAfter && retryAfter > delay)
            {
                delay = retryAfter;
            }

            return new IngestionCatalogDeliveryFailureDecision(
                true,
                failedAtUtc.Add(delay),
                transport?.Code ?? "INGESTION_CATALOG_DELIVERY_TRANSIENT",
                BoundDetail(exception.Message));
        }

        return new IngestionCatalogDeliveryFailureDecision(
            false,
            null,
            transient
                ? "INGESTION_CATALOG_DELIVERY_RETRY_EXHAUSTED"
                : transport?.Code ?? "INGESTION_CATALOG_DELIVERY_TERMINAL",
            BoundDetail(exception.Message));
    }

    private static bool IsTransientInfrastructure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is NpgsqlException { IsTransient: true } ||
                current is DbUpdateException { InnerException: NpgsqlException { IsTransient: true } } ||
                current is HttpRequestException or TimeoutException or IOException or OperationCanceledException)
            {
                return true;
            }
        }

        return false;
    }

    private static T? Find<T>(Exception exception) where T : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static TimeSpan ComputeRetryDelay(int attempt)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 5);
        return TimeSpan.FromSeconds(Math.Min(120, 5 * (1 << exponent)));
    }

    private static string BoundDetail(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return "Catalog delivery failed without diagnostic detail.";
        }

        var normalized = detail.Trim();
        return normalized[..Math.Min(normalized.Length, 4_000)];
    }
}

/// <summary>Typed Catalog transport failure retaining status and Retry-After semantics.</summary>
public sealed class IngestionCatalogCommandTransportException : Exception
{
    public IngestionCatalogCommandTransportException(
        string code,
        HttpStatusCode statusCode,
        string message,
        TimeSpan? retryAfter = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public string Code { get; }

    public HttpStatusCode StatusCode { get; }

    public TimeSpan? RetryAfter { get; }

    public bool IsTransient =>
        StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)StatusCode >= 500;

    internal static IngestionCatalogCommandTransportException FromResponse(
        string code,
        HttpStatusCode statusCode,
        byte[] responseBytes,
        RetryConditionHeaderValue? retryAfter,
        DateTimeOffset receivedAtUtc)
    {
        var detail = responseBytes.Length == 0
            ? $"Catalog returned HTTP {(int)statusCode} without a response body."
            : Encoding.UTF8.GetString(responseBytes);
        detail = detail.Trim();
        if (detail.Length > 4_000)
        {
            detail = detail[..4_000];
        }

        return new IngestionCatalogCommandTransportException(
            code,
            statusCode,
            detail,
            ResolveRetryAfter(retryAfter, receivedAtUtc));
    }

    private static TimeSpan? ResolveRetryAfter(
        RetryConditionHeaderValue? value,
        DateTimeOffset receivedAtUtc)
    {
        if (receivedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Retry-After reference time must be UTC.", nameof(receivedAtUtc));
        }

        if (value?.Delta is { } delta)
        {
            return delta > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : delta;
        }

        if (value?.Date is { } date)
        {
            var delay = date - receivedAtUtc;
            return delay <= TimeSpan.Zero
                ? TimeSpan.Zero
                : delay > TimeSpan.FromMinutes(5)
                    ? TimeSpan.FromMinutes(5)
                    : delay;
        }

        return null;
    }
}
