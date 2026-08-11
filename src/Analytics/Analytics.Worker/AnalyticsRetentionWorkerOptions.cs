namespace Aggregator.Analytics.Worker;

/// <summary>Required worker-owned policy for aggregate-closed raw interaction minimization.</summary>
public sealed record AnalyticsRetentionWorkerOptions
{
    public const string SectionName = "Analytics:Retention";

    public int RawEventRetentionDays { get; init; }

    public int BatchSize { get; init; }

    public TimeSpan PollInterval { get; init; }

    public TimeSpan ContinuationDelay { get; init; }

    public TimeSpan FailureDelay { get; init; }

    public int MaximumConsecutiveFailures { get; init; }

    public void Validate()
    {
        if (RawEventRetentionDays is < 1 or > 3650)
        {
            throw new InvalidOperationException(
                $"{SectionName}:RawEventRetentionDays must be between 1 and 3650.");
        }

        if (BatchSize is < 1 or > 5000)
        {
            throw new InvalidOperationException(
                $"{SectionName}:BatchSize must be between 1 and 5000.");
        }

        if (MaximumConsecutiveFailures is < 1 or > 20)
        {
            throw new InvalidOperationException(
                $"{SectionName}:MaximumConsecutiveFailures must be between 1 and 20.");
        }

        ValidateDelay(PollInterval, nameof(PollInterval), TimeSpan.FromSeconds(5), TimeSpan.FromHours(24));
        ValidateDelay(ContinuationDelay, nameof(ContinuationDelay), TimeSpan.FromMilliseconds(100), TimeSpan.FromMinutes(5));
        ValidateDelay(FailureDelay, nameof(FailureDelay), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10));
    }

    private static void ValidateDelay(
        TimeSpan value,
        string name,
        TimeSpan minimum,
        TimeSpan maximum)
    {
        if (value < minimum || value > maximum)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{name} must be between {minimum} and {maximum}.");
        }
    }
}
