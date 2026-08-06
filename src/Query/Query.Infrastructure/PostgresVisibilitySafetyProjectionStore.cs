using Aggregator.Query.Application;
using Aggregator.Query.Domain;
using Npgsql;

namespace Aggregator.Query.Infrastructure;

/// <summary>
/// Applies Catalog suppression events through two committed phases: durable visibility block first,
/// then immutable overlay materialization and one composite public-read pointer switch.
/// </summary>
public sealed partial class PostgresVisibilitySafetyProjectionStore :
    IVisibilitySafetyProjectionStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IQueryIdFactory _idFactory;
    private readonly IQueryClock _clock;

    public PostgresVisibilitySafetyProjectionStore(
        NpgsqlDataSource dataSource,
        IQueryIdFactory idFactory,
        IQueryClock clock)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _idFactory = idFactory ?? throw new ArgumentNullException(nameof(idFactory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<VisibilitySafetyProjectionResult> ApplyAsync(
        QueryVisibilitySuppression suppression,
        VisibilitySuppressionInboxMessage inboxMessage,
        CancellationToken cancellationToken)
    {
        ValidateInput(suppression, inboxMessage);
        var begin = await BeginAsync(suppression, inboxMessage, cancellationToken);
        if (begin.FinalResult is not null)
        {
            return begin.FinalResult;
        }

        if (begin.ConflictMessage is not null)
        {
            throw Failure(
                "QUERY_VISIBILITY_REVISION_CONFLICT",
                409,
                begin.ConflictMessage,
                "Keep the catalog blocked, quarantine the event, and inspect the Catalog suppression outbox for divergent revision payloads.");
        }

        return await CompleteAsync(suppression, inboxMessage, cancellationToken);
    }

    private static void ValidateInput(
        QueryVisibilitySuppression suppression,
        VisibilitySuppressionInboxMessage inboxMessage)
    {
        ArgumentNullException.ThrowIfNull(suppression);
        ArgumentNullException.ThrowIfNull(inboxMessage);
        if (inboxMessage.EventId == Guid.Empty)
        {
            throw Failure(
                "QUERY_VISIBILITY_EVENT_ID_INVALID",
                500,
                "Visibility safety store received an empty event ID.",
                "Correct the Query worker envelope validation before persistence.");
        }

        if (!IsDigest(inboxMessage.PayloadDigest))
        {
            throw Failure(
                "QUERY_VISIBILITY_PAYLOAD_DIGEST_INVALID",
                500,
                "Visibility safety store received a non-canonical SHA-256 payload digest.",
                "Verify the exact producer payload bytes before invoking Query persistence.");
        }

        if (inboxMessage.ReceivedAtUtc.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "QUERY_VISIBILITY_RECEIVED_AT_NOT_UTC",
                500,
                "Visibility safety inbox received a non-UTC timestamp.",
                "Configure the Query worker clock to return UTC timestamps.");
        }

        if (suppression.State == QueryVisibilitySuppressionState.Active)
        {
            suppression.EnsureValidInitialProjection();
        }
        else if (suppression.AggregateRevision != 3)
        {
            throw Failure(
                "QUERY_VISIBILITY_RESOLVED_REVISION_INVALID",
                422,
                "A resolved Catalog suppression must carry aggregate revision '3'.",
                "Republish the exact resolved suppression revision from Catalog.");
        }
    }

    private static bool IsDigest(string value) =>
        value is { Length: 64 } &&
        value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static QueryProjectionException Failure(
        string code,
        int statusCode,
        string message,
        string requiredAction,
        IReadOnlyDictionary<string, object?>? context = null) =>
        new(
            "Query.VisibilitySafety",
            code,
            statusCode,
            message,
            requiredAction,
            context);

    private sealed record BeginResult(
        VisibilitySafetyProjectionResult? FinalResult,
        string? ConflictMessage)
    {
        public static BeginResult Pending { get; } = new(null, null);
    }

    private sealed record PersistedInbox(
        string PayloadDigest,
        string ProcessingState,
        Guid? ResultPublicReadRevisionId);

    private sealed record CurrentReadContext(
        PublicReadRevision Revision,
        string BaseProjectionDigest,
        string PromotionOverlayDigest,
        long SafetySourceRevision,
        long PointerActivationRevision);
}
