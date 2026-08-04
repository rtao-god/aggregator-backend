using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Domain;

namespace Ingestion.Application.Tests;

public sealed class IngestionCanonicalJsonTests
{
    [Fact]
    public void PersistedBatchSnapshotRoundTripsWithStableDigest()
    {
        var snapshot = new IngestionBatchSnapshot(
            ImportBatchId.Create(Guid.Parse("0198a123-0000-7000-8000-000000000401")),
            "collector-berlin",
            "build-2026-08-04",
            Guid.Parse("0198a123-0000-7000-8000-000000000402"),
            new string('a', 64),
            "berlin-recording",
            "berlin-recording-services",
            Guid.Parse("0198a123-0000-7000-8000-000000000403"),
            2,
            new string('b', 64),
            new string('c', 64),
            new string('d', 64),
            "ingestion/quarantine/package.json",
            new string('e', 64),
            4_096,
            "application/json",
            new DateTimeOffset(2026, 8, 4, 6, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 4, 6, 1, 0, TimeSpan.Zero),
            ImportBatchState.Registered,
            1,
            0,
            0,
            0,
            null);

        var document = IngestionCanonicalJson.Serialize(snapshot);
        var expectedDigest = IngestionCanonicalJson.ComputeDigest(document);
        var restored = IngestionCanonicalJson.Deserialize<IngestionBatchSnapshot>(document);
        var restoredDocument = IngestionCanonicalJson.Serialize(restored);

        Assert.Equal(snapshot, restored);
        Assert.Equal(expectedDigest, IngestionCanonicalJson.ComputeDigest(restoredDocument));
        Assert.Equal(document, restoredDocument);
    }

    [Fact]
    public void EmptyPersistedDocumentFailsWithOwnerContext()
    {
        var exception = Assert.Throws<IngestionApplicationException>(() =>
            IngestionCanonicalJson.Deserialize<IngestionBatchSnapshot>(ReadOnlySpan<byte>.Empty));

        Assert.Equal("Ingestion.Serialization", exception.Owner);
        Assert.Equal("INGESTION_DOCUMENT_EMPTY", exception.Code);
    }
}
