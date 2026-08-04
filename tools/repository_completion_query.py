from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def update(relative: str, transform) -> None:
    path = ROOT / relative
    source = path.read_text(encoding="utf-8")
    result = transform(source)
    if result != source:
        path.write_text(result, encoding="utf-8")


def ensure_package(source: str, package: str) -> str:
    marker = f'<PackageReference Include="{package}" />'
    if marker in source:
        return source
    return source.replace(
        "</Project>",
        f"  <ItemGroup>\n    {marker}\n  </ItemGroup>\n</Project>",
        1,
    )


def ensure_project(source: str, project: str) -> str:
    marker = f'<ProjectReference Include="{project}" />'
    if marker in source:
        return source
    return source.replace(
        "</Project>",
        f"  <ItemGroup>\n    {marker}\n  </ItemGroup>\n</Project>",
        1,
    )


def normalize_adapter_generator(source: str) -> str:
    return source.replace(
        "            output.Directory.Create();",
        "            Directory.CreateDirectory(output.DirectoryName!);",
    )


def normalize_composition_project(source: str) -> str:
    source = ensure_package(source, "Microsoft.Extensions.Configuration.Binder")
    source = ensure_package(source, "Microsoft.Extensions.DependencyInjection.Abstractions")
    return source


def normalize_query_application(source: str) -> str:
    return ensure_project(
        source,
        "../../Catalog/Catalog.Contracts/Catalog.Contracts.csproj",
    )


def normalize_worker(source: str) -> str:
    return source.replace(
        "            ConsumerDispatchConcurrency = Math.Min(Environment.ProcessorCount, options.PrefetchCount),",
        "            ConsumerDispatchConcurrency = (ushort)Math.Min(Environment.ProcessorCount, options.PrefetchCount),",
    )


def main() -> None:
    update(
        "tools/QueryProjectionAdapterGenerator/Program.cs",
        normalize_adapter_generator,
    )
    update(
        "tools/QueryWorkerCompositionGenerator/QueryWorkerCompositionGenerator.csproj",
        normalize_composition_project,
    )
    update(
        "src/Query/Query.Application/Query.Application.csproj",
        normalize_query_application,
    )
    update(
        "src/Query/Query.Worker/Program.cs",
        normalize_worker,
    )


if __name__ == "__main__":
    main()
