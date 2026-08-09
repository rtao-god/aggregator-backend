using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

/// <summary>Canonical lifecycle stored for one durable Catalog publication operation.</summary>
public enum CatalogPublicationOperationState
{
    Pending = 1,
    Leased = 2,
    RetryWait = 3,
    Completed = 4,
    Failed = 5,
}

/// <summary>Owner-context failure retained with a terminal or retryable publication attempt.</summary>
public sealed record CatalogPublicationOperationFailure(
    string Owner,
    string Code,
    string Detail,
    string RequiredAction)
{
    public static CatalogPublicationOperationFailure Create(
        string owner,
        string code,
        string detail,
        string requiredAction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredAction);
        return new CatalogPublicationOperationFailure(
            owner.Trim(),
            code.Trim(),
            detail.Trim(),
            requiredAction.Trim());
    }
}

/// <summary>Immutable registration command for a durable publication operation.</summary>
public sealed record CatalogPublicationOperationRegistration(
    Guid OperationId,
    Guid PublicationId,
    string CatalogKey,
    Guid ActorId,
    string IdempotencyKey,
    byte[] RequestDocument,
    string RequestDigest,
    string CorrelationId,
    Guid? CausationId,
    DateTimeOffset CreatedAtUtc);

/// <summary>Read model of one durable publication operation.</summary>
public sealed record CatalogPublicationOperationSnapshot(
    Guid OperationId,
    Guid PlannedPublicationId,
    long PlannedPublicationSequence,
    string CatalogKey,
    Guid ActorId,
    CatalogPublicationOperationState State,
    int Attempt,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? NextAttemptAtUtc,
    Guid? PublicationId,
    CatalogPublicationOperationFailure? Failure);

/// <summary>Exclusive execution lease over one exact publication request snapshot.</summary>
public sealed record CatalogPublicationOperationLease(
    Guid OperationId,
    Guid PublicationId,
    long PublicationSequence,
    string CatalogKey,
    Guid ActorId,
    byte[] RequestDocument,
    string RequestDigest,
    string CorrelationId,
    Guid? CausationId,
    DateTimeOffset CreatedAtUtc,
    Guid LeaseToken,
    int Attempt);

/// <summary>Prepared publication effect awaiting one atomic Catalog database commit.</summary>
public sealed record CatalogPreparedPublication(
    CatalogPublication Publication,
    Guid? ExpectedCurrentPublicationId,
    IReadOnlyList<Listing> Listings,
    CatalogPublicationActivationOutboxFactory OutboxFactory);

/// <summary>Exact lease identity that must be consumed by the publication transaction.</summary>
public sealed record CatalogPublicationOperationCompletion(
    Guid OperationId,
    Guid LeaseToken,
    DateTimeOffset CompletedAtUtc);

/// <summary>Signals that a worker no longer owns the publication operation it attempted to mutate.</summary>
public sealed class CatalogPublicationOperationLeaseLostException : InvalidOperationException
{
    public CatalogPublicationOperationLeaseLostException(Guid operationId)
        : base($"Catalog publication operation '{operationId}' lease is absent, expired, or owned by another worker.")
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("Operation ID is required.", nameof(operationId));
        }

        OperationId = operationId;
    }

    public Guid OperationId { get; }
}

/// <summary>Failure classifier decision used by the durable publication executor.</summary>
public sealed record CatalogPublicationOperationFailureDecision(
    bool Retry,
    DateTimeOffset? NextAttemptAtUtc,
    CatalogPublicationOperationFailure Failure);

/// <summary>Persists and leases durable Catalog publication operations.</summary>
public interface ICatalogPublicationOperationStore
{
    public Task<CatalogPublicationOperationSnapshot> RegisterAsync(
        CatalogPublicationOperationRegistration registration,
        CancellationToken cancellationToken);

    public Task<CatalogPublicationOperationSnapshot?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken);

    public Task<CatalogPublicationOperationLease?> ClaimNextAsync(
        string workerIdentity,
        DateTimeOffset claimedAtUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    public Task ScheduleRetryAsync(
        Guid operationId,
        Guid leaseToken,
        CatalogPublicationOperationFailure failure,
        DateTimeOffset nextAttemptAtUtc,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken);

    public Task FailAsync(
        Guid operationId,
        Guid leaseToken,
        CatalogPublicationOperationFailure failure,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken);
}

/// <summary>Commits publication state and consumes its exact operation lease in one Catalog transaction.</summary>
public interface ICatalogPublicationOperationCommitter
{
    public Task CommitAsync(
        CatalogPreparedPublication preparedPublication,
        CatalogPublicationOperationCompletion completion,
        CancellationToken cancellationToken);
}

