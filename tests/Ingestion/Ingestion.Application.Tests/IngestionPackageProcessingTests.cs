using System.Text.Json;
using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Contracts;
using Aggregator.Ingestion.Domain;

namespace Ingestion.Application.Tests;

public sealed class IngestionPackageProcessingTests
{
    [Fact]
    public async Task ExactPayloadProducesAcceptedReviewAndRejectedDecisionsAtomically()
    {
        var fixture = CreateFixture();
        var repository = new PackageWorkRepository(fixture.Claim);
        var service = new IngestionPackageProcessingService(
            repository,
            new ExactObjectReader(fixture.PayloadBytes),
            new IngestionPackagePayloadValidator(),
            new FixedClock(fixture.Timestamp.AddMinutes(1)),
            new IngestionPackageProcessingOptions());

        var result = await service.ProcessNextAsync(
            "worker:package-validation",
            CancellationToken.None);

        Assert.Equal(IngestionPackageProcessOutcome.Completed, result.Outcome);
        Assert.Null(result.FailureCode);
        Assert.NotNull(repository.CompletedBatch);
        Assert.NotNull(repository.Validation);
        Assert.Equal(ImportBatchState.ReviewRequired, repository.CompletedBatch.State);
        Assert.Equal(1, repository.CompletedBatch.AcceptedItemCount);
        Assert.Equal(1, repository.CompletedBatch.ReviewRequiredItemCount);
        Assert.Equal(1, repository.CompletedBatch.RejectedItemCount);
        Assert.Equal(3, repository.Validation.Items.Count);
        Assert.Contains(
            repository.Validation.Items,
            item => item.ItemKey == "accepted-item" &&
                item.Decision == IngestionPackageDecisionKind.Accepted &&
                item.ReasonCodes.SequenceEqual(["validation.accepted"]));
        Assert.Contains(
            repository.Validation.Items,
            item => item.ItemKey == "review-item" &&
                item.Decision == IngestionPackageDecisionKind.NeedsReview &&
                item.ReasonCodes.Contains("provenance.internal_review_only", StringComparer.Ordinal));
        Assert.Contains(
            repository.Validation.Items,
            item => item.ItemKey == "rejected-item" &&
                item.Decision == IngestionPackageDecisionKind.Rejected &&
                item.ReasonCodes.Contains("provenance.forbidden", StringComparer.Ordinal));
        Assert.Null(repository.FailedBatch);
    }

    [Fact]
    public async Task CorruptedPayloadFailsWholePackageWithoutPartialCompletion()
    {
        var fixture = CreateFixture();
        var corrupted = fixture.PayloadBytes.ToArray();
        corrupted[^1] ^= 0x01;
        var repository = new PackageWorkRepository(fixture.Claim);
        var service = new IngestionPackageProcessingService(
            repository,
            new ExactObjectReader(corrupted),
            new IngestionPackagePayloadValidator(),
            new FixedClock(fixture.Timestamp.AddMinutes(1)),
            new IngestionPackageProcessingOptions());

        var result = await service.ProcessNextAsync(
            "worker:package-validation",
            CancellationToken.None);

        Assert.Equal(IngestionPackageProcessOutcome.IntegrityRejected, result.Outcome);
        Assert.Equal("INGESTION_PAYLOAD_OBJECT_DIGEST_MISMATCH", result.FailureCode);
        Assert.NotNull(repository.FailedBatch);
        Assert.Equal(ImportBatchState.IntegrityFailed, repository.FailedBatch.State);
        Assert.Null(repository.CompletedBatch);
        Assert.Null(repository.Validation);
    }

    [Fact]
    public void DuplicateItemIdentityRejectsCompletePayload()
    {
        var fixture = CreateFixture(duplicateFirstItem: true);
        var validator = new IngestionPackagePayloadValidator();

        var exception = Assert.Throws<IngestionPackageIntegrityException>(() =>
            validator.Validate(fixture.Claim.Batch, fixture.PayloadBytes));

        Assert.Equal("INGESTION_ITEM_KEY_DUPLICATE", exception.Code);
    }

