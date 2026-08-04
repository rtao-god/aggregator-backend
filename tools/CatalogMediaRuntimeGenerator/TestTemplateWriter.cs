internal static class TestTemplateWriter
{
    public static void Write(CatalogMediaGenerationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        WriteDomainTests(context);
        WriteApplicationTests(context);
        WriteInfrastructureTests(context);
    }

    private static void WriteDomainTests(CatalogMediaGenerationContext context)
    {
        var directory = context.TestsDirectory("Domain");
        Directory.CreateDirectory(directory);
        WriteTestProject(
            directory,
            "Catalog.Media.Domain.Tests.csproj",
            "../../../src/Catalog/Catalog.Media.Domain/Catalog.Media.Domain.csproj");
        WriteUsings(directory);
        File.WriteAllText(
            Path.Combine(directory, "CatalogMediaDomainBoundaryTests.cs"),
            DomainTests().Trim() + Environment.NewLine);
    }

    private static void WriteApplicationTests(CatalogMediaGenerationContext context)
    {
        var directory = context.TestsDirectory("Application");
        Directory.CreateDirectory(directory);
        WriteTestProject(
            directory,
            "Catalog.Media.Application.Tests.csproj",
            "../../../src/Catalog/Catalog.Media.Application/Catalog.Media.Application.csproj",
            "../../../src/Catalog/Catalog.Media.Domain/Catalog.Media.Domain.csproj");
        WriteUsings(directory);
        File.WriteAllText(
            Path.Combine(directory, "CatalogMediaApplicationBoundaryTests.cs"),
            ApplicationTests().Trim() + Environment.NewLine);
    }

    private static void WriteInfrastructureTests(CatalogMediaGenerationContext context)
    {
        var directory = context.TestsDirectory("Infrastructure");
        Directory.CreateDirectory(directory);
        WriteTestProject(
            directory,
            "Catalog.Media.Infrastructure.Tests.csproj",
            "../../../src/Catalog/Catalog.Media.Infrastructure/Catalog.Media.Infrastructure.csproj",
            "../../../src/Catalog/Catalog.Media.Application/Catalog.Media.Application.csproj");
        WriteUsings(directory);
        File.WriteAllText(
            Path.Combine(directory, "CatalogMediaInfrastructureBoundaryTests.cs"),
            InfrastructureTests().Trim() + Environment.NewLine);
    }

    private static void WriteTestProject(
        string directory,
        string fileName,
        params string[] projectReferences)
    {
        var references = string.Join(
            Environment.NewLine,
            projectReferences.Select(reference =>
                $"    <ProjectReference Include=\"{reference}\" />"));
        File.WriteAllText(
            Path.Combine(directory, fileName),
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <IsPackable>false</IsPackable>
                <IsTestProject>true</IsTestProject>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <PackageReference Include="xunit" />
                <PackageReference Include="xunit.runner.visualstudio">
                  <PrivateAssets>all</PrivateAssets>
                  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
                </PackageReference>
                <PackageReference Include="coverlet.collector">
                  <PrivateAssets>all</PrivateAssets>
                  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
                </PackageReference>
              </ItemGroup>
              <ItemGroup>
            {{references}}
              </ItemGroup>
            </Project>
            """ + Environment.NewLine);
    }

    private static void WriteUsings(string directory) =>
        File.WriteAllText(
            Path.Combine(directory, "Usings.cs"),
            "global using Xunit;" + Environment.NewLine);

    private static string DomainTests() =>
        """
        using Aggregator.CatalogMedia.Domain;

        namespace Catalog.Media.Domain.Tests;

        public sealed class CatalogMediaDomainBoundaryTests
        {
            [Fact]
            public void DomainAssemblyHasNoPersistenceOrHttpFrameworkDependency()
            {
                var references = typeof(CatalogMediaAsset).Assembly
                    .GetReferencedAssemblies()
                    .Select(reference => reference.Name ?? string.Empty)
                    .ToArray();

                Assert.DoesNotContain(
                    references,
                    reference => reference.Contains("EntityFrameworkCore", StringComparison.Ordinal));
                Assert.DoesNotContain(
                    references,
                    reference => reference.Contains("AspNetCore", StringComparison.Ordinal));
                Assert.DoesNotContain(
                    references,
                    reference => reference.Contains("Npgsql", StringComparison.Ordinal));
            }

            [Fact]
            public void MediaLifecycleExposesExplicitOwnerStates()
            {
                var states = Enum.GetNames<CatalogMediaState>();

                Assert.Contains("Registered", states);
                Assert.Contains("Uploaded", states);
                Assert.Contains("Scanning", states);
                Assert.Contains("Accepted", states);
                Assert.Contains("Rejected", states);
                Assert.Contains("RightsRevoked", states);
            }
        }
        """;

    private static string ApplicationTests() =>
        """
        using Aggregator.CatalogMedia.Application;

        namespace Catalog.Media.Application.Tests;

        public sealed class CatalogMediaApplicationBoundaryTests
        {
            [Fact]
            public void ProcessingLeaseCapturesExactStoredAggregateRevision()
            {
                var property = typeof(CatalogMediaProcessingLease)
                    .GetProperty(nameof(CatalogMediaProcessingLease.StoredAggregateRevision));

                Assert.NotNull(property);
                Assert.Equal(typeof(long), property.PropertyType);
            }

            [Fact]
            public void RepositoryPortHasNoPublicationAuthority()
            {
                var methods = typeof(ICatalogMediaRepository).GetMethods();

                Assert.DoesNotContain(
                    methods,
                    method => method.Name.Contains("Publish", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(
                    methods,
                    method => method.Name.Contains("Publication", StringComparison.OrdinalIgnoreCase));
            }

            [Fact]
            public void CanonicalJsonDigestDoesNotDependOnDictionaryInsertionOrder()
            {
                var first = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["b"] = "second",
                    ["a"] = "first",
                };
                var second = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["a"] = "first",
                    ["b"] = "second",
                };

                Assert.Equal(
                    CatalogMediaCanonicalJson.ComputeDigest(first),
                    CatalogMediaCanonicalJson.ComputeDigest(second));
            }
        }
        """;

    private static string InfrastructureTests() =>
        """
        using Aggregator.CatalogMedia.Application;
        using Aggregator.CatalogMedia.Infrastructure;

        namespace Catalog.Media.Infrastructure.Tests;

        public sealed class CatalogMediaInfrastructureBoundaryTests
        {
            [Fact]
            public void RepositoryImplementsTheCatalogMediaOwnerPort()
            {
                Assert.Contains(
                    typeof(ICatalogMediaRepository),
                    typeof(EfCatalogMediaRepository).GetInterfaces());
            }

            [Fact]
            public void InfrastructureDoesNotReferenceAnotherBusinessContextImplementation()
            {
                var references = typeof(EfCatalogMediaRepository).Assembly
                    .GetReferencedAssemblies()
                    .Select(reference => reference.Name ?? string.Empty)
                    .Where(reference => reference.StartsWith("Catalog.", StringComparison.Ordinal) ||
                        reference.StartsWith("Query.", StringComparison.Ordinal) ||
                        reference.StartsWith("Ingestion.", StringComparison.Ordinal) ||
                        reference.StartsWith("Analytics.", StringComparison.Ordinal) ||
                        reference.StartsWith("Promotion.", StringComparison.Ordinal))
                    .ToArray();

                Assert.All(
                    references,
                    reference => Assert.StartsWith("Catalog.Media.", reference, StringComparison.Ordinal));
            }
        }
        """;
}