/// <summary>Classifies publication failures without weakening owner contracts.</summary>
public interface ICatalogPublicationOperationFailureClassifier
{
    public CatalogPublicationOperationFailureDecision Classify(
        Exception exception,
        int attempt,
        int maximumAttempts,
        DateTimeOffset failedAtUtc);
}

/// <summary>Queues and reads durable Catalog publication operations.</summary>
public sealed class CatalogPublicationOperationService(
    ICatalogPublicationOperationStore store,
    ICatalogIdSource idSource,
    TimeProvider timeProvider)
{
    public async Task<CatalogPublicationOperationResponse> EnqueueAsync(
        CreateCatalogPublicationRequest request,
        CatalogActor actor,
        CatalogEventContext eventContext,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        CatalogPublicationRequestValidator.Validate(request);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(eventContext);
        var normalizedIdempotencyKey = NormalizeIdempotencyKey(idempotencyKey);
        var requestDocument = CatalogCanonicalJson.SerializePublicationRequest(request);
        var requestDigest = CatalogCanonicalJson.ComputeSha256(requestDocument);
        var createdAtUtc = timeProvider.GetUtcNow();
        var registration = new CatalogPublicationOperationRegistration(
            idSource.CreateId(),
            idSource.CreateId(),
            CatalogKey.Create(request.CatalogKey).Value,
            actor.Id,
            normalizedIdempotencyKey,
            requestDocument,
            requestDigest,
            eventContext.CorrelationId,
            eventContext.CausationId,
            createdAtUtc);
        var operation = await store.RegisterAsync(registration, cancellationToken);
        return ToResponse(operation);
    }

    public async Task<CatalogPublicationOperationResponse> GetAsync(
        Guid operationId,
        CatalogActor actor,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty)
        {
            throw new CatalogContractException(
                "catalog.publication_operation_id_invalid",
                "Publication operation ID must be a non-empty UUID.");
        }

        ArgumentNullException.ThrowIfNull(actor);
        var operation = await store.GetAsync(operationId, cancellationToken)
            ?? throw new CatalogNotFoundException("catalog-publication-operation", operationId);
        if (operation.ActorId != actor.Id)
        {
            throw new CatalogNotFoundException("catalog-publication-operation", operationId);
        }

        return ToResponse(operation);
    }

    private static string NormalizeIdempotencyKey(string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var normalized = idempotencyKey.Trim();
        if (normalized.Length is < 8 or > 128 || normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':')))
        {
            throw new CatalogContractException(
                "catalog.idempotency_key_invalid",
                "Idempotency-Key must contain 8 to 128 allowlisted ASCII characters.");
        }

        return normalized;
    }

    private static CatalogPublicationOperationResponse ToResponse(
        CatalogPublicationOperationSnapshot operation) =>
        new(
            operation.OperationId,
            operation.CatalogKey,
            operation.State switch
            {
                CatalogPublicationOperationState.Pending => CatalogOperationStateContract.Pending,
                CatalogPublicationOperationState.Leased => CatalogOperationStateContract.Leased,
                CatalogPublicationOperationState.RetryWait => CatalogOperationStateContract.RetryWait,
                CatalogPublicationOperationState.Completed => CatalogOperationStateContract.Completed,
                CatalogPublicationOperationState.Failed => CatalogOperationStateContract.Failed,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(operation),
                    operation.State,
                    "Publication operation state is unsupported."),
            },
            operation.Attempt,
            operation.CreatedAtUtc,
            operation.UpdatedAtUtc,
            operation.NextAttemptAtUtc,
            operation.PublicationId,
            operation.Failure is null
                ? null
                : new CatalogOperationFailureContract(
                    operation.Failure.Owner,
                    operation.Failure.Code,
                    operation.Failure.Detail,
                    operation.Failure.RequiredAction));
}

