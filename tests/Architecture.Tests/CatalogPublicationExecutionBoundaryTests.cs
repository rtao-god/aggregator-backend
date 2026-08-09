using System.Xml.Linq;
using Xunit;

namespace Architecture.Tests;

public sealed class CatalogPublicationExecutionBoundaryTests
{
    [Fact]
    public void CatalogCommandApiCanOnlyEnqueuePublicationMaterialization()
    {
        var repository = RepositoryModel.Load();
        var controller = Read(repository, "src/Catalog/Catalog.Api/CatalogPublicationsController.cs");
        var operationController = Read(repository, "src/Catalog/Catalog.Api/CatalogOperationsController.cs");

        Assert.Contains("CatalogPublicationOperationService operationService", controller, StringComparison.Ordinal);
        Assert.Contains("operationService.EnqueueAsync(", controller, StringComparison.Ordinal);
        Assert.Contains("StatusCodes.Status202Accepted", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("publicationService.PublishAsync(", controller, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"{operationId:guid}\"", operationController, StringComparison.Ordinal);
        Assert.Contains("operationService.GetAsync(", operationController, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteNextAsync(", operationController, StringComparison.Ordinal);
        Assert.DoesNotContain("ClaimNextAsync(", operationController, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogWorkerOwnsPublicationExecutionComposition()
    {
        var repository = RepositoryModel.Load();
        var workerProjectPath = Path.Combine(
            repository.Root,
            "src",
            "Catalog",
            "Catalog.Worker",
            "Catalog.Worker.csproj");
        var workerProject = XDocument.Load(workerProjectPath);
        var references = workerProject
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value?.Replace('\\', '/'))
            .Where(value => value is not null)
            .Select(value => value!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var program = Read(repository, "src/Catalog/Catalog.Worker/Program.cs");
        var registration = Read(
            repository,
            "src/Catalog/Catalog.Worker/CatalogWorkerServiceCollectionExtensions.cs");
        var worker = Read(
            repository,
            "src/Catalog/Catalog.Worker/CatalogPublicationOperationWorker.cs");

        Assert.Contains("../Catalog.Application/Catalog.Application.csproj", references);
        Assert.Contains("../Catalog.Infrastructure/Catalog.Infrastructure.csproj", references);
        Assert.Contains("AddCatalogApplication()", program, StringComparison.Ordinal);
        Assert.Contains("AddCatalogInfrastructure(builder.Configuration)", program, StringComparison.Ordinal);
        Assert.Contains("AddHostedService<CatalogPublicationOperationWorker>()", registration, StringComparison.Ordinal);
        Assert.Contains("CatalogPublicationOperationExecutor", worker, StringComparison.Ordinal);
        Assert.Contains("ExecuteNextAsync(", worker, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicationActivationConsumesTheExactLeaseAtomically()
    {
        var repository = RepositoryModel.Load();
        var application = Read(
            repository,
            "src/Catalog/Catalog.Application/CatalogPublicationOperations.cs");
        var repositorySource = Read(
            repository,
            "src/Catalog/Catalog.Infrastructure/EfCatalogRepository.Publications.cs");
        var migration = Read(
            repository,
            "src/Catalog/Catalog.Migrations/Migrations/V013__catalog_publication_operation.sql");

        Assert.Contains("ICatalogPublicationOperationCommitter", application, StringComparison.Ordinal);
        Assert.Contains("ValidatePublicationOperationLease(operation, publication, completion)", repositorySource, StringComparison.Ordinal);
        Assert.Contains("operation.LeaseToken != completion.LeaseToken", repositorySource, StringComparison.Ordinal);
        Assert.Contains("operation.LeaseExpiresAtUtc <= completion.CompletedAtUtc", repositorySource, StringComparison.Ordinal);
        Assert.Contains("operation.State = (int)CatalogPublicationOperationState.Completed", repositorySource, StringComparison.Ordinal);
        Assert.Contains("operation.ResultPublicationId = publication.Id", repositorySource, StringComparison.Ordinal);
        Assert.Contains("AddOutbox(outboxMessage)", repositorySource, StringComparison.Ordinal);
        Assert.Contains("await _dbContext.SaveChangesAsync(cancellationToken)", repositorySource, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE catalog.publication_operation", migration, StringComparison.Ordinal);
        Assert.Contains("publication_operation_lease_consistent", migration, StringComparison.Ordinal);
        Assert.Contains("publication_operation_result_consistent", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogWorkerReceivesOnlyItsRequiredPublicationStorageBoundary()
    {
        var repository = RepositoryModel.Load();
        var compose = Read(repository, "compose.yaml");
        var workerStart = compose.IndexOf("  catalog-worker:", StringComparison.Ordinal);
        var nextService = compose.IndexOf("\n  catalog-media-worker:", workerStart, StringComparison.Ordinal);
        Assert.True(workerStart >= 0 && nextService > workerStart, "Catalog worker service block was not found.");
        var workerBlock = compose[workerStart..nextService];

        Assert.Contains("Catalog__ObjectStorage__ServiceUrl", workerBlock, StringComparison.Ordinal);
        Assert.Contains("Catalog__ObjectStorage__BucketName", workerBlock, StringComparison.Ordinal);
        Assert.Contains("seaweedfs: {condition: service_healthy}", workerBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("Ingestion__ObjectStorage", workerBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("CatalogMediaWorker__", workerBlock, StringComparison.Ordinal);
    }

    private static string Read(RepositoryModel repository, string relativePath) =>
        File.ReadAllText(Path.Combine(
            repository.Root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
