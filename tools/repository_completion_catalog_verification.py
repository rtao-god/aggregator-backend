from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def patch(relative: str, transform) -> None:
    path = ROOT / relative
    source = path.read_text(encoding="utf-8")
    result = transform(source)
    if result != source:
        path.write_text(result, encoding="utf-8")


def patch_application(source: str) -> str:
    interface = '''public interface ICatalogIngestionDraftCommandHandler
{
    public Task<CatalogIngestionCommandOutcome> ExecuteAsync(
        CatalogIngestionUpsertDraftCommand command,
        string callerIdentity,
        CancellationToken cancellationToken);
}

'''
    anchor = "public interface ICatalogIngestionTargetProjectionWriter\n"
    if interface not in source:
        if anchor not in source:
            raise RuntimeError("Catalog ingestion command-handler anchor was not found.")
        source = source.replace(anchor, interface + anchor, 1)
    source = source.replace(
        "public sealed class VerifiedCatalogIngestionDraftService(\n",
        "public sealed class VerifiedCatalogIngestionDraftService(\n",
        1,
    )
    declaration_end = "    TimeProvider timeProvider)\n{"
    replacement = "    TimeProvider timeProvider) : ICatalogIngestionDraftCommandHandler\n{"
    if declaration_end in source:
        source = source.replace(declaration_end, replacement, 1)
    elif replacement not in source:
        raise RuntimeError("Verified Catalog ingestion service declaration changed unexpectedly.")
    return source


def patch_controller(source: str) -> str:
    return source.replace(
        "public sealed class CatalogIngestionDraftController(CatalogIngestionDraftService service)",
        "public sealed class CatalogIngestionDraftController(ICatalogIngestionDraftCommandHandler service)",
        1,
    )


def patch_program(source: str) -> str:
    old = '''        builder.Services.AddCatalogIngestionInfrastructure(builder.Configuration);
        builder.Services.AddScoped<CatalogIngestionDraftService>();'''
    new = '''        builder.Services.AddCatalogIngestionInfrastructure(builder.Configuration);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<CatalogIngestionDraftService>();
        builder.Services.AddScoped<ICatalogIngestionDraftCommandHandler, VerifiedCatalogIngestionDraftService>();'''
    if old in source:
        source = source.replace(old, new, 1)
    elif "ICatalogIngestionDraftCommandHandler" not in source:
        raise RuntimeError("Catalog ingestion DI anchor was not found.")
    return source


def patch_infrastructure(source: str) -> str:
    old = '''        services.AddDbContext<CatalogIngestionDbContext>(options =>
            options.UseNpgsql(connectionString));
        services.AddScoped<ICatalogIngestionDraftStore, EfCatalogIngestionDraftStore>();'''
    new = '''        services.AddDbContext<CatalogIngestionDbContext>(options =>
            options.UseNpgsql(connectionString));
        services.AddScoped<ICatalogIngestionDraftStore, EfCatalogIngestionDraftStore>();
        services.AddScoped<ICatalogIngestionTargetProjectionWriter, EfCatalogIngestionTargetProjectionWriter>();'''
    if old in source:
        source = source.replace(old, new, 1)
    elif "ICatalogIngestionTargetProjectionWriter" not in source:
        raise RuntimeError("Catalog ingestion infrastructure DI anchor was not found.")
    return source


def patch_test_factory(source: str) -> str:
    if "services.RemoveAll<ICatalogIngestionDraftCommandHandler>();" not in source:
        source = source.replace(
            "            services.RemoveAll<ICatalogIngestionDraftStore>();",
            "            services.RemoveAll<ICatalogIngestionDraftStore>();\n            services.RemoveAll<ICatalogIngestionDraftCommandHandler>();",
            1,
        )
        source = source.replace(
            "            services.AddSingleton<ICatalogIngestionDraftStore>(Store);",
            "            services.AddSingleton<ICatalogIngestionDraftStore>(Store);\n            services.AddSingleton<ICatalogIngestionDraftCommandHandler>(\n                provider => new TestCommandHandler(\n                    new CatalogIngestionDraftService(provider.GetRequiredService<ICatalogIngestionDraftStore>())));",
            1,
        )
    handler = '''
    private sealed class TestCommandHandler(CatalogIngestionDraftService service)
        : ICatalogIngestionDraftCommandHandler
    {
        public Task<CatalogIngestionCommandOutcome> ExecuteAsync(
            CatalogIngestionUpsertDraftCommand command,
            string callerIdentity,
            CancellationToken cancellationToken) =>
            service.ExecuteAsync(command, callerIdentity, cancellationToken);
    }
'''
    marker = "\n    private sealed class TestAuthenticationHandler("
    if handler not in source:
        if marker not in source:
            raise RuntimeError("Catalog ingestion API test handler anchor was not found.")
        source = source.replace(marker, handler + marker, 1)
    return source


def main() -> None:
    patch(
        "src/Catalog/Catalog.Application/VerifiedCatalogIngestionDraftService.cs",
        patch_application,
    )
    patch(
        "src/Catalog/Catalog.Api/CatalogIngestionDraftController.cs",
        patch_controller,
    )
    patch("src/Catalog/Catalog.Api/Program.cs", patch_program)
    patch(
        "src/Catalog/Catalog.Infrastructure/CatalogIngestionDraftPersistence.cs",
        patch_infrastructure,
    )
    patch(
        "tests/Catalog/Catalog.Ingestion.Api.Tests/CatalogIngestionApiFactory.cs",
        patch_test_factory,
    )


if __name__ == "__main__":
    main()
