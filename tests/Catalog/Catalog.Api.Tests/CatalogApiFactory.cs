using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Aggregator.Catalog.Api;
using Aggregator.Catalog.Application;
using Aggregator.Catalog.Domain;
using Aggregator.Catalog.Media.Application;
using Aggregator.Catalog.Media.Domain;
using Aggregator.Catalog.Media.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Catalog.Api.Tests;

public sealed class CatalogApiFactory : WebApplicationFactory<Program>
{
    public const string AuthenticationHeader = "X-Test-Authenticated";
    public const string ActorHeader = "X-Test-Actor-Id";
    public const string ScopesHeader = "X-Test-Scopes";
    public static readonly Guid DefaultMediaAssetId =
        Guid.Parse("01980f00-0000-7000-8000-000000000001");
    private readonly CatalogApiPostgresDatabase _database = CatalogApiPostgresDatabase.Start();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Catalog", _database.ConnectionString);
        builder.UseSetting("Catalog:ObjectStorage:ServiceUrl", "http://127.0.0.1:9000");
        builder.UseSetting("Catalog:ObjectStorage:Region", "us-east-1");
        builder.UseSetting("Catalog:ObjectStorage:Bucket", "catalog-publications");
        builder.UseSetting("Catalog:ObjectStorage:AccessKey", "catalog-access");
        builder.UseSetting("Catalog:ObjectStorage:SecretKey", "catalog-secret");
        builder.UseSetting("Catalog:ObjectStorage:ForcePathStyle", "true");
        builder.UseSetting("Catalog:ObjectStorage:MaximumPublicationBytes", "1048576");
        builder.UseSetting("CatalogMedia:ObjectStorage:ServiceUrl", "http://127.0.0.1:9000");
        builder.UseSetting("CatalogMedia:ObjectStorage:Region", "us-east-1");
        builder.UseSetting("CatalogMedia:ObjectStorage:Bucket", "catalog-media");
        builder.UseSetting("CatalogMedia:ObjectStorage:AccessKey", "catalog-media-access");
        builder.UseSetting("CatalogMedia:ObjectStorage:SecretKey", "catalog-media-secret");
        builder.UseSetting("CatalogMedia:ObjectStorage:ForcePathStyle", "true");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICatalogPublicationOperationStore>();
            services.AddSingleton<ICatalogPublicationOperationStore, TestCatalogPublicationOperationStore>();
            services.RemoveAll<ICatalogListingDisputeRepository>();
            services.AddSingleton<ICatalogListingDisputeRepository, TestCatalogListingDisputeRepository>();
            services.RemoveAll<ICatalogMediaStore>();
            services.AddSingleton<ICatalogMediaStore, TestCatalogMediaStore>();
            services.RemoveAll<ICatalogMediaObjectStore>();
            services.AddSingleton<ICatalogMediaObjectStore, TestCatalogMediaObjectStore>();
            services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.Scheme;
                    options.DefaultChallengeScheme = TestAuthenticationHandler.Scheme;
                    options.DefaultForbidScheme = TestAuthenticationHandler.Scheme;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.Scheme,
                    _ => { });
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _database.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed class TestCatalogListingDisputeRepository : ICatalogListingDisputeRepository
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<
            Guid,
            ListingDispute> _disputes = new();

        public Task<ListingDispute> AddAsync(
            ListingDispute dispute,
            long expectedListingVersion,
            CatalogEventContext eventContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(eventContext);
            if (expectedListingVersion <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(expectedListingVersion));
            }

            if (_disputes.Values.Any(candidate =>
                    candidate.ListingId == dispute.ListingId &&
                    candidate.State == ListingDisputeState.Open))
            {
                throw new CatalogConflictException(
                    $"Listing '{dispute.ListingId}' already has an open dispute.");
            }

            if (!_disputes.TryAdd(dispute.Id, dispute))
            {
                throw new CatalogConflictException(
                    $"Listing dispute '{dispute.Id}' already exists.");
            }

            return Task.FromResult(dispute);
        }

        public Task<ListingDispute?> GetAsync(
            Guid listingId,
            Guid disputeId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _disputes.TryGetValue(disputeId, out var dispute);
            return Task.FromResult(
                dispute is not null && dispute.ListingId == listingId
                    ? dispute
                    : null);
        }

        public Task<ListingDispute> SaveAsync(
            ListingDispute dispute,
            long expectedStoredAggregateRevision,
            CatalogEventContext eventContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(eventContext);
            if (!_disputes.TryGetValue(dispute.Id, out var current))
            {
                throw new CatalogNotFoundException("listing-dispute", dispute.Id);
            }

            if (current.AggregateRevision != dispute.AggregateRevision ||
                expectedStoredAggregateRevision != dispute.AggregateRevision - 1)
            {
                throw new CatalogListingDisputeConcurrencyException(
                    dispute.Id,
                    expectedStoredAggregateRevision,
                    current.AggregateRevision);
            }

            _disputes[dispute.Id] = dispute;
            return Task.FromResult(dispute);
        }
    }

    private sealed class TestCatalogPublicationOperationStore : ICatalogPublicationOperationStore
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<
            Guid,
            CatalogPublicationOperationSnapshot> _operations = new();

        public Task<CatalogPublicationOperationSnapshot> RegisterAsync(
            CatalogPublicationOperationRegistration registration,
            CancellationToken cancellationToken)
        {
            var operation = new CatalogPublicationOperationSnapshot(
                registration.OperationId,
                registration.PublicationId,
                1,
                registration.CatalogKey,
                registration.ActorId,
                CatalogPublicationOperationState.Pending,
                0,
                registration.CreatedAtUtc,
                registration.CreatedAtUtc,
                null,
                null,
                null);
            _operations[operation.OperationId] = operation;
            return Task.FromResult(operation);
        }

        public Task<CatalogPublicationOperationSnapshot?> GetAsync(
            Guid operationId,
            CancellationToken cancellationToken)
        {
            _operations.TryGetValue(operationId, out var operation);
            return Task.FromResult(operation);
        }

        public Task<CatalogPublicationOperationLease?> ClaimNextAsync(
            string workerIdentity,
            DateTimeOffset claimedAtUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ScheduleRetryAsync(
            Guid operationId,
            Guid leaseToken,
            CatalogPublicationOperationFailure failure,
            DateTimeOffset nextAttemptAtUtc,
            DateTimeOffset updatedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task FailAsync(
            Guid operationId,
            Guid leaseToken,
            CatalogPublicationOperationFailure failure,
            DateTimeOffset failedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Scheme = "CatalogApiTests";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(AuthenticationHeader, out var value) || value != "true")
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, "catalog-api-tests"),
            };
            if (Request.Headers.TryGetValue(ActorHeader, out var actorId))
            {
                claims.Add(new Claim("actor_id", actorId.ToString()));
            }

            if (Request.Headers.TryGetValue(ScopesHeader, out var scopes))
            {
                claims.Add(new Claim("scope", scopes.ToString()));
            }

            var identity = new ClaimsIdentity(claims, Scheme);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, Scheme)));
        }
    }
}

