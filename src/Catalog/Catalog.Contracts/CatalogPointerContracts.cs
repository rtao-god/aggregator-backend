namespace Aggregator.Catalog.Contracts;

public enum PointerExpectationKindContract
{
    Absent = 1,
    Exact = 2,
}

public sealed record PublicationPointerExpectationContract(
    PointerExpectationKindContract Kind,
    Guid? PublicationId);

public sealed record ConfigurationPointerExpectationContract(
    PointerExpectationKindContract Kind,
    Guid? ConfigurationRevisionId);

public sealed record ActivateProductConfigurationRequest(
    Guid TargetConfigurationRevisionId,
    ConfigurationPointerExpectationContract ExpectedCurrent);

public sealed record CreateCatalogPublicationRequest(
    string CatalogKey,
    Guid ConfigurationRevisionId,
    PublicationPointerExpectationContract ExpectedCurrent,
    IReadOnlyList<PublicationSelectionContract> Selections);
