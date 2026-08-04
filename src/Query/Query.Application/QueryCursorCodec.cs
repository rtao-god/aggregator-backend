namespace Aggregator.Query.Application;

internal readonly record struct QueryCursor(
    Guid PublicReadRevisionId,
    Guid LastListingId,
    string QueryDigest);

internal static class QueryCursorCodec
{
    private const byte ContractRevision = 1;
    private const int PayloadLength = 65;

    public static string ComputeQueryDigest(string catalogKey, string locale, string? categoryKey) =>
        QueryCanonicalJson.ComputeDigest(new CursorQueryIdentity(catalogKey, locale, categoryKey));

    public static string Encode(Guid publicReadRevisionId, Guid lastListingId, string queryDigest)
    {
        if (publicReadRevisionId == Guid.Empty || lastListingId == Guid.Empty)
        {
            throw new ArgumentException("Cursor identities must be non-empty UUIDs.");
        }

        var digest = Convert.FromHexString(queryDigest);
        if (digest.Length != 32)
        {
            throw new ArgumentException("Cursor query digest must be SHA-256.", nameof(queryDigest));
        }

        Span<byte> payload = stackalloc byte[PayloadLength];
        payload[0] = ContractRevision;
        _ = publicReadRevisionId.TryWriteBytes(payload[1..17]);
        _ = lastListingId.TryWriteBytes(payload[17..33]);
        digest.CopyTo(payload[33..]);
        return Convert.ToBase64String(payload)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static QueryCursor Decode(string cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            throw InvalidCursor("Cursor must be a non-empty opaque value.");
        }

        var normalized = cursor.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - (normalized.Length % 4)) % 4), '=');
        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(normalized);
        }
        catch (FormatException exception)
        {
            throw InvalidCursor("Cursor is not valid base64url.", exception);
        }

        if (payload.Length != PayloadLength || payload[0] != ContractRevision)
        {
            throw InvalidCursor("Cursor contract revision or payload length is unsupported.");
        }

        var publicReadRevisionId = new Guid(payload.AsSpan(1, 16));
        var lastListingId = new Guid(payload.AsSpan(17, 16));
        if (publicReadRevisionId == Guid.Empty || lastListingId == Guid.Empty)
        {
            throw InvalidCursor("Cursor contains an empty required identity.");
        }

        return new QueryCursor(
            publicReadRevisionId,
            lastListingId,
            Convert.ToHexString(payload.AsSpan(33, 32)).ToLowerInvariant());
    }

    private static QueryReadException InvalidCursor(string message, Exception? innerException = null) =>
        new(
            "Query.Cursor",
            "QUERY_CURSOR_INVALID",
            400,
            message,
            "Restart the search without the cursor.",
            innerException: innerException);

    private sealed record CursorQueryIdentity(string CatalogKey, string Locale, string? CategoryKey);
}
