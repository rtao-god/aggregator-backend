using System.Security.Cryptography;
using System.Text;
using Aggregator.Catalog.Application;
using Aggregator.Catalog.Infrastructure;

namespace Catalog.Infrastructure.Tests;

public sealed class CatalogPublicationOperationPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid ActorId = Guid.Parse("0198a222-0000-7000-8000-000000000001");

    [Fact]
    public async Task RegistrationReplayKeepsExactOwnerIdentityAndRejectsDivergentPayload()
    {
        await using var database = await CatalogPostgresTestDatabase.CreateAsync();
        await database.ApplyAllCatalogMigrationsAsync();
        await using var context = database.CreateContext();
        var store = new PostgresCatalogPublicationOperationStore(context);
        var registration = CreateRegistration(
            Guid.Parse("0198a222-0000-7000-8000-000000000002"),
            Guid.Parse("0198a222-0000-7000-8000-000000000003"),
            "publication-idempotency-0001",
            "request-one");

        var first = await store.RegisterAsync(registration, CancellationToken.None);
        var replay = await store.RegisterAsync(
            CreateRegistration(
                Guid.Parse("0198a222-0000-7000-8000-000000000004"),
                Guid.Parse("0198a222-0000-7000-8000-000000000005"),
                registration.IdempotencyKey,
                "request-one"),
            CancellationToken.None);

        Assert.Equal(first.OperationId, replay.OperationId);
        Assert.Equal(first.PlannedPublicationId, replay.PlannedPublicationId);
        Assert.Equal(first.PlannedPublicationSequence, replay.PlannedPublicationSequence);
        Assert.Equal(CatalogPublicationOperationState.Pending, replay.State);

        var conflict = await Assert.ThrowsAsync<CatalogConflictException>(() =>
            store.RegisterAsync(
                CreateRegistration(
                    Guid.Parse("0198a222-0000-7000-8000-000000000006"),
                    Guid.Parse("0198a222-0000-7000-8000-000000000007"),
                    registration.IdempotencyKey,
                    "request-two"),
                CancellationToken.None));
        Assert.Contains("different Catalog publication request", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentClaimProducesOneExclusiveLease()
    {
        await using var database = await CatalogPostgresTestDatabase.CreateAsync();
        await database.ApplyAllCatalogMigrationsAsync();
        await using (var registrationContext = database.CreateContext())
        {
            var registrationStore = new PostgresCatalogPublicationOperationStore(registrationContext);
            _ = await registrationStore.RegisterAsync(
                CreateRegistration(
                    Guid.Parse("0198a222-0000-7000-8000-000000000010"),
                    Guid.Parse("0198a222-0000-7000-8000-000000000011"),
                    "publication-idempotency-0002",
                    "claim-request"),
                CancellationToken.None);
        }

        await using var firstContext = database.CreateContext();
        await using var secondContext = database.CreateContext();
        var firstStore = new PostgresCatalogPublicationOperationStore(firstContext);
        var secondStore = new PostgresCatalogPublicationOperationStore(secondContext);

        var claims = await Task.WhenAll(
            firstStore.ClaimNextAsync(
                "catalog-worker-a",
                Now,
                TimeSpan.FromMinutes(5),
                CancellationToken.None),
            secondStore.ClaimNextAsync(
                "catalog-worker-b",
                Now,
                TimeSpan.FromMinutes(5),
                CancellationToken.None));

        var lease = Assert.IsType<CatalogPublicationOperationLease>(
            Assert.Single(claims.Where(candidate => candidate is not null)));
        Assert.Equal(1, lease.Attempt);
        Assert.NotEqual(Guid.Empty, lease.LeaseToken);
        Assert.Single(claims.Where(candidate => candidate is null));
    }

    [Fact]
    public async Task ExpiredLeaseCanBeReclaimedAndOldWorkerCannotMutateOperation()
    {
        await using var database = await CatalogPostgresTestDatabase.CreateAsync();
        await database.ApplyAllCatalogMigrationsAsync();
        Guid operationId;
        await using (var registrationContext = database.CreateContext())
        {
            var registrationStore = new PostgresCatalogPublicationOperationStore(registrationContext);
            var operation = await registrationStore.RegisterAsync(
                CreateRegistration(
                    Guid.Parse("0198a222-0000-7000-8000-000000000020"),
                    Guid.Parse("0198a222-0000-7000-8000-000000000021"),
                    "publication-idempotency-0003",
                    "lease-request"),
                CancellationToken.None);
            operationId = operation.OperationId;
        }

        await using var oldContext = database.CreateContext();
        var oldStore = new PostgresCatalogPublicationOperationStore(oldContext);
        var oldLease = Assert.IsType<CatalogPublicationOperationLease>(await oldStore.ClaimNextAsync(
            "catalog-worker-old",
            Now,
            TimeSpan.FromMinutes(1),
            CancellationToken.None));

        await using var replacementContext = database.CreateContext();
        var replacementStore = new PostgresCatalogPublicationOperationStore(replacementContext);
        var replacementTime = Now.AddMinutes(2);
        var replacementLease = Assert.IsType<CatalogPublicationOperationLease>(
            await replacementStore.ClaimNextAsync(
                "catalog-worker-replacement",
                replacementTime,
                TimeSpan.FromMinutes(5),
                CancellationToken.None));

        Assert.Equal(operationId, replacementLease.OperationId);
        Assert.Equal(2, replacementLease.Attempt);
        Assert.NotEqual(oldLease.LeaseToken, replacementLease.LeaseToken);

        await Assert.ThrowsAsync<CatalogPublicationOperationLeaseLostException>(() =>
            oldStore.FailAsync(
                oldLease.OperationId,
                oldLease.LeaseToken,
                CatalogPublicationOperationFailure.Create(
                    "Catalog.Publications",
                    "OLD_WORKER_FAILURE",
                    "The expired worker must not own the operation.",
                    "No action is required for the replacement lease."),
                replacementTime.AddSeconds(1),
                CancellationToken.None));

        await replacementStore.FailAsync(
            replacementLease.OperationId,
            replacementLease.LeaseToken,
            CatalogPublicationOperationFailure.Create(
                "Catalog.Publications",
                "TERMINAL_TEST_FAILURE",
                "The replacement worker owns this terminal test transition.",
                "Inspect the retained test operation."),
            replacementTime.AddSeconds(2),
            CancellationToken.None);
        var snapshot = Assert.IsType<CatalogPublicationOperationSnapshot>(
            await replacementStore.GetAsync(operationId, CancellationToken.None));
        Assert.Equal(CatalogPublicationOperationState.Failed, snapshot.State);
        Assert.Equal("TERMINAL_TEST_FAILURE", snapshot.Failure?.Code);
    }

    private static CatalogPublicationOperationRegistration CreateRegistration(
        Guid operationId,
        Guid publicationId,
        string idempotencyKey,
        string request)
    {
        var document = Encoding.UTF8.GetBytes(request);
        return new CatalogPublicationOperationRegistration(
            operationId,
            publicationId,
            "catalog",
            ActorId,
            idempotencyKey,
            document,
            Convert.ToHexString(SHA256.HashData(document)).ToLowerInvariant(),
            $"correlation-{operationId:N}",
            null,
            Now);
    }
}
