namespace Aggregator.Catalog.Application;

public sealed class CatalogCommandException : Exception
{
    public CatalogCommandException(
        string owner,
        string code,
        int statusCode,
        string message,
        string requiredAction,
        IReadOnlyDictionary<string, object?>? context = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Owner = Require(owner, nameof(owner));
        Code = Require(code, nameof(code));
        RequiredAction = Require(requiredAction, nameof(requiredAction));
        if (statusCode is < 400 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode), "Status code must represent an HTTP error.");
        }

        StatusCode = statusCode;
        Context = context ?? new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    public string Owner { get; }

    public string Code { get; }

    public int StatusCode { get; }

    public string RequiredAction { get; }

    public IReadOnlyDictionary<string, object?> Context { get; }

    private static string Require(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value;
}

public sealed record CatalogCommandIdentity(string Scope, string Key, string RequestDigest)
{
    public static CatalogCommandIdentity Create(string scope, string key, string requestDigest)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope.Length > 150)
        {
            throw new CatalogCommandException(
                "Catalog.Commands",
                "IDEMPOTENCY_SCOPE_INVALID",
                500,
                "The command owner supplied an invalid idempotency scope.",
                "Correct the command composition root before retrying.");
        }

        if (string.IsNullOrWhiteSpace(key) || key.Length > 200)
        {
            throw new CatalogCommandException(
                "Catalog.Commands",
                "IDEMPOTENCY_KEY_INVALID",
                400,
                "A non-empty Idempotency-Key of at most 200 characters is required.",
                "Submit the command with one stable Idempotency-Key.");
        }

        ArgumentNullException.ThrowIfNull(requestDigest);
        if (requestDigest.Length != 64 || requestDigest.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new CatalogCommandException(
                "Catalog.Commands",
                "REQUEST_DIGEST_INVALID",
                500,
                "The command request digest is invalid.",
                "Correct canonical request hashing before retrying.");
        }

        return new CatalogCommandIdentity(scope, key, requestDigest);
    }
}

public sealed record CommandPersistenceResult<T>(T Value, bool Replayed);
