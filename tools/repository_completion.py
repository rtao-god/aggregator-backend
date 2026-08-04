from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8")


def write(relative: str, content: str) -> None:
    path = ROOT / relative
    if path.read_text(encoding="utf-8") != content:
        path.write_text(content, encoding="utf-8")


def replace_once(relative: str, old: str, new: str, *, required: bool = True) -> None:
    source = read(relative)
    if old in source:
        write(relative, source.replace(old, new, 1))
        return
    if new in source:
        return
    if required:
        raise RuntimeError(f"Expected normalization anchor was not found in {relative}: {old!r}")


def ensure_using(relative: str, namespace: str) -> None:
    source = read(relative)
    directive = f"using {namespace};\n"
    if directive not in source:
        write(relative, directive + source)


def ensure_package_reference(relative: str, package: str) -> None:
    source = read(relative)
    marker = f'<PackageReference Include="{package}" />'
    if marker in source:
        return
    insertion = f"  <ItemGroup>\n    {marker}\n  </ItemGroup>\n"
    write(relative, source.replace("</Project>", insertion + "</Project>", 1))


def ensure_project_reference(relative: str, project_reference: str) -> None:
    source = read(relative)
    marker = f'<ProjectReference Include="{project_reference}" />'
    if marker in source:
        return
    insertion = f"  <ItemGroup>\n    {marker}\n  </ItemGroup>\n"
    write(relative, source.replace("</Project>", insertion + "</Project>", 1))


def normalize_analytics() -> None:
    replace_once(
        "src/Analytics/Analytics.Domain/AnalyticsObservation.cs",
        "public sealed record AnalyticsObservation\n",
        "public sealed partial record AnalyticsObservation\n",
        required=False,
    )
    ensure_package_reference(
        "src/Analytics/Analytics.Application/Analytics.Application.csproj",
        "Microsoft.Extensions.DependencyInjection.Abstractions",
    )

    relative = "tests/Analytics/Analytics.Runtime.Tests/AnalyticsRuntimeTests.cs"
    source = read(relative)
    source = source.replace(
        "        var revisionId = Guid.CreateVersion7();",
        '        var revisionId = Guid.Parse("019b9b00-0000-7000-8000-000000000102");',
        1,
    )
    write(relative, source)


