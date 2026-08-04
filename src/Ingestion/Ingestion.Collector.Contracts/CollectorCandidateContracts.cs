namespace Aggregator.Ingestion.Collector.Contracts;

public enum CollectorCandidateKindContract
{
    Place = 1,
    Provider = 2,
}

public sealed record SubmitCollectorCandidateRequest(
    Guid CommandId,
    string SourceSystem,
    string SourceReference,
    DateTimeOffset ObservedAtUtc,
    CollectorCandidateKindContract Kind,
    string ExternalId,
    string Title,
    string Website,
    decimal? HourlyPrice,
    string EvidenceDigest);

public sealed record CollectorCandidateResponse(
    Guid CommandId,
    Guid CandidateId,
    Guid SubjectId,
    Guid SubjectRevisionId,
    string SourceSystem,
    string SourceReference,
    DateTimeOffset ObservedAtUtc,
    CollectorCandidateKindContract Kind,
    string ExternalId,
    string Title,
    string Website,
    decimal? HourlyPrice,
    string EvidenceDigest,
    string ContentDigest,
    DateTimeOffset AcceptedAtUtc,
    bool Replayed);
