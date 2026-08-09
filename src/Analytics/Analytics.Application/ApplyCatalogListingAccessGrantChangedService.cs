using Aggregator.Analytics.Domain;
using Aggregator.Catalog.Contracts;

namespace Aggregator.Analytics.Application;

/// <summary>Validates and applies one producer-owned Catalog listing access-grant change.</summary>
public sealed class ApplyCatalogListingAccessGrantChangedService(
    IListingMetricsAccessProjectionStore store,
    TimeProvider timeProvider)
{
    public Task<ListingMetricsAccessProjectionResult> ApplyAsync(
        CatalogListingAccessGrantProjectionMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        try
        {
            ValidateEnvelope(message);
            var accessEvent = message.Event
                ?? throw new AnalyticsDomainException(
                    "ANALYTICS_ACCESS_EVENT_REQUIRED",
                    "Catalog listing access message has no producer event payload.");
            if (message.MessageId != accessEvent.EventId)
            {
                throw new AnalyticsDomainException(
                    "ANALYTICS_ACCESS_MESSAGE_ID_MISMATCH",
                    "Catalog listing access message ID must match the producer-owned event identity.");
            }

            var permissions = RequireCanonicalPermissions(accessEvent.Permissions);
            var projection = MapProjection(
                accessEvent,
                permissions,
                message.PayloadDigest);
            var change = new ListingMetricsAccessProjectionChange(
                projection,
                projection.ProjectionDigest,
                message.MessageId,
                message.RoutingKey.Trim(),
                message.ContractIdentity.Trim(),
                message.PayloadDigest,
                message.CorrelationId.Trim(),
                message.CausationId);
            var receivedAtUtc = timeProvider.GetUtcNow();
            AnalyticsDomainRules.RequireUtc(receivedAtUtc, nameof(receivedAtUtc));
            if (receivedAtUtc < accessEvent.OccurredAtUtc)
            {
                throw new AnalyticsDomainException(
                    "ANALYTICS_ACCESS_RECEIVED_TIME_INVALID",
                    "Catalog listing access message cannot be received before its producer timestamp.");
            }

            return store.ApplyAsync(change, receivedAtUtc, cancellationToken);
        }
        catch (AnalyticsCommandException)
        {
            throw;
        }
        catch (AnalyticsDomainException exception)
        {
            throw InvalidAccessEvent(exception);
        }
    }

    private static void ValidateEnvelope(CatalogListingAccessGrantProjectionMessage message)
    {
        AnalyticsDomainRules.RequireIdentifier(message.MessageId, nameof(message.MessageId));
        RequireExact(
            message.RoutingKey,
            CatalogIntegrationEventTypes.ListingAccessGrantChanged,
            "ANALYTICS_ACCESS_ROUTING_KEY_INVALID",
            "Catalog listing access routing key is unsupported.");
        RequireExact(
            message.ContractIdentity,
            CatalogIntegrationEventContracts.ListingAccessGrantChanged,
            "ANALYTICS_ACCESS_CONTRACT_INVALID",
            "Catalog listing access contract identity is unsupported.");
        _ = AnalyticsDomainRules.RequireDigest(message.PayloadDigest, nameof(message.PayloadDigest));
        RequireTransportValue(message.CorrelationId, nameof(message.CorrelationId), 128);
        if (message.CausationId == Guid.Empty)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_ACCESS_CAUSATION_ID_INVALID",
                "Catalog listing access causation ID must be absent or a non-empty UUID.");
        }
    }

    private static IReadOnlyList<ListingAccessScopeContract> RequireCanonicalPermissions(
        IReadOnlyList<ListingAccessScopeContract>? permissions)
    {
        if (permissions is null || permissions.Count == 0)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_ACCESS_PERMISSIONS_REQUIRED",
                "Catalog listing access event must contain at least one permission.");
        }

        var values = permissions.ToArray();
        if (values.Any(permission => !Enum.IsDefined(permission)))
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_ACCESS_PERMISSION_UNSUPPORTED",
                "Catalog listing access event contains an unsupported permission.");
        }

        var canonical = values
            .Distinct()
            .OrderBy(permission => (int)permission)
            .ToArray();
        if (!values.SequenceEqual(canonical))
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_ACCESS_PERMISSIONS_NOT_CANONICAL",
                "Catalog listing access permissions must be unique and ordered by producer contract identity.");
        }

        return Array.AsReadOnly(canonical);
    }

    private static ListingMetricsAccessProjection MapProjection(
        CatalogListingAccessGrantChanged accessEvent,
        IReadOnlyList<ListingAccessScopeContract> permissions,
        string payloadDigest)
    {
        AnalyticsDomainRules.RequireIdentifier(accessEvent.EventId, nameof(accessEvent.EventId));
        AnalyticsDomainRules.RequireIdentifier(accessEvent.GrantId, nameof(accessEvent.GrantId));
        AnalyticsDomainRules.RequireIdentifier(accessEvent.ListingId, nameof(accessEvent.ListingId));
        AnalyticsDomainRules.RequireIdentifier(accessEvent.ActorId, nameof(accessEvent.ActorId));
        AnalyticsDomainRules.RequireUtc(accessEvent.GrantedAtUtc, nameof(accessEvent.GrantedAtUtc));
        AnalyticsDomainRules.RequireUtc(accessEvent.OccurredAtUtc, nameof(accessEvent.OccurredAtUtc));
        if (accessEvent.ExpiresAtUtc is not null)
        {
            AnalyticsDomainRules.RequireUtc(accessEvent.ExpiresAtUtc.Value, nameof(accessEvent.ExpiresAtUtc));
            if (accessEvent.ExpiresAtUtc <= accessEvent.GrantedAtUtc)
            {
                throw new AnalyticsDomainException(
                    "ANALYTICS_ACCESS_EXPIRATION_INVALID",
                    "Catalog listing access expiration must follow grant creation.");
            }
        }

        if (accessEvent.AggregateRevision <= 0)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_ACCESS_REVISION_INVALID",
                "Catalog listing access aggregate revision must be positive.");
        }

        var isActive = accessEvent.State switch
        {
            CatalogListingAccessGrantStateContract.Active => true,
            CatalogListingAccessGrantStateContract.Revoked => false,
            _ => throw new AnalyticsDomainException(
                "ANALYTICS_ACCESS_STATE_UNSUPPORTED",
                $"Catalog listing access state '{accessEvent.State}' is unsupported."),
        };
        DateTimeOffset? revokedAtUtc;
        if (isActive)
        {
            if (accessEvent.AggregateRevision != 1 ||
                accessEvent.OccurredAtUtc != accessEvent.GrantedAtUtc)
            {
                throw new AnalyticsDomainException(
                    "ANALYTICS_ACCESS_ACTIVE_REVISION_INVALID",
                    "An active Catalog access grant must be its initial revision and occur at grant creation.");
            }

            revokedAtUtc = null;
        }
        else
        {
            if (accessEvent.AggregateRevision < 2 ||
                accessEvent.OccurredAtUtc < accessEvent.GrantedAtUtc)
            {
                throw new AnalyticsDomainException(
                    "ANALYTICS_ACCESS_REVOKED_REVISION_INVALID",
                    "A revoked Catalog access grant must follow its initial grant revision.");
            }

            revokedAtUtc = accessEvent.OccurredAtUtc;
        }

        return ListingMetricsAccessProjection.Create(
            accessEvent.GrantId,
            accessEvent.ListingId,
            accessEvent.ActorId,
            isActive && permissions.Contains(ListingAccessScopeContract.ViewAnalytics),
            accessEvent.GrantedAtUtc,
            accessEvent.ExpiresAtUtc,
            revokedAtUtc,
            accessEvent.AggregateRevision,
            payloadDigest,
            accessEvent.OccurredAtUtc);
    }

    private static void RequireExact(
        string value,
        string expected,
        string code,
        string message)
    {
        var normalized = RequireTransportValue(value, nameof(value), 200);
        if (!string.Equals(normalized, expected, StringComparison.Ordinal))
        {
            throw new AnalyticsDomainException(code, message);
        }
    }

    private static string RequireTransportValue(
        string value,
        string name,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_ACCESS_MESSAGE_METADATA_INVALID",
                $"'{name}' must contain between one and {maximumLength} characters.");
        }

        return value.Trim();
    }

    private static AnalyticsCommandException InvalidAccessEvent(
        AnalyticsDomainException exception) =>
        new(
            "Analytics.AccessProjection",
            exception.Code,
            422,
            exception.Message,
            "Correct or replay the exact Catalog listing access-grant event before reading dependent owner metrics.");
}