/// <summary>Executes one leased publication operation through the existing Catalog publication owner.</summary>
public sealed class CatalogPublicationOperationExecutor(
    ICatalogPublicationOperationStore store,
    ICatalogPublicationOperationCommitter committer,
    ICatalogPublicationOperationFailureClassifier failureClassifier,
    CatalogPublicationService publicationService,
    TimeProvider timeProvider)
{
    public async Task<bool> ExecuteNextAsync(
        string workerIdentity,
        TimeSpan leaseDuration,
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerIdentity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumAttempts);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        var lease = await store.ClaimNextAsync(
            workerIdentity.Trim(),
            timeProvider.GetUtcNow(),
            leaseDuration,
            cancellationToken);
        if (lease is null)
        {
            return false;
        }

        if (lease.Attempt > maximumAttempts)
        {
            try
            {
                await store.FailAsync(
                    lease.OperationId,
                    lease.LeaseToken,
                    CatalogPublicationOperationFailure.Create(
                        "Catalog.Publications",
                        "CATALOG_PUBLICATION_ATTEMPT_LIMIT_EXCEEDED",
                        $"Publication operation '{lease.OperationId}' exceeded its maximum attempt count '{maximumAttempts}'.",
                        "Inspect the retained operation failures and create a new publication request after correcting the owner state."),
                    timeProvider.GetUtcNow(),
                    cancellationToken);
            }
            catch (CatalogPublicationOperationLeaseLostException)
            {
                // Another worker owns the operation now; this attempt must not mutate it.
            }

            return true;
        }

        try
        {
            var computedDigest = CatalogCanonicalJson.ComputeSha256(lease.RequestDocument);
            if (!string.Equals(computedDigest, lease.RequestDigest, StringComparison.Ordinal))
            {
                throw new CatalogContractException(
                    "catalog.publication_operation_request_digest_mismatch",
                    $"Publication operation '{lease.OperationId}' request document does not match its persisted digest.");
            }

            var request = CatalogCanonicalJson.DeserializePublicationRequest(lease.RequestDocument);
            var preparedPublication = await publicationService.PrepareAsync(
                request,
                CatalogActor.Create(lease.ActorId),
                CatalogEventContext.Create(lease.CorrelationId, lease.CausationId),
                lease.PublicationId,
                lease.PublicationSequence,
                lease.CreatedAtUtc,
                cancellationToken);
            await committer.CommitAsync(
                preparedPublication,
                new CatalogPublicationOperationCompletion(
                    lease.OperationId,
                    lease.LeaseToken,
                    timeProvider.GetUtcNow()),
                cancellationToken);
        }
        catch (CatalogPublicationOperationLeaseLostException)
        {
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failedAtUtc = timeProvider.GetUtcNow();
            var decision = failureClassifier.Classify(
                exception,
                lease.Attempt,
                maximumAttempts,
                failedAtUtc);
            try
            {
                if (decision.Retry)
                {
                    var nextAttemptAtUtc = decision.NextAttemptAtUtc
                        ?? throw new InvalidOperationException(
                            "Retryable publication failure must define the next attempt time.");
                    await store.ScheduleRetryAsync(
                        lease.OperationId,
                        lease.LeaseToken,
                        decision.Failure,
                        nextAttemptAtUtc,
                        failedAtUtc,
                        cancellationToken);
                }
                else
                {
                    await store.FailAsync(
                        lease.OperationId,
                        lease.LeaseToken,
                        decision.Failure,
                        failedAtUtc,
                        cancellationToken);
                }
            }
            catch (CatalogPublicationOperationLeaseLostException)
            {
                // A replacement attempt owns the operation; retain its state unchanged.
            }
        }

        return true;
    }
}

internal static class CatalogPublicationRequestValidator
{
    public static void Validate(CreateCatalogPublicationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CatalogKey);
        if (request.ConfigurationRevisionId == Guid.Empty)
        {
            throw new CatalogContractException(
                "catalog.publication_configuration_revision_invalid",
                "Publication configuration revision ID must be a non-empty UUID.");
        }

        ArgumentNullException.ThrowIfNull(request.ExpectedCurrent);
        _ = request.ExpectedCurrent.Kind switch
        {
            PointerExpectationKindContract.Absent when request.ExpectedCurrent.PublicationId is null => true,
            PointerExpectationKindContract.Exact when request.ExpectedCurrent.PublicationId is { } publicationId && publicationId != Guid.Empty => true,
            _ => throw new CatalogContractException(
                "catalog.publication_pointer_expectation_invalid",
                "Publication pointer expectation must be either explicit absence or an exact non-empty publication ID."),
        };

        ArgumentNullException.ThrowIfNull(request.Selections);
        if (request.Selections.Count == 0)
        {
            throw new CatalogContractException(
                "catalog.publication_empty",
                "A publication must contain at least one exact listing revision selection.");
        }

        foreach (var selection in request.Selections)
        {
            if (selection.ListingId == Guid.Empty || selection.ListingRevisionId == Guid.Empty)
            {
                throw new CatalogContractException(
                    "catalog.publication_selection_identity_invalid",
                    "Every publication selection must contain non-empty listing and listing revision IDs.");
            }

            if (selection.ExpectedListingVersion < 0)
            {
                throw new CatalogContractException(
                    "catalog.publication_listing_version_invalid",
                    "Expected listing version cannot be negative.");
            }
        }

        var duplicateListings = request.Selections
            .GroupBy(selection => selection.ListingId)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateListings.Length > 0)
        {
            throw new CatalogContractException(
                "catalog.publication_duplicate_listing",
                $"Publication contains duplicate listings: {string.Join(", ", duplicateListings)}.");
        }
    }
}