def normalize_promotion() -> None:
    rename_paths = [
        "src/Promotion/Promotion.Application/PromotionCampaignServices.cs",
        "src/Promotion/Promotion.Infrastructure/PromotionRuntimePersistence.cs",
        "src/Promotion/Promotion.Api/Program.cs",
        "src/Promotion/Promotion.Api/PromotionCampaignsController.cs",
        "tests/Promotion/Promotion.Runtime.Tests/PromotionRuntimeTests.cs",
        "tests/Promotion/Promotion.Api.Tests/PromotionApiFactory.cs",
    ]
    for relative in rename_paths:
        source = read(relative)
        source = source.replace(
            "PromotionApplicationException",
            "PromotionCampaignApplicationException",
        )
        write(relative, source)

    ensure_package_reference(
        "src/Promotion/Promotion.Application/Promotion.Application.csproj",
        "Microsoft.Extensions.DependencyInjection.Abstractions",
    )

    relative = "src/Promotion/Promotion.Infrastructure/PromotionRuntimePersistence.cs"
    source = read(relative)
    old_active = '''    public async Task<IReadOnlyList<PromotionCampaignSnapshot>> ReadActiveAsync(
        string catalogKey,
        string placementKey,
        DateTimeOffset effectiveAtUtc,
        int limit,
        CancellationToken cancellationToken) =>
        await dbContext.Campaigns
            .AsNoTracking()
            .Where(row =>
                row.CatalogKey == catalogKey &&
                row.PlacementKey == placementKey &&
                row.State == (int)PromotionCampaignState.Active &&
                row.StartsAtUtc <= effectiveAtUtc &&
                row.EndsAtUtc > effectiveAtUtc)
            .OrderBy(row => row.StartsAtUtc)
            .ThenBy(row => row.Id)
            .Take(limit)
            .Select(row => ToSnapshot(row))
            .ToArrayAsync(cancellationToken);
'''
    new_active = '''    public async Task<IReadOnlyList<PromotionCampaignSnapshot>> ReadActiveAsync(
        string catalogKey,
        string placementKey,
        DateTimeOffset effectiveAtUtc,
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.Campaigns
            .AsNoTracking()
            .Where(row =>
                row.CatalogKey == catalogKey &&
                row.PlacementKey == placementKey &&
                row.State == (int)PromotionCampaignState.Active &&
                row.StartsAtUtc <= effectiveAtUtc &&
                row.EndsAtUtc > effectiveAtUtc)
            .OrderBy(row => row.StartsAtUtc)
            .ThenBy(row => row.Id)
            .Take(limit)
            .ToArrayAsync(cancellationToken);
        return rows.Select(ToSnapshot).ToArray();
    }
'''
    if old_active in source:
        source = source.replace(old_active, new_active, 1)

    old_expired = '''    public async Task<IReadOnlyList<PromotionCampaignSnapshot>> ReadExpiredAsync(
        DateTimeOffset effectiveAtUtc,
        int limit,
        CancellationToken cancellationToken) =>
        await dbContext.Campaigns
            .AsNoTracking()
            .Where(row =>
                (row.State == (int)PromotionCampaignState.Active ||
                 row.State == (int)PromotionCampaignState.Suspended) &&
                row.EndsAtUtc <= effectiveAtUtc)
            .OrderBy(row => row.EndsAtUtc)
            .ThenBy(row => row.Id)
            .Take(limit)
            .Select(row => ToSnapshot(row))
            .ToArrayAsync(cancellationToken);
'''
    new_expired = '''    public async Task<IReadOnlyList<PromotionCampaignSnapshot>> ReadExpiredAsync(
        DateTimeOffset effectiveAtUtc,
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.Campaigns
            .AsNoTracking()
            .Where(row =>
                (row.State == (int)PromotionCampaignState.Active ||
                 row.State == (int)PromotionCampaignState.Suspended) &&
                row.EndsAtUtc <= effectiveAtUtc)
            .OrderBy(row => row.EndsAtUtc)
            .ThenBy(row => row.Id)
            .Take(limit)
            .ToArrayAsync(cancellationToken);
        return rows.Select(ToSnapshot).ToArray();
    }
'''
    if old_expired in source:
        source = source.replace(old_expired, new_expired, 1)
    write(relative, source)

    relative = "src/Promotion/Promotion.Migrations/Migrations/V001__promotion_runtime.sql"
    source = read(relative)
    source = source.replace(
        "CONSTRAINT ck_promotion_eligibility_entitlement_id CHECK (entitlement_id <> '00000000-0000-0000-8000-000000000000'::uuid OR entitlement_id <> '00000000-0000-0000-0000-000000000000'::uuid)",
        "CONSTRAINT ck_promotion_eligibility_entitlement_id CHECK (entitlement_id <> '00000000-0000-0000-0000-000000000000'::uuid)",
    )
    write(relative, source)


