namespace Aggregator.Ingestion.Collector.Domain;

public enum CollectorCandidateKind
{
    Place = 1,
    Provider = 2,
}

public sealed record CollectorCandidate
{
    private CollectorCandidate(
        Guid candidateId,
        Guid subjectId,
        Guid subjectRevisionId,
        string sourceSystem,
        string sourceReference,
        DateTimeOffset observedAtUtc,
        CollectorCandidateKind kind,
        string externalId,
        string title,
        Uri website,
        decimal? hourlyPrice,
        string evidenceDigest,
        string contentDigest,
        DateTimeOffset acceptedAtUtc)
    {
        CandidateId = candidateId;
        SubjectId = subjectId;
        SubjectRevisionId = subjectRevisionId;
        SourceSystem = sourceSystem;
        SourceReference = sourceReference;
        ObservedAtUtc = observedAtUtc;
        Kind = kind;
        ExternalId = externalId;
        Title = title;
        Website = website;
        HourlyPrice = hourlyPrice;
        EvidenceDigest = evidenceDigest;
        ContentDigest = contentDigest;
        AcceptedAtUtc = acceptedAtUtc;
    }

    public Guid CandidateId { get; }

    public Guid SubjectId { get; }

    public Guid SubjectRevisionId { get; }

    public string SourceSystem { get; }

    public string SourceReference { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public CollectorCandidateKind Kind { get; }

    public string ExternalId { get; }

    public string Title { get; }

    public Uri Website { get; }

    public decimal? HourlyPrice { get; }

    public string EvidenceDigest { get; }

    public string ContentDigest { get; }

    public DateTimeOffset AcceptedAtUtc { get; }

    public static CollectorCandidate Create(
        Guid candidateId,
        Guid subjectId,
        Guid subjectRevisionId,
        string sourceSystem,
        string sourceReference,
        DateTimeOffset observedAtUtc,
        CollectorCandidateKind kind,
        string externalId,
        string title,
        Uri website,
        decimal? hourlyPrice,
        string evidenceDigest,
        string contentDigest,
        DateTimeOffset acceptedAtUtc)
    {
        RequireId(candidateId, nameof(candidateId));
        RequireId(subjectId, nameof(subjectId));
        RequireId(subjectRevisionId, nameof(subjectRevisionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(website);
        if (!website.IsAbsoluteUri || website.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException(
                "Collector candidate website must be an absolute HTTP URL.",
                nameof(website));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (hourlyPrice is < 0 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hourlyPrice),
                "Collector candidate hourly price must be between zero and 1,000,000.");
        }

        RequireUtc(observedAtUtc, nameof(observedAtUtc));
        RequireUtc(acceptedAtUtc, nameof(acceptedAtUtc));
        if (acceptedAtUtc < observedAtUtc)
        {
            throw new ArgumentException(
                "Collector candidate acceptance cannot precede observation.",
                nameof(acceptedAtUtc));
        }

        return new CollectorCandidate(
            candidateId,
            subjectId,
            subjectRevisionId,
            sourceSystem.Trim(),
            sourceReference.Trim(),
            observedAtUtc,
            kind,
            externalId.Trim(),
            title.Trim(),
            website,
            hourlyPrice,
            RequireDigest(evidenceDigest, nameof(evidenceDigest)),
            RequireDigest(contentDigest, nameof(contentDigest)),
            acceptedAtUtc);
    }

    private static void RequireId(Guid value, string name)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A non-empty identity is required.", name);
        }
    }

    private static void RequireUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A UTC timestamp is required.", name);
        }
    }

    private static string RequireDigest(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 64 ||
            value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "A lowercase SHA-256 hexadecimal digest is required.",
                name);
        }

        return value;
    }
}