internal sealed class CatalogApiPostgresDatabase : IDisposable
{
    public static CatalogApiPostgresDatabase Start() =>
        new("Host=127.0.0.1;Port=1;Database=catalog-api-tests;Username=test;Password=test;Timeout=1;Command Timeout=1");

    private CatalogApiPostgresDatabase(string connectionString)
    {
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public void Dispose()
    {
    }
}

internal sealed class TestCatalogMediaStore : ICatalogMediaStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CatalogMediaAsset> _assets = new();

    public Task AddAsync(CatalogMediaAsset asset, string idempotencyKey, CancellationToken cancellationToken)
    {
        _assets[asset.Id] = asset;
        return Task.CompletedTask;
    }

    public Task<CatalogMediaAsset?> GetAsync(Guid assetId, CancellationToken cancellationToken)
    {
        _assets.TryGetValue(assetId, out var asset);
        return Task.FromResult(asset);
    }

    public Task<IReadOnlyList<CatalogMediaWorkLease>> LeasePendingAsync(
        string workerIdentity,
        int limit,
        DateTimeOffset leasedAtUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CatalogMediaWorkLease>>([]);

    public Task SaveAsync(CatalogMediaAsset asset, CatalogMediaWorkLease? lease, string? idempotencyKey, CancellationToken cancellationToken)
    {
        _assets[asset.Id] = asset;
        return Task.CompletedTask;
    }

    public Task<CatalogMediaAsset?> GetForPublicationAsync(
        Guid assetId,
        long aggregateRevision,
        Guid variantId,
        CancellationToken cancellationToken)
    {
        _assets.TryGetValue(assetId, out var asset);
        return Task.FromResult(
            asset is not null &&
            asset.AggregateRevision == aggregateRevision &&
            asset.Variants.Any(variant => variant.Id == variantId)
                ? asset
                : null);
    }
}

internal sealed class TestCatalogMediaObjectStore : ICatalogMediaObjectStore
{
    public Task<CatalogMediaObjectMetadata> ReadMetadataAsync(
        string objectKey,
        CancellationToken cancellationToken) =>
        Task.FromResult(new CatalogMediaObjectMetadata(
            objectKey,
            "image/jpeg",
            1,
            Convert.ToHexString(SHA256.HashData([1])).ToLowerInvariant()));

    public Task<byte[]> ReadAsync(string objectKey, CancellationToken cancellationToken) =>
        Task.FromResult<byte[]>([1]);

    public Task PutAsync(
        string objectKey,
        ReadOnlyMemory<byte> content,
        string contentType,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task MoveAsync(
        string sourceObjectKey,
        string destinationObjectKey,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
