using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var issuer = builder.Configuration["AcceptanceIdentity:Issuer"]
    ?? "http://acceptance-identity:8080";
var keyId = builder.Configuration["AcceptanceIdentity:KeyId"]
    ?? "acceptance-rs256";
var rsa = RSA.Create(2048);
builder.Services.AddSingleton(rsa);

string[] supportedGrantTypes = ["client_credentials"];
string[] supportedAuthenticationMethods = ["none"];
string[] supportedSigningAlgorithms = ["RS256"];
string[] supportedScopes =
[
    "catalog.manage-configuration",
    "catalog.edit-listing",
    "catalog.publish",
    "catalog.rollback",
    "catalog.submit-claim",
    "catalog.verify-claim",
    "catalog.test-contracts",
    "ingestion.submit",
    "ingestion.review",
    "ingestion.admin",
    "promotion.manage",
    "promotion.publish",
    "promotion.overlay.publish",
    "analytics.view-listing",
    "analytics.test-contracts",
];
string[] audiences =
[
    "aggregator-catalog-command",
    "aggregator-ingestion-command",
    "aggregator-promotion-command",
    "aggregator-promotion-overlay",
    "aggregator-analytics",
];
var discoveryDocument = new
{
    issuer,
    jwks_uri = $"{issuer.TrimEnd('/')}/jwks",
    token_endpoint = $"{issuer.TrimEnd('/')}/token",
    grant_types_supported = supportedGrantTypes,
    token_endpoint_auth_methods_supported = supportedAuthenticationMethods,
    scopes_supported = supportedScopes,
    id_token_signing_alg_values_supported = supportedSigningAlgorithms,
};
var publicParameters = rsa.ExportParameters(includePrivateParameters: false);
var jwksDocument = new
{
    keys = new[]
    {
        new
        {
            kty = "RSA",
            use = "sig",
            kid = keyId,
            alg = "RS256",
            n = Base64Url(publicParameters.Modulus
                ?? throw new InvalidOperationException("RSA modulus is unavailable.")),
            e = Base64Url(publicParameters.Exponent
                ?? throw new InvalidOperationException("RSA exponent is unavailable.")),
        },
    },
};

var app = builder.Build();
app.MapGet("/.well-known/openid-configuration", () => Results.Json(discoveryDocument));
app.MapGet("/jwks", () => Results.Json(jwksDocument));
app.MapPost("/token", async (HttpRequest request, RSA signingKey) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new
        {
            error = "invalid_request",
            error_description = "Token request must use application/x-www-form-urlencoded.",
        });
    }

    var form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
    if (!string.Equals(form["grant_type"], "client_credentials", StringComparison.Ordinal))
    {
        return Results.BadRequest(new
        {
            error = "unsupported_grant_type",
        });
    }

    var now = DateTimeOffset.UtcNow;
    var expiresAt = now.AddMinutes(15);
    var scope = string.IsNullOrWhiteSpace(form["scope"])
        ? string.Empty
        : form["scope"].ToString().Trim();
    var subject = string.IsNullOrWhiteSpace(form["client_id"])
        ? "acceptance-client"
        : form["client_id"].ToString().Trim();
    var actorId = string.IsNullOrWhiteSpace(form["actor_id"])
        ? "0198ff00-0000-7000-8000-000000000001"
        : form["actor_id"].ToString().Trim();
    if (!Guid.TryParse(actorId, out var parsedActorId) || parsedActorId == Guid.Empty)
    {
        return Results.BadRequest(new
        {
            error = "invalid_request",
            error_description = "actor_id must be a non-empty GUID.",
        });
    }

    var token = CreateToken(
        signingKey,
        keyId,
        issuer,
        audiences,
        subject,
        parsedActorId,
        scope,
        now,
        expiresAt);
    return Results.Json(new
    {
        access_token = token,
        token_type = "Bearer",
        expires_in = checked((int)(expiresAt - now).TotalSeconds),
        scope,
    });
});
app.MapGet("/health/live", () => Results.Ok(new
{
    owner = "Acceptance.Identity",
    state = "live",
}));

await app.RunAsync();

static string CreateToken(
    RSA rsa,
    string keyId,
    string issuer,
    IReadOnlyList<string> audiences,
    string subject,
    Guid actorId,
    string scope,
    DateTimeOffset issuedAt,
    DateTimeOffset expiresAt)
{
    ArgumentNullException.ThrowIfNull(audiences);
    var header = JsonSerializer.SerializeToUtf8Bytes(new
    {
        alg = "RS256",
        typ = "JWT",
        kid = keyId,
    });
    var payload = JsonSerializer.SerializeToUtf8Bytes(new
    {
        iss = issuer,
        aud = audiences,
        sub = subject,
        actor_id = actorId.ToString("D"),
        scope,
        iat = issuedAt.ToUnixTimeSeconds(),
        nbf = issuedAt.AddSeconds(-5).ToUnixTimeSeconds(),
        exp = expiresAt.ToUnixTimeSeconds(),
        jti = Guid.CreateVersion7().ToString("D"),
    });
    var signingInput = $"{Base64Url(header)}.{Base64Url(payload)}";
    var signature = rsa.SignData(
        Encoding.ASCII.GetBytes(signingInput),
        HashAlgorithmName.SHA256,
        RSASignaturePadding.Pkcs1);
    return $"{signingInput}.{Base64Url(signature)}";
}

static string Base64Url(ReadOnlySpan<byte> value) =>
    Convert.ToBase64String(value)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

public partial class Program;