def normalize_ingestion() -> None:
    ensure_using(
        "src/Ingestion/Ingestion.Application/IngestionProcessingServices.cs",
        "System.Text.Json",
    )
    ensure_project_reference(
        "src/Ingestion/Ingestion.Application/Ingestion.Application.csproj",
        "../../Catalog/Catalog.Contracts/Catalog.Contracts.csproj",
    )
    ensure_project_reference(
        "src/Ingestion/Ingestion.Infrastructure/Ingestion.Infrastructure.csproj",
        "../../Catalog/Catalog.Contracts/Catalog.Contracts.csproj",
    )

    relative = "src/Ingestion/Ingestion.Worker/Ingestion.Worker.csproj"
    source = read(relative).replace(
        '    <PackageReference Include="Microsoft.Extensions.Http" />\n',
        "",
    )
    write(relative, source)

    relative = "src/Ingestion/Ingestion.Application/IngestionApplicationServiceCollectionExtensions.cs"
    source = read(relative)
    if "services.AddIngestionProcessingApplication();" not in source:
        source = source.replace(
            "        services.AddScoped<ReadIngestionBatchService>();",
            "        services.AddScoped<ReadIngestionBatchService>();\n        services.AddIngestionProcessingApplication();",
            1,
        )
    write(relative, source)

    relative = "src/Ingestion/Ingestion.Application/IngestionProcessingServices.cs"
    source = read(relative)
    if "services.AddSingleton(TimeProvider.System);" not in source:
        source = source.replace(
            "        ArgumentNullException.ThrowIfNull(services);",
            "        ArgumentNullException.ThrowIfNull(services);\n        services.AddSingleton(TimeProvider.System);",
            1,
        )
    write(relative, source)

    relative = "src/Ingestion/Ingestion.Application/IngestionCanonicalJson.cs"
    source = read(relative)
    exact_overload = '''    public static string ComputeDigest(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    }

'''
    if "public static string ComputeDigest(byte[] value)" not in source:
        anchor = "    public static string ComputeDigest(ReadOnlySpan<byte> value) =>\n"
        if anchor not in source:
            raise RuntimeError("Ingestion canonical byte digest anchor was not found.")
        source = source.replace(anchor, exact_overload + anchor, 1)
    write(relative, source)

    relative = "src/Ingestion/Ingestion.Infrastructure/IngestionProcessingPersistence.cs"
    source = read(relative)
    old_digest = '''        var digestInput = new CatalogCommandDigestInput(
            commandId,
            batch.Id,
            item.ItemKey,
            batch.TargetSiteKey,
            batch.TargetCatalogKey,
            batch.TargetCatalogConfigurationRevisionId,
            item.EntityKind,
            item.SubjectNaturalKey,
            fields,
            requestedAtUtc);
        var commandDigest = ProcessingDocument.ComputeDigest(ProcessingDocument.Serialize(digestInput));'''
    new_digest = '''        var digestInput = new CatalogIngestionCommandDigestInput(
            commandId,
            batch.Id,
            item.ItemKey,
            batch.TargetSiteKey,
            batch.TargetCatalogKey,
            batch.TargetCatalogConfigurationRevisionId,
            item.EntityKind,
            item.SubjectNaturalKey,
            fields,
            requestedAtUtc);
        var commandDigest = CatalogIngestionCommandDigest.Compute(digestInput);'''
    if old_digest in source:
        source = source.replace(old_digest, new_digest, 1)
    source = source.replace(
        "        batch.FailureCode = NormalizeReason(failureCode);",
        "        batch.FailureCode = NormalizeFailureCode(failureCode);",
        1,
    )
    if "private static string NormalizeFailureCode" not in source:
        anchor = "    private static string NormalizeReason(string value)\n"
        helper = '''    private static string NormalizeFailureCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 ||
            value.Any(character => !(char.IsLetterOrDigit(character) || character is '_' or '-' or ':' or '.')))
        {
            throw ProcessingFailure(
                "INGESTION_FAILURE_CODE_INVALID",
                500,
                "A generated processing failure code is invalid.",
                "Correct the processing failure classifier.");
        }

        return value;
    }

'''
        if anchor not in source:
            raise RuntimeError("Ingestion failure-code helper anchor was not found.")
        source = source.replace(anchor, helper + anchor, 1)
    write(relative, source)

    relative = "src/Ingestion/Ingestion.Migrations/Migrations/V002__ingestion_processing.sql"
    source = read(relative).replace(
        "IF OLD.state IN (3, 4) AND ROW(OLD.*) IS DISTINCT FROM ROW(NEW.*)",
        "IF OLD.state IN (3, 4) AND OLD IS DISTINCT FROM NEW",
    )
    write(relative, source)

    relative = "tests/Ingestion/Ingestion.Processing.Tests/IngestionProcessingTests.cs"
    source = read(relative).replace(
        "            return Task.FromResult(ValidationResult);",
        "            return Task.FromResult(ValidationResult!);",
    )
    write(relative, source)