    [Fact]
    public void LinkOnlyEvidenceCannotSupportPublicContentField()
    {
        var fixture = CreateFixture(linkOnlyPublicField: true);
        var result = new IngestionPackagePayloadValidator().Validate(
            fixture.Claim.Batch,
            fixture.PayloadBytes);
        var item = Assert.Single(result.Items, value => value.ItemKey == "accepted-item");

        Assert.Equal(IngestionPackageDecisionKind.Rejected, item.Decision);
        Assert.Contains("provenance.link_only_field_forbidden", item.ReasonCodes);
    }

    private static PackageFixture CreateFixture(
        bool duplicateFirstItem = false,
        bool linkOnlyPublicField = false)
    {
        var timestamp = new DateTimeOffset(2026, 8, 4, 9, 0, 0, TimeSpan.Zero);
        var exportId = Guid.Parse("0198a123-3000-7000-8000-000000000001");
        var manifestDigest = new string('a', 64);
        var items = new[]
        {
            CreateItem(
                "accepted-item",
                ordinal: 0,
                linkOnlyPublicField
                    ? new IngestionPackageEvidenceContract(
                        "name",
                        "source-public",
                        IngestionPackageUsagePolicyContract.LinkOnly,
                        "https://example.test/accepted",
                        timestamp,
                        new string('1', 64))
                    : new IngestionPackageEvidenceContract(
                        "name",
                        "source-public",
                        IngestionPackageUsagePolicyContract.PublicAllowed,
                        "https://example.test/accepted",
                        timestamp,
                        new string('1', 64)),
                qualityIssues: []),
            CreateItem(
                duplicateFirstItem ? "accepted-item" : "review-item",
                ordinal: 1,
                new IngestionPackageEvidenceContract(
                    "name",
                    "source-review",
                    IngestionPackageUsagePolicyContract.InternalReviewOnly,
                    "https://example.test/review",
                    timestamp,
                    new string('2', 64)),
                [
                    new IngestionPackageQualityIssueContract(
                        "address_uncertain",
                        IngestionPackageQualitySeverityContract.Warning,
                        "Address requires an operator check."),
                ]),
            CreateItem(
                "rejected-item",
                ordinal: 2,
                new IngestionPackageEvidenceContract(
                    "name",
                    "source-forbidden",
                    IngestionPackageUsagePolicyContract.Forbidden,
                    "https://example.test/rejected",
                    timestamp,
                    new string('3', 64)),
                [
                    new IngestionPackageQualityIssueContract(
                        "identity_conflict",
                        IngestionPackageQualitySeverityContract.Blocking,
                        "Candidate identity conflicts with the package source."),
                ]),
        };
        var index = items
            .OrderBy(item => item.Ordinal)
            .Select(item => new IngestionPackageIndexEntryContract(
                item.ItemKey,
                item.Ordinal,
                item.ContentDigest))
            .ToArray();
        var itemIndexDigest = IngestionCanonicalJson.ComputeDigest(index);
        var payload = new AggregatorCandidatePayload(
            AggregatorCandidatePayloadContract.Identity,
            AggregatorCandidatePayloadContract.Revision,
            exportId,
            manifestDigest,
            items);
        var payloadBytes = IngestionCanonicalJson.Serialize(payload);
        var payloadDigest = IngestionDocumentDigest.Compute(payloadBytes);
        var batch = ImportBatch.Create(
            ImportBatchId.Create(Guid.Parse("0198a123-3000-7000-8000-000000000002")),
            "collector-berlin",
            "collector-build-1",
            exportId,
            new string('b', 64),
            "berlin",
            "providers",
            Guid.Parse("0198a123-3000-7000-8000-000000000003"),
            items.Length,
            manifestDigest,
            itemIndexDigest,
            payloadDigest,
            "ingestion/quarantine/package.json",
            payloadDigest,
            payloadBytes.Length,
            "application/json",
            timestamp);
        batch.BeginUpload(batch.AggregateRevision, timestamp);
        batch.MarkUploaded(payloadDigest, payloadBytes.Length, batch.AggregateRevision, timestamp);
        batch.BeginIntegrityCheck(batch.AggregateRevision, timestamp);
        var claim = new IngestionPackageWorkClaim(
            Guid.Parse("0198a123-3000-7000-8000-000000000004"),
            AttemptNumber: 1,
            "worker:package-validation",
            timestamp.AddMinutes(5),
            IngestionBatchSnapshot.From(batch));
        return new PackageFixture(timestamp, payloadBytes, claim);
    }

