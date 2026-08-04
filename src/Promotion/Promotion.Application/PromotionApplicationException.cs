namespace Aggregator.Promotion.Application;

public sealed class PromotionApplicationException : InvalidOperationException
{
    public PromotionApplicationException(
        string owner,
        string code,
        int statusCode,
        string message,
        string requiredAction,
        IReadOnlyDictionary<string, object?>? context = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredAction);
        if (statusCode is < 400 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }

        Owner = owner.Trim();
        Code = code.Trim();
        StatusCode = statusCode;
        RequiredAction = requiredAction.Trim();
        Context = context ?? new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    public string Owner { get; }

    public string Code { get; }

    public int StatusCode { get; }

    public string RequiredAction { get; }

    public IReadOnlyDictionary<string, object?> Context { get; }
}