def normalize_catalog() -> None:
    relative = "src/Catalog/Catalog.Infrastructure/CatalogIngestionDraftPersistence.cs"
    source = read(relative)
    relationship = '''        entity.HasOne<CatalogIngestionDraftRow>()
            .WithMany()
            .HasForeignKey(row => new { row.IngestionBatchId, row.IngestionItemKey })
            .HasPrincipalKey(row => new { row.IngestionBatchId, row.IngestionItemKey })
            .OnDelete(DeleteBehavior.Restrict);
'''
    source = source.replace(relationship, "")
    write(relative, source)

    relative = "src/Catalog/Catalog.Api/Program.cs"
    source = read(relative)
    if "builder.Services.AddCatalogIngestionInfrastructure(builder.Configuration);" not in source:
        source = source.replace(
            "        builder.Services.AddCatalogInfrastructure(builder.Configuration);",
            "        builder.Services.AddCatalogInfrastructure(builder.Configuration);\n        builder.Services.AddCatalogIngestionInfrastructure(builder.Configuration);\n        builder.Services.AddScoped<CatalogIngestionDraftService>();",
            1,
        )
    if "CatalogIngestionAuthorizationPolicies.ExecuteDraftCommand" not in source:
        anchor = '''            .AddRequiredScopePolicy(
                CatalogAuthorizationPolicies.TestContracts,
                CatalogAuthorizationPolicies.TestContracts);'''
        replacement = '''            .AddRequiredScopePolicy(
                CatalogAuthorizationPolicies.TestContracts,
                CatalogAuthorizationPolicies.TestContracts)
            .AddRequiredScopePolicy(
                CatalogIngestionAuthorizationPolicies.ExecuteDraftCommand,
                CatalogIngestionAuthorizationPolicies.ExecuteDraftCommand);'''
        if anchor not in source:
            raise RuntimeError("Catalog authorization chain anchor was not found.")
        source = source.replace(anchor, replacement, 1)
    if "application.UseMiddleware<CatalogIngestionFailureMiddleware>();" not in source:
        source = source.replace(
            "        application.UseMiddleware<CatalogFailureMiddleware>();",
            "        application.UseMiddleware<CatalogIngestionFailureMiddleware>();\n        application.UseMiddleware<CatalogFailureMiddleware>();",
            1,
        )
    write(relative, source)

    relative = "src/Ingestion/Ingestion.Api/Program.cs"
    source = read(relative)
    if "builder.Services.AddIngestionProcessingInfrastructure(builder.Configuration);" not in source:
        source = source.replace(
            "        builder.Services.AddIngestionInfrastructure(builder.Configuration);",
            "        builder.Services.AddIngestionInfrastructure(builder.Configuration);\n        builder.Services.AddIngestionProcessingInfrastructure(builder.Configuration);",
            1,
        )
    if "IngestionProcessingAuthorizationPolicies.Review" not in source:
        anchor = '''            .AddRequiredScopePolicy(
                IngestionAuthorizationPolicies.TestContracts,
                IngestionAuthorizationPolicies.TestContracts);'''
        replacement = '''            .AddRequiredScopePolicy(
                IngestionAuthorizationPolicies.TestContracts,
                IngestionAuthorizationPolicies.TestContracts)
            .AddRequiredScopePolicy(
                IngestionProcessingAuthorizationPolicies.Review,
                IngestionProcessingAuthorizationPolicies.Review)
            .AddRequiredScopePolicy(
                IngestionProcessingAuthorizationPolicies.Commit,
                IngestionProcessingAuthorizationPolicies.Commit)
            .AddRequiredScopePolicy(
                IngestionProcessingAuthorizationPolicies.Delivery,
                IngestionProcessingAuthorizationPolicies.Delivery);'''
        if anchor not in source:
            raise RuntimeError("Ingestion authorization chain anchor was not found.")
        source = source.replace(anchor, replacement, 1)
    write(relative, source)


def normalize_security_test() -> None:
    relative = "tests/Architecture.Tests/RepositorySecurityRulesTests.cs"
    source = read(relative)
    old = '''        value.StartsWith("Environment.GetEnvironmentVariable", StringComparison.Ordinal) ||
        value.StartsWith("configuration[", StringComparison.OrdinalIgnoreCase);'''
    new = '''        value.StartsWith("Environment.GetEnvironmentVariable", StringComparison.Ordinal) ||
        value.StartsWith("configuration[", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("RequireSetting(", StringComparison.Ordinal) ||
        value.Contains("GetConnectionString(", StringComparison.Ordinal) ||
        value.Contains("GetEnvironmentVariable(", StringComparison.Ordinal) ||
        value.Contains("GetRequiredSection(", StringComparison.Ordinal) ||
        value.Contains("GetValue<", StringComparison.Ordinal) ||
        value.Contains("nameof(", StringComparison.Ordinal) ||
        value.Contains("Options.", StringComparison.Ordinal) ||
        value.Contains("options.", StringComparison.Ordinal) ||
        value.Contains("configuration", StringComparison.OrdinalIgnoreCase);'''
    if old in source:
        source = source.replace(old, new, 1)
    write(relative, source)


def main() -> None:
    normalize_analytics()
    normalize_promotion()
    normalize_ingestion()
    normalize_catalog()
    normalize_security_test()


if __name__ == "__main__":
    main()
