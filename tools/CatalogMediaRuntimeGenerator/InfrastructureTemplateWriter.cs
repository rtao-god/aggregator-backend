using System.Reflection;

internal static class InfrastructureTemplateWriter
{
    public static void Write(CatalogMediaGenerationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Directory.CreateDirectory(context.InfrastructureDirectory);
        WriteFile(
            context.InfrastructureDirectory,
            "Catalog.Media.Infrastructure.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.EntityFrameworkCore" />
                <PackageReference Include="Npgsql" />
                <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="../Catalog.Media.Domain/Catalog.Media.Domain.csproj" />
                <ProjectReference Include="../Catalog.Media.Contracts/Catalog.Media.Contracts.csproj" />
                <ProjectReference Include="../Catalog.Media.Application/Catalog.Media.Application.csproj" />
                <ProjectReference Include="../../BuildingBlocks/Platform.ObjectStorage/Platform.ObjectStorage.csproj" />
              </ItemGroup>
            </Project>
            """);
        WriteFile(context.InfrastructureDirectory, "CatalogMediaRows.cs", Rows());
        WriteFile(context.InfrastructureDirectory, "CatalogMediaDbContext.cs", DbContext());
        WriteFile(context.InfrastructureDirectory, "CatalogMediaPersistenceJson.cs", PersistenceJson());
        WriteFile(
            context.InfrastructureDirectory,
            "ObjectStoreCatalogMediaStore.cs",
            ObjectStoreAdapter(context.Descriptor, context.Upload));
        WriteFile(
            context.InfrastructureDirectory,
            "CatalogMediaInfrastructureServiceCollectionExtensions.cs",
            ServiceCollection());
        CatalogMediaRepositoryTemplateWriter.Write(context);
    }

    private static string Rows() =>
        """
        namespace Aggregator.CatalogMedia.Infrastructure;

        internal sealed class CatalogMediaAssetRow
        {
            public Guid Id { get; set; }
            public required string CatalogKey { get; set; }
            public int State { get; set; }
            public required string QuarantineObjectKey { get; set; }
            public required string ExpectedContentType { get; set; }
            public required string ExpectedContentDigest { get; set; }
            public long ExpectedSize { get; set; }
            public int RightsBasis { get; set; }
            public required string RightsReference { get; set; }
            public DateTimeOffset RegisteredAtUtc { get; set; }
            public DateTimeOffset ChangedAtUtc { get; set; }
            public long AggregateRevision { get; set; }
            public DateTimeOffset? UploadAuthorizationExpiresAtUtc { get; set; }
            public DateTimeOffset? UploadedAtUtc { get; set; }
            public DateTimeOffset? ScannedAtUtc { get; set; }
            public DateTimeOffset? AcceptedAtUtc { get; set; }
            public DateTimeOffset? RightsRevokedAtUtc { get; set; }
            public Guid? RightsRevokedByActorId { get; set; }
            public string? FailureCode { get; set; }
        }

        internal sealed class CatalogMediaVariantRow
        {
            public Guid Id { get; set; }
            public Guid AssetId { get; set; }
            public int Kind { get; set; }
            public required string ObjectKey { get; set; }
            public required string ContentType { get; set; }
            public required string ContentDigest { get; set; }
            public long Size { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public DateTimeOffset CreatedAtUtc { get; set; }
        }

        internal sealed class CatalogMediaCommandRow
        {
            public required string Scope { get; set; }
            public required string IdempotencyKey { get; set; }
            public required string RequestDigest { get; set; }
            public Guid AssetId { get; set; }
            public required byte[] ResultDocument { get; set; }
            public required string ResultDigest { get; set; }
            public Guid ActorId { get; set; }
            public required string CorrelationId { get; set; }
            public DateTimeOffset CreatedAtUtc { get; set; }
        }

        internal sealed class CatalogMediaProcessingWorkRow
        {
            public Guid AssetId { get; set; }
            public Guid? LeaseToken { get; set; }
            public string? LeasedBy { get; set; }
            public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
            public int AttemptCount { get; set; }
            public string? LastError { get; set; }
            public DateTimeOffset? LastFailedAtUtc { get; set; }
            public DateTimeOffset? CompletedAtUtc { get; set; }
        }

        internal sealed class CatalogMediaOutboxRow
        {
            public Guid MessageId { get; set; }
            public required string RoutingKey { get; set; }
            public required string ContractIdentity { get; set; }
            public required string PayloadJson { get; set; }
            public required string PayloadDigest { get; set; }
            public DateTimeOffset OccurredAtUtc { get; set; }
            public required string CorrelationId { get; set; }
            public Guid? CausationId { get; set; }
            public Guid? LeaseToken { get; set; }
            public string? LeasedBy { get; set; }
            public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
            public int DeliveryAttempts { get; set; }
            public DateTimeOffset? DispatchedAtUtc { get; set; }
            public string? LastError { get; set; }
            public DateTimeOffset? DeadLetteredAtUtc { get; set; }
            public string? DeadLetterReason { get; set; }
        }
        """;

    private static string DbContext() =>
        """
        using System.Text;
        using Microsoft.EntityFrameworkCore;

        namespace Aggregator.CatalogMedia.Infrastructure;

        public sealed class CatalogMediaDbContext(DbContextOptions<CatalogMediaDbContext> options) : DbContext(options)
        {
            internal DbSet<CatalogMediaAssetRow> Assets => Set<CatalogMediaAssetRow>();
            internal DbSet<CatalogMediaVariantRow> Variants => Set<CatalogMediaVariantRow>();
            internal DbSet<CatalogMediaCommandRow> Commands => Set<CatalogMediaCommandRow>();
            internal DbSet<CatalogMediaProcessingWorkRow> ProcessingWork => Set<CatalogMediaProcessingWorkRow>();
            internal DbSet<CatalogMediaOutboxRow> OutboxMessages => Set<CatalogMediaOutboxRow>();

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                ArgumentNullException.ThrowIfNull(modelBuilder);
                modelBuilder.Entity<CatalogMediaAssetRow>(entity =>
                {
                    entity.ToTable("asset", "media");
                    entity.HasKey(row => row.Id);
                    entity.Property(row => row.CatalogKey).HasMaxLength(120);
                    entity.Property(row => row.QuarantineObjectKey).HasMaxLength(1024);
                    entity.Property(row => row.ExpectedContentType).HasMaxLength(128);
                    entity.Property(row => row.ExpectedContentDigest).HasMaxLength(64);
                    entity.Property(row => row.RightsReference).HasMaxLength(4000);
                    entity.Property(row => row.FailureCode).HasMaxLength(120);
                    entity.Property(row => row.AggregateRevision).IsConcurrencyToken();
                    entity.HasIndex(row => row.QuarantineObjectKey).IsUnique();
                    entity.HasIndex(row => new { row.CatalogKey, row.State, row.RegisteredAtUtc });
                });
                modelBuilder.Entity<CatalogMediaVariantRow>(entity =>
                {
                    entity.ToTable("variant", "media");
                    entity.HasKey(row => row.Id);
                    entity.Property(row => row.ObjectKey).HasMaxLength(1024);
                    entity.Property(row => row.ContentType).HasMaxLength(128);
                    entity.Property(row => row.ContentDigest).HasMaxLength(64);
                    entity.HasIndex(row => new { row.AssetId, row.Kind }).IsUnique();
                    entity.HasIndex(row => row.ObjectKey).IsUnique();
                    entity.HasOne<CatalogMediaAssetRow>()
                        .WithMany()
                        .HasForeignKey(row => row.AssetId)
                        .OnDelete(DeleteBehavior.Restrict);
                });
                modelBuilder.Entity<CatalogMediaCommandRow>(entity =>
                {
                    entity.ToTable("command_result", "operations");
                    entity.HasKey(row => new { row.Scope, row.IdempotencyKey });
                    entity.Property(row => row.Scope).HasMaxLength(180);
                    entity.Property(row => row.IdempotencyKey).HasMaxLength(200);
                    entity.Property(row => row.RequestDigest).HasMaxLength(64);
                    entity.Property(row => row.ResultDocument).HasColumnType("bytea");
                    entity.Property(row => row.ResultDigest).HasMaxLength(64);
                    entity.Property(row => row.CorrelationId).HasMaxLength(128);
                    entity.HasIndex(row => row.AssetId);
                    entity.HasOne<CatalogMediaAssetRow>()
                        .WithMany()
                        .HasForeignKey(row => row.AssetId)
                        .OnDelete(DeleteBehavior.Restrict);
                });
                modelBuilder.Entity<CatalogMediaProcessingWorkRow>(entity =>
                {
                    entity.ToTable("processing_work", "operations");
                    entity.HasKey(row => row.AssetId);
                    entity.Property(row => row.LeasedBy).HasMaxLength(200);
                    entity.Property(row => row.LastError).HasMaxLength(4000);
                    entity.HasIndex(row => row.LeaseExpiresAtUtc);
                    entity.HasOne<CatalogMediaAssetRow>()
                        .WithOne()
                        .HasForeignKey<CatalogMediaProcessingWorkRow>(row => row.AssetId)
                        .OnDelete(DeleteBehavior.Restrict);
                });
                modelBuilder.Entity<CatalogMediaOutboxRow>(entity =>
                {
                    entity.ToTable("outbox_message", "media_messaging");
                    entity.HasKey(row => row.MessageId);
                    entity.Property(row => row.RoutingKey).HasMaxLength(256);
                    entity.Property(row => row.ContractIdentity).HasMaxLength(256);
                    entity.Property(row => row.PayloadJson).HasColumnType("jsonb");
                    entity.Property(row => row.PayloadDigest).HasMaxLength(64);
                    entity.Property(row => row.CorrelationId).HasMaxLength(128);
                    entity.Property(row => row.LeasedBy).HasMaxLength(200);
                    entity.Property(row => row.LastError).HasMaxLength(2000);
                    entity.Property(row => row.DeadLetterReason).HasMaxLength(2000);
                    entity.HasIndex(row => new { row.DispatchedAtUtc, row.DeadLetteredAtUtc, row.OccurredAtUtc });
                    entity.HasIndex(row => row.LeaseExpiresAtUtc);
                });
                ApplySnakeCaseColumns(modelBuilder);
            }

            private static void ApplySnakeCaseColumns(ModelBuilder modelBuilder)
            {
                foreach (var entityType in modelBuilder.Model.GetEntityTypes())
                {
                    foreach (var property in entityType.GetProperties())
                    {
                        property.SetColumnName(ToSnakeCase(property.Name));
                    }
                }
            }

            private static string ToSnakeCase(string value)
            {
                var builder = new StringBuilder(value.Length + 8);
                for (var index = 0; index < value.Length; index++)
                {
                    var character = value[index];
                    if (char.IsUpper(character) && index > 0) builder.Append('_');
                    builder.Append(char.ToLowerInvariant(character));
                }
                return builder.ToString();
            }
        }
        """;

    private static string PersistenceJson() =>
        """
        using Aggregator.CatalogMedia.Application;
        using Aggregator.CatalogMedia.Domain;

        namespace Aggregator.CatalogMedia.Infrastructure;

        internal sealed record CatalogMediaPersistenceVariant(
            Guid Id,
            Guid AssetId,
            CatalogMediaVariantKind Kind,
            string ObjectKey,
            string ContentType,
            string ContentDigest,
            long Size,
            int Width,
            int Height,
            DateTimeOffset CreatedAtUtc);

        internal sealed record CatalogMediaPersistenceSnapshot(
            Guid Id,
            string CatalogKey,
            CatalogMediaState State,
            string QuarantineObjectKey,
            string ExpectedContentType,
            string ExpectedContentDigest,
            long ExpectedSize,
            CatalogMediaRightsBasis RightsBasis,
            string RightsReference,
            DateTimeOffset RegisteredAtUtc,
            DateTimeOffset ChangedAtUtc,
            long AggregateRevision,
            DateTimeOffset? UploadAuthorizationExpiresAtUtc,
            DateTimeOffset? UploadedAtUtc,
            DateTimeOffset? ScannedAtUtc,
            DateTimeOffset? AcceptedAtUtc,
            DateTimeOffset? RightsRevokedAtUtc,
            Guid? RightsRevokedByActorId,
            string? FailureCode,
            IReadOnlyList<CatalogMediaPersistenceVariant> Variants);

        internal static class CatalogMediaPersistenceJson
        {
            public static byte[] Serialize(CatalogMediaAsset asset)
            {
                ArgumentNullException.ThrowIfNull(asset);
                return CatalogMediaCanonicalJson.Serialize(ToSnapshot(asset));
            }

            public static CatalogMediaAsset Deserialize(ReadOnlySpan<byte> document, string expectedDigest)
            {
                CatalogMediaCanonicalJson.RequireDigest(expectedDigest, nameof(expectedDigest));
                var actualDigest = CatalogMediaCanonicalJson.ComputeDigest(document);
                if (!string.Equals(actualDigest, expectedDigest, StringComparison.Ordinal))
                {
                    throw Failure(
                        "CATALOG_MEDIA_COMMAND_RESULT_DIGEST_MISMATCH",
                        "Persisted Catalog media command result failed digest verification.",
                        "Restore the exact command result from a verified catalog_db backup.");
                }
                return Restore(CatalogMediaCanonicalJson.Deserialize<CatalogMediaPersistenceSnapshot>(document));
            }

            public static CatalogMediaAsset Restore(CatalogMediaPersistenceSnapshot snapshot)
            {
                ArgumentNullException.ThrowIfNull(snapshot);
                var variants = snapshot.Variants.Select(item => CatalogMediaVariant.Create(
                    item.Id, item.AssetId, item.Kind, item.ObjectKey, item.ContentType,
                    item.ContentDigest, item.Size, item.Width, item.Height, item.CreatedAtUtc));
                return CatalogMediaAsset.Restore(
                    snapshot.Id,
                    snapshot.CatalogKey,
                    snapshot.State,
                    snapshot.QuarantineObjectKey,
                    snapshot.ExpectedContentType,
                    snapshot.ExpectedContentDigest,
                    snapshot.ExpectedSize,
                    snapshot.RightsBasis,
                    snapshot.RightsReference,
                    snapshot.RegisteredAtUtc,
                    snapshot.ChangedAtUtc,
                    snapshot.AggregateRevision,
                    snapshot.UploadAuthorizationExpiresAtUtc,
                    snapshot.UploadedAtUtc,
                    snapshot.ScannedAtUtc,
                    snapshot.AcceptedAtUtc,
                    snapshot.RightsRevokedAtUtc,
                    snapshot.RightsRevokedByActorId,
                    snapshot.FailureCode,
                    variants);
            }

            public static CatalogMediaPersistenceSnapshot ToSnapshot(CatalogMediaAsset asset) =>
                new(
                    asset.Id,
                    asset.CatalogKey,
                    asset.State,
                    asset.QuarantineObjectKey,
                    asset.ExpectedContentType,
                    asset.ExpectedContentDigest,
                    asset.ExpectedSize,
                    asset.RightsBasis,
                    asset.RightsReference,
                    asset.RegisteredAtUtc,
                    asset.ChangedAtUtc,
                    asset.AggregateRevision,
                    asset.UploadAuthorizationExpiresAtUtc,
                    asset.UploadedAtUtc,
                    asset.ScannedAtUtc,
                    asset.AcceptedAtUtc,
                    asset.RightsRevokedAtUtc,
                    asset.RightsRevokedByActorId,
                    asset.FailureCode,
                    asset.Variants.Select(item => new CatalogMediaPersistenceVariant(
                        item.Id, item.AssetId, item.Kind, item.ObjectKey, item.ContentType,
                        item.ContentDigest, item.Size, item.Width, item.Height, item.CreatedAtUtc)).ToArray());

            private static CatalogMediaApplicationException Failure(string code, string message, string action) =>
                new("CatalogMedia.Persistence", code, 500, message, action);
        }
        """;

    private static string ObjectStoreAdapter(DescriptorContract descriptor, UploadContract upload)
    {
        var uploadUri = upload.Uri.PropertyType == typeof(Uri)
            ? $"upload.{upload.Uri.Name}"
            : $"new Uri(upload.{upload.Uri.Name}, UriKind.Absolute)";
        return $$"""
        using System.Security.Cryptography;
        using Aggregator.CatalogMedia.Application;
        using Aggregator.CatalogMedia.Domain;
        using Platform.ObjectStorage;

        namespace Aggregator.CatalogMedia.Infrastructure;

        public sealed class ObjectStoreCatalogMediaStore(IObjectStore objectStore) : ICatalogMediaObjectStore
        {
            public async Task<CatalogMediaUploadAuthorization> CreateUploadAuthorizationAsync(
                CatalogMediaAsset asset,
                TimeSpan lifetime,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(asset);
                var upload = await objectStore.CreatePresignedUploadAsync(
                    asset.QuarantineObjectKey,
                    asset.ExpectedContentType,
                    asset.ExpectedSize,
                    lifetime,
                    cancellationToken);
                return new CatalogMediaUploadAuthorization(
                    {{uploadUri}},
                    upload.{{upload.ExpiresAt.Name}},
                    upload.{{upload.Headers.Name}});
            }

            public async Task<CatalogMediaObjectDescriptor> VerifyUploadedAsync(
                CatalogMediaAsset asset,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(asset);
                var descriptor = await objectStore.HeadAsync(asset.QuarantineObjectKey, cancellationToken)
                    ?? throw Failure(
                        "CATALOG_MEDIA_OBJECT_NOT_FOUND",
                        "Registered media object is absent from quarantine storage.",
                        "Upload the exact object before completing the command.");
                var key = descriptor.{{descriptor.Key.Name}};
                var contentType = descriptor.{{descriptor.ContentType.Name}};
                var digest = descriptor.{{descriptor.Digest.Name}};
                var size = Convert.ToInt64(descriptor.{{descriptor.Size.Name}}, System.Globalization.CultureInfo.InvariantCulture);
                if (!string.Equals(key, asset.QuarantineObjectKey, StringComparison.Ordinal) ||
                    !string.Equals(contentType, asset.ExpectedContentType, StringComparison.Ordinal) ||
                    !string.Equals(digest, asset.ExpectedContentDigest, StringComparison.Ordinal) ||
                    size != asset.ExpectedSize)
                {
                    throw Failure(
                        "CATALOG_MEDIA_OBJECT_METADATA_MISMATCH",
                        "Quarantine object metadata differs from the registered media identity.",
                        "Delete the divergent object and upload the exact registered bytes.");
                }
                await using var verified = await objectStore.OpenReadVerifiedAsync(
                    asset.QuarantineObjectKey,
                    asset.ExpectedContentDigest,
                    cancellationToken);
                var buffer = new byte[64 * 1024];
                long observed = 0;
                while (true)
                {
                    var read = await verified.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    observed += read;
                    if (observed > asset.ExpectedSize)
                    {
                        throw Failure(
                            "CATALOG_MEDIA_OBJECT_SIZE_MISMATCH",
                            "Verified media stream exceeds the registered size.",
                            "Replace the quarantine object with the exact registered bytes.");
                    }
                }
                if (observed != asset.ExpectedSize)
                {
                    throw Failure(
                        "CATALOG_MEDIA_OBJECT_SIZE_MISMATCH",
                        "Verified media stream length differs from the registered size.",
                        "Replace the quarantine object with the exact registered bytes.");
                }
                return new CatalogMediaObjectDescriptor(key, contentType, digest, size);
            }

            public Task<Stream> OpenQuarantineReadAsync(
                CatalogMediaAsset asset,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(asset);
                return objectStore.OpenReadVerifiedAsync(
                    asset.QuarantineObjectKey,
                    asset.ExpectedContentDigest,
                    cancellationToken);
            }

            public async Task<CatalogMediaObjectDescriptor> PutVariantAsync(
                CatalogMediaAsset asset,
                CatalogMediaVariantKind kind,
                string contentType,
                ReadOnlyMemory<byte> content,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(asset);
                if (content.IsEmpty)
                {
                    throw Failure(
                        "CATALOG_MEDIA_VARIANT_EMPTY",
                        "Generated media variant is empty.",
                        "Correct the image-processing owner before retrying.");
                }
                var extension = contentType switch
                {
                    "image/jpeg" => "jpg",
                    "image/png" => "png",
                    "image/webp" => "webp",
                    _ => throw Failure(
                        "CATALOG_MEDIA_VARIANT_CONTENT_TYPE_UNSUPPORTED",
                        $"Generated media type '{contentType}' is unsupported.",
                        "Emit one of the allowlisted image content types."),
                };
                var key = $"catalog-media/published/{asset.CatalogKey}/{asset.Id:N}/{kind.ToString().ToLowerInvariant()}.{extension}";
                var digest = Convert.ToHexStringLower(SHA256.HashData(content.Span));
                await using var stream = new MemoryStream(content.ToArray(), writable: false);
                _ = await objectStore.PutVerifiedAsync(
                    key,
                    stream,
                    content.Length,
                    digest,
                    contentType,
                    cancellationToken);
                return new CatalogMediaObjectDescriptor(key, contentType, digest, content.Length);
            }

            public Task DeleteQuarantineAsync(CatalogMediaAsset asset, CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(asset);
                return objectStore.DeleteAsync(asset.QuarantineObjectKey, cancellationToken);
            }

            private static CatalogMediaApplicationException Failure(string code, string message, string action) =>
                new("CatalogMedia.ObjectStorage", code, 422, message, action);
        }
        """;
    }

    private static string ServiceCollection() =>
        """
        using Aggregator.CatalogMedia.Application;
        using Microsoft.EntityFrameworkCore;
        using Microsoft.Extensions.Configuration;
        using Microsoft.Extensions.DependencyInjection;
        using Platform.ObjectStorage;

        namespace Aggregator.CatalogMedia.Infrastructure;

        public static class CatalogMediaInfrastructureServiceCollectionExtensions
        {
            public static IServiceCollection AddCatalogMediaInfrastructure(
                this IServiceCollection services,
                IConfiguration configuration)
            {
                ArgumentNullException.ThrowIfNull(services);
                ArgumentNullException.ThrowIfNull(configuration);
                var connectionString = configuration.GetConnectionString("Catalog")
                    ?? throw new InvalidOperationException("Connection string 'Catalog' is required for Catalog media.");
                services.AddDbContext<CatalogMediaDbContext>(options => options.UseNpgsql(connectionString));
                services.AddScoped<EfCatalogMediaRepository>();
                services.AddScoped<ICatalogMediaRepository>(services =>
                    services.GetRequiredService<EfCatalogMediaRepository>());
                services.AddScoped<ICatalogMediaObjectStore, ObjectStoreCatalogMediaStore>();
                services.AddSingleton<ICatalogMediaClock, SystemCatalogMediaClock>();
                services.AddSingleton<ICatalogMediaIdSource, UuidV7CatalogMediaIdSource>();
                services.AddScoped<CatalogMediaReadinessProbe>();
                return services;
            }
        }

        public sealed class SystemCatalogMediaClock : ICatalogMediaClock
        {
            public DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow();
        }

        public sealed class UuidV7CatalogMediaIdSource : ICatalogMediaIdSource
        {
            public Guid CreateId() => Guid.CreateVersion7();
        }

        public sealed class CatalogMediaReadinessProbe(CatalogMediaDbContext dbContext)
        {
            public Task<bool> CanConnectAsync(CancellationToken cancellationToken) =>
                dbContext.Database.CanConnectAsync(cancellationToken);
        }
        """;

    private static void WriteFile(string directory, string fileName, string content)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content.Trim() + Environment.NewLine);
    }
}
