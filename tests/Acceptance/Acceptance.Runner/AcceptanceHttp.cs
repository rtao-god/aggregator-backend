using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aggregator.Acceptance.Runner;

public static class AcceptanceHttp
{
    public static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static async Task<string> GetTokenAsync(
        HttpClient client,
        string scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        using var request = new HttpRequestMessage(HttpMethod.Post, "token")
        {
            Content = new FormUrlEncodedContent(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = "backend-acceptance-runner",
                    ["actor_id"] = "0198ff00-0000-7000-8000-000000000001",
                    ["scope"] = scope,
                }),
        };
        using var response = await client.SendAsync(request, cancellationToken);
        var token = await ReadRequiredAsync<TokenResponse>(response, cancellationToken);
        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException("Acceptance identity returned an empty access token.");
        }

        return token.AccessToken;
    }

    public static async Task<TResponse> PostAsync<TRequest, TResponse>(
        HttpClient client,
        string relativePath,
        TRequest requestBody,
        string? bearerToken,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(requestBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, relativePath)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(requestBody, SerializerOptions),
                Encoding.UTF8,
                "application/json"),
        };
        AddHeaders(request, bearerToken, headers);
        using var response = await client.SendAsync(request, cancellationToken);
        return await ReadRequiredAsync<TResponse>(response, cancellationToken);
    }

    public static async Task<HttpStatusCode> PostForStatusAsync<TRequest>(
        HttpClient client,
        string relativePath,
        TRequest requestBody,
        string? bearerToken,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(requestBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, relativePath)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(requestBody, SerializerOptions),
                Encoding.UTF8,
                "application/json"),
        };
        AddHeaders(request, bearerToken, headers);
        using var response = await client.SendAsync(request, cancellationToken);
        return response.StatusCode;
    }

    public static async Task<TResponse> GetAsync<TResponse>(
        HttpClient client,
        string relativePath,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        AddHeaders(request, bearerToken: null, headers);
        using var response = await client.SendAsync(request, cancellationToken);
        return await ReadRequiredAsync<TResponse>(response, cancellationToken);
    }

    public static async Task<JsonDocument> GetDocumentAsync(
        HttpClient client,
        string relativePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        using var response = await client.GetAsync(relativePath, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    public static async Task WaitForHealthAsync(
        HttpClient client,
        string relativePath,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await client.GetAsync(relativePath, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException(
            $"Acceptance endpoint '{client.BaseAddress}{relativePath}' did not become healthy before the deadline.");
    }

    public static async Task<T> WaitAsync<T>(
        Func<CancellationToken, Task<T?>> probe,
        DateTimeOffset deadline,
        string description,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Exception? lastException = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var value = await probe(cancellationToken);
                if (value is not null)
                {
                    return value;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException)
            {
                lastException = exception;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException(
            $"Timed out waiting for {description}.",
            lastException);
    }

    public static async Task<TResponse> ReadRequiredAsync<TResponse>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var value = await JsonSerializer.DeserializeAsync<TResponse>(
            stream,
            SerializerOptions,
            cancellationToken);
        return value
            ?? throw new JsonException(
                $"Acceptance endpoint '{response.RequestMessage?.RequestUri}' returned an empty JSON response.");
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"Acceptance request '{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}' failed with {(int)response.StatusCode} {response.StatusCode}: {body}",
            inner: null,
            response.StatusCode);
    }

    private static void AddHeaders(
        HttpRequestMessage request,
        string? bearerToken,
        IReadOnlyDictionary<string, string>? headers)
    {
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        if (headers is null)
        {
            return;
        }

        foreach (var (name, value) in headers)
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                throw new InvalidOperationException(
                    $"Acceptance header '{name}' could not be applied.");
            }
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            WriteIndented = false,
        };
        options.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        string Scope);
}
