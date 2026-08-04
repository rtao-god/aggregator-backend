using System.Collections.ObjectModel;

namespace Platform.ProblemDetails;

/// <summary>Describes a contract failure at the canonical production owner boundary.</summary>
public sealed record OwnerError
{
    public OwnerError(
        string owner,
        string code,
        string title,
        int status,
        string detail,
        string? requiredAction = null,
        IReadOnlyDictionary<string, object?>? context = null)
    {
        Owner = RequireText(owner, nameof(owner));
        Code = RequireText(code, nameof(code));
        Title = RequireText(title, nameof(title));
        Status = status is >= 400 and <= 599
            ? status
            : throw new ArgumentOutOfRangeException(nameof(status), status, "HTTP error status must be between 400 and 599.");
        Detail = RequireText(detail, nameof(detail));
        RequiredAction = string.IsNullOrWhiteSpace(requiredAction) ? null : requiredAction.Trim();
        Context = new ReadOnlyDictionary<string, object?>(
            context is null
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : new Dictionary<string, object?>(context, StringComparer.Ordinal));
    }

    public string Owner { get; }

    public string Code { get; }

    public string Title { get; }

    public int Status { get; }

    public string Detail { get; }

    public string? RequiredAction { get; }

    public IReadOnlyDictionary<string, object?> Context { get; }

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value.Trim();
}

/// <summary>Raises a typed owner failure without erasing its diagnostics.</summary>
public sealed class OwnerException : Exception
{
    public OwnerException(OwnerError error)
        : base(error?.Detail)
    {
        Error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public OwnerException(OwnerError error, Exception innerException)
        : base(error?.Detail, innerException)
    {
        Error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public OwnerError Error { get; }
}
