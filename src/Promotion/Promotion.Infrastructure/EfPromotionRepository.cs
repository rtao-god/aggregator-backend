using System.Data;
using Aggregator.Promotion.Application;
using Aggregator.Promotion.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aggregator.Promotion.Infrastructure;

/// <summary>Promotion-owned PostgreSQL repository with atomic idempotency and outbox persistence.</summary>
public sealed partial class EfPromotionRepository : IPromotionRepository
{
    private readonly PromotionDbContext _dbContext;

    public EfPromotionRepository(PromotionDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    private async Task<PromotionCommandResult<TAggregate>> ExecuteCommandAsync<TAggregate>(
        PromotionCommandIdentity commandIdentity,
        PromotionCommandContext commandContext,
        Func<CancellationToken, Task<TAggregate>> stageMutation,
        CancellationToken cancellationToken)
        where TAggregate : class
    {
        ArgumentNullException.ThrowIfNull(commandIdentity);
        ArgumentNullException.ThrowIfNull(commandContext);
        ArgumentNullException.ThrowIfNull(stageMutation);
        var replay = await TryReadReplayAsync<TAggregate>(commandIdentity, cancellationToken);
        if (replay is not null)
        {
            return replay;
        }

        try
        {
            return await ExecuteInTransactionAsync(async innerCancellationToken =>
            {
                var transactionReplay = await TryReadReplayAsync<TAggregate>(
                    commandIdentity,
                    innerCancellationToken);
                if (transactionReplay is not null)
                {
                    return transactionReplay;
                }

                var aggregate = await stageMutation(innerCancellationToken);
                AddCommandResult(commandIdentity, commandContext, aggregate);
                await _dbContext.SaveChangesAsync(innerCancellationToken);
                return new PromotionCommandResult<TAggregate>(aggregate, Replayed: false);
            }, cancellationToken);
        }
        catch (DbUpdateException exception) when (IsConcurrentCommandConflict(exception))
        {
            _dbContext.ChangeTracker.Clear();
            var concurrentReplay = await TryReadReplayAsync<TAggregate>(commandIdentity, cancellationToken);
            return concurrentReplay ?? throw Failure(
                "Promotion.Commands",
                "PROMOTION_CONCURRENT_COMMAND_UNRESOLVED",
                409,
                "A concurrent Promotion command won the idempotency race but its result is not readable.",
                "Retry the exact request after the winning transaction is visible.",
                exception);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            _dbContext.ChangeTracker.Clear();
            var concurrentReplay = await TryReadReplayAsync<TAggregate>(commandIdentity, cancellationToken);
            return concurrentReplay ?? throw Failure(
                "Promotion.Persistence",
                "PROMOTION_SERIALIZATION_CONFLICT",
                409,
                "Promotion command conflicted with another serializable owner transaction.",
                "Reload the current aggregate revision before retrying the command.",
                exception);
        }
    }

    private async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            var result = await operation(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<PromotionCommandResult<TAggregate>?> TryReadReplayAsync<TAggregate>(
        PromotionCommandIdentity commandIdentity,
        CancellationToken cancellationToken)
        where TAggregate : class
    {
        var row = await _dbContext.Commands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.Scope == commandIdentity.Scope &&
                    candidate.IdempotencyKey == commandIdentity.Key,
                cancellationToken);
        if (row is null)
        {
            return null;
        }

        if (!string.Equals(row.RequestDigest, commandIdentity.RequestDigest, StringComparison.Ordinal))
        {
            throw Failure(
                "Promotion.Commands",
                "PROMOTION_IDEMPOTENCY_CONFLICT",
                409,
                "Promotion command key was already used with another request digest.",
                "Use the original request or submit a new semantic idempotency key.");
        }

        var aggregate = PromotionPersistenceJson.DeserializeResult<TAggregate>(
            row.ResultKind,
            row.ResultJson,
            row.ResultDigest);
        return new PromotionCommandResult<TAggregate>(aggregate, Replayed: true);
    }

    private void AddCommandResult<TAggregate>(
        PromotionCommandIdentity commandIdentity,
        PromotionCommandContext commandContext,
        TAggregate aggregate)
        where TAggregate : class
    {
        var result = PromotionPersistenceJson.SerializeResult(aggregate);
        _dbContext.Commands.Add(new PromotionCommandRow
        {
            Scope = commandIdentity.Scope,
            IdempotencyKey = commandIdentity.Key,
            RequestDigest = commandIdentity.RequestDigest,
            ResultKind = result.Kind,
            ResultJson = result.Json,
            ResultDigest = result.Digest,
            ActorId = commandContext.Actor.Id,
            CorrelationId = commandContext.CorrelationId,
            CreatedAtUtc = aggregate switch
            {
                PromotionProduct product => product.CurrentRevision.CreatedAtUtc,
                PromotionEntitlement entitlement => entitlement.ChangedAtUtc,
                SponsoredPlacement placement => placement.ChangedAtUtc,
                _ => throw Failure(
                    "Promotion.Persistence",
                    "PROMOTION_RESULT_KIND_UNSUPPORTED",
                    500,
                    $"Promotion aggregate '{typeof(TAggregate).Name}' cannot own a command result.",
                    "Add an explicit owner persistence contract before storing this command result."),
            },
        });
    }

    private void AddOutbox(PromotionOutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _dbContext.OutboxMessages.Add(new PromotionOutboxRow
        {
            MessageId = message.Id,
            RoutingKey = message.EventType,
            ContractIdentity = message.ContractIdentity,
            PayloadJson = message.Payload,
            PayloadDigest = message.PayloadDigest,
            OccurredAtUtc = message.OccurredAtUtc,
            CorrelationId = message.CorrelationId,
            CausationId = message.CausationId,
            LeaseToken = null,
            LeasedBy = null,
            LeaseExpiresAtUtc = null,
            DeliveryAttempts = 0,
            DispatchedAtUtc = null,
            LastError = null,
            DeadLetteredAtUtc = null,
            DeadLetterReason = null,
        });
    }

    private static bool IsConcurrentCommandConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres &&
        postgres.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure;

    private static PromotionApplicationException Failure(
        string owner,
        string code,
        int statusCode,
        string message,
        string requiredAction,
        Exception? innerException = null) =>
        new(
            owner,
            code,
            statusCode,
            message,
            requiredAction,
            innerException: innerException);
}