    private static AggregatorCandidatePayloadItem CreateItem(
        string itemKey,
        int ordinal,
        IngestionPackageEvidenceContract evidence,
        IReadOnlyList<IngestionPackageQualityIssueContract> qualityIssues)
    {
        var candidate = JsonSerializer.SerializeToElement(new
        {
            name = itemKey,
            externalReference = $"https://example.test/items/{itemKey}",
        });
        var evidenceValues = new[] { evidence };
        var canonicalDocument = IngestionCanonicalJson.Serialize(new
        {
            ItemKey = itemKey,
            Ordinal = ordinal,
            EntityKind = IngestionPackageEntityKindContract.Provider,
            Candidate = candidate,
            Evidence = evidenceValues
                .OrderBy(value => value.Field, StringComparer.Ordinal)
                .ThenBy(value => value.SourceKey, StringComparer.Ordinal)
                .ThenBy(value => value.Locator, StringComparer.Ordinal)
                .ThenBy(value => value.EvidenceDigest, StringComparer.Ordinal)
                .ToArray(),
            QualityIssues = qualityIssues
                .OrderBy(value => value.Code, StringComparer.Ordinal)
                .ThenBy(value => value.Detail, StringComparer.Ordinal)
                .ToArray(),
        });
        return new AggregatorCandidatePayloadItem(
            itemKey,
            ordinal,
            IngestionPackageEntityKindContract.Provider,
            IngestionDocumentDigest.Compute(canonicalDocument),
            candidate,
            evidenceValues,
            qualityIssues);
    }

    private sealed record PackageFixture(
        DateTimeOffset Timestamp,
        byte[] PayloadBytes,
        IngestionPackageWorkClaim Claim);

    private sealed class FixedClock(DateTimeOffset value) : IIngestionClock
    {
        public DateTimeOffset GetUtcNow() => value;
    }

    private sealed class ExactObjectReader(byte[] bytes) : IIngestionPackageObjectReader
    {
        private readonly byte[] _bytes = bytes;

        public Task<byte[]> ReadExactAsync(
            string objectKey,
            string expectedDigest,
            long expectedSize,
            long maximumSize,
            CancellationToken cancellationToken)
        {
            Assert.Equal("ingestion/quarantine/package.json", objectKey);
            Assert.True(expectedSize <= maximumSize);
            return Task.FromResult(_bytes.ToArray());
        }
    }

    private sealed class PackageWorkRepository(IngestionPackageWorkClaim claim) : IIngestionPackageWorkRepository
    {
        private IngestionPackageWorkClaim? _claim = claim;

        public ImportBatch? CompletedBatch { get; private set; }

        public IngestionPackageValidationResult? Validation { get; private set; }

        public ImportBatch? FailedBatch { get; private set; }

        public Task<IngestionPackageWorkClaim?> ClaimNextAsync(
            string workerIdentity,
            DateTimeOffset leasedAtUtc,
            TimeSpan leaseLifetime,
            CancellationToken cancellationToken)
        {
            var value = _claim;
            _claim = null;
            return Task.FromResult(value);
        }

        public Task CompleteAsync(
            IngestionPackageWorkClaim workClaim,
            ImportBatch batch,
            IngestionPackageValidationResult validation,
            CancellationToken cancellationToken)
        {
            Assert.Null(CompletedBatch);
            Assert.Null(FailedBatch);
            CompletedBatch = batch;
            Validation = validation;
            return Task.CompletedTask;
        }

        public Task FailIntegrityAsync(
            IngestionPackageWorkClaim workClaim,
            ImportBatch batch,
            string failureCode,
            CancellationToken cancellationToken)
        {
            Assert.Null(CompletedBatch);
            Assert.Null(FailedBatch);
            Assert.Equal(failureCode, batch.FailureCode);
            FailedBatch = batch;
            return Task.CompletedTask;
        }
    }
}
