namespace Aggregator.Ingestion.Contracts;

/// <summary>Creates or revises one exact producer authorization registration.</summary>
public sealed record PutIngestionProducerRegistrationRequest(
    long ExpectedAggregateRevision,
    bool Active,
    IReadOnlyList<int> SupportedContractRevisions,
    string Reason);

/// <summary>Current immutable revision selected by the Ingestion producer registry.</summary>
public sealed record IngestionProducerRegistrationDto(
    string ProducerIdentity,
    bool Active,
    IReadOnlyList<int> SupportedContractRevisions,
    long AggregateRevision,
    string ContentDigest,
    string UpdatedByServiceIdentity,
    string Reason,
    DateTimeOffset UpdatedAtUtc);

public sealed record IngestionProducerRegistrationResponse(
    IngestionProducerRegistrationDto Registration,
    bool Replayed);
