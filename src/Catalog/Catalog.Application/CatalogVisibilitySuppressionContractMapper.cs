using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

internal static class CatalogVisibilitySuppressionContractMapper
{
    public static PublicVisibilitySuppressionTarget ToDomain(
        PublicVisibilitySuppressionTargetContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return PublicVisibilitySuppressionTarget.Create(
            ToDomain(contract.Kind),
            contract.ListingId,
            contract.TargetKey);
    }

    public static PublicVisibilitySuppressionResponse ToResponse(
        PublicVisibilitySuppression suppression)
    {
        ArgumentNullException.ThrowIfNull(suppression);
        return new PublicVisibilitySuppressionResponse(
            suppression.Id,
            suppression.CatalogKey.Value,
            ToContract(suppression.Target),
            suppression.PublicReasonClass,
            suppression.PrivateEvidenceReference,
            ToContract(suppression.ResponseMode),
            suppression.StartsAtUtc,
            suppression.ExpiresAtUtc,
            ToContract(suppression.State),
            suppression.Revision,
            suppression.ChangedByActorId,
            suppression.TransitionReason,
            suppression.ChangedAtUtc);
    }

    public static CatalogPublicVisibilitySuppressionChanged ToIntegrationEvent(
        Guid eventId,
        PublicVisibilitySuppression suppression)
    {
        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("Event ID is required.", nameof(eventId));
        }

        ArgumentNullException.ThrowIfNull(suppression);
        if (suppression.State == PublicVisibilitySuppressionState.Requested)
        {
            throw new CatalogInvariantException(
                "Requested visibility suppression is private Catalog workflow state and cannot be published to Query.");
        }

        return new CatalogPublicVisibilitySuppressionChanged(
            eventId,
            suppression.Id,
            suppression.CatalogKey.Value,
            ToContract(suppression.Target),
            suppression.PublicReasonClass,
            ToContract(suppression.ResponseMode),
            ToContract(suppression.State),
            suppression.StartsAtUtc,
            suppression.ExpiresAtUtc,
            suppression.Revision,
            suppression.ChangedAtUtc);
    }

    private static PublicVisibilitySuppressionTargetContract ToContract(
        PublicVisibilitySuppressionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new PublicVisibilitySuppressionTargetContract(
            ToContract(target.Kind),
            target.ListingId,
            target.TargetKey);
    }

    private static PublicVisibilitySuppressionTargetKind ToDomain(
        PublicVisibilitySuppressionTargetKindContract value)
    {
        return value switch
        {
            PublicVisibilitySuppressionTargetKindContract.Listing =>
                PublicVisibilitySuppressionTargetKind.Listing,
            PublicVisibilitySuppressionTargetKindContract.Media =>
                PublicVisibilitySuppressionTargetKind.Media,
            PublicVisibilitySuppressionTargetKindContract.Route =>
                PublicVisibilitySuppressionTargetKind.Route,
            PublicVisibilitySuppressionTargetKindContract.Contact =>
                throw new CatalogContractException(
                    "catalog.visibility_contact_identity_unsupported",
                    "Contact suppression requires a stable Catalog-owned public contact ID, which the current publication contract does not expose."),
            PublicVisibilitySuppressionTargetKindContract.ExternalReference =>
                throw new CatalogContractException(
                    "catalog.visibility_external_reference_identity_unsupported",
                    "External-reference suppression requires a stable Catalog-owned public external-reference ID, which the current publication contract does not expose."),
            _ => throw new CatalogContractException(
                "catalog.visibility_target_kind_unsupported",
                $"Visibility suppression target kind '{value}' is unsupported."),
        };
    }

    public static PublicVisibilitySuppressionResponseMode ToDomain(
        PublicVisibilitySuppressionResponseModeContract value)
    {
        return value switch
        {
            PublicVisibilitySuppressionResponseModeContract.HideAsNotFound =>
                PublicVisibilitySuppressionResponseMode.HideAsNotFound,
            PublicVisibilitySuppressionResponseModeContract.Gone =>
                PublicVisibilitySuppressionResponseMode.Gone,
            PublicVisibilitySuppressionResponseModeContract.TemporarilyUnavailable =>
                PublicVisibilitySuppressionResponseMode.TemporarilyUnavailable,
            PublicVisibilitySuppressionResponseModeContract.OmitChildElement =>
                PublicVisibilitySuppressionResponseMode.OmitChildElement,
            _ => throw new CatalogContractException(
                "catalog.visibility_response_mode_unsupported",
                $"Visibility suppression response mode '{value}' is unsupported."),
        };
    }

    private static PublicVisibilitySuppressionTargetKindContract ToContract(
        PublicVisibilitySuppressionTargetKind value)
    {
        return value switch
        {
            PublicVisibilitySuppressionTargetKind.Listing =>
                PublicVisibilitySuppressionTargetKindContract.Listing,
            PublicVisibilitySuppressionTargetKind.Media =>
                PublicVisibilitySuppressionTargetKindContract.Media,
            PublicVisibilitySuppressionTargetKind.Contact =>
                PublicVisibilitySuppressionTargetKindContract.Contact,
            PublicVisibilitySuppressionTargetKind.Route =>
                PublicVisibilitySuppressionTargetKindContract.Route,
            PublicVisibilitySuppressionTargetKind.ExternalReference =>
                PublicVisibilitySuppressionTargetKindContract.ExternalReference,
            _ => throw new CatalogInvariantException(
                $"Visibility suppression target kind '{value}' cannot be serialized."),
        };
    }

    private static PublicVisibilitySuppressionResponseModeContract ToContract(
        PublicVisibilitySuppressionResponseMode value)
    {
        return value switch
        {
            PublicVisibilitySuppressionResponseMode.HideAsNotFound =>
                PublicVisibilitySuppressionResponseModeContract.HideAsNotFound,
            PublicVisibilitySuppressionResponseMode.Gone =>
                PublicVisibilitySuppressionResponseModeContract.Gone,
            PublicVisibilitySuppressionResponseMode.TemporarilyUnavailable =>
                PublicVisibilitySuppressionResponseModeContract.TemporarilyUnavailable,
            PublicVisibilitySuppressionResponseMode.OmitChildElement =>
                PublicVisibilitySuppressionResponseModeContract.OmitChildElement,
            _ => throw new CatalogInvariantException(
                $"Visibility suppression response mode '{value}' cannot be serialized."),
        };
    }

    private static PublicVisibilitySuppressionStateContract ToContract(
        PublicVisibilitySuppressionState value)
    {
        return value switch
        {
            PublicVisibilitySuppressionState.Requested =>
                PublicVisibilitySuppressionStateContract.Requested,
            PublicVisibilitySuppressionState.Active =>
                PublicVisibilitySuppressionStateContract.Active,
            PublicVisibilitySuppressionState.Resolved =>
                PublicVisibilitySuppressionStateContract.Resolved,
            _ => throw new CatalogInvariantException(
                $"Visibility suppression state '{value}' cannot be serialized."),
        };
    }
}
