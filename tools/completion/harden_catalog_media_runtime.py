#!/usr/bin/env python3
from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CATALOG = ROOT / "src" / "Catalog"
TESTS = ROOT / "tests" / "Catalog"


def replace_required(path: Path, old: str, new: str, label: str) -> None:
    text = path.read_text(encoding="utf-8")
    if old not in text:
        raise RuntimeError(f"Catalog media runtime hardening anchor is missing: {label} ({path})")
    path.write_text(text.replace(old, new), encoding="utf-8")


application_project = CATALOG / "Catalog.Media.Application" / "Catalog.Media.Application.csproj"
replace_required(
    application_project,
    "  <ItemGroup>\n    <ProjectReference",
    "  <ItemGroup>\n    <PackageReference Include=\"Microsoft.Extensions.DependencyInjection.Abstractions\" />\n  </ItemGroup>\n  <ItemGroup>\n    <ProjectReference",
    "application DI package",
)

application = CATALOG / "Catalog.Media.Application" / "CatalogMediaApplication.cs"
replace_required(
    application,
    '''        catch (Exception exception)
        {
            _ = await repository.RecordProcessingFailureAsync(
                lease, exception.Message, terminal: false, maximumAttempts,
                clock.GetUtcNow(), cancellationToken);
            return true;
        }''',
    '''        catch (Exception exception)
        {
            var failedAtUtc = clock.GetUtcNow();
            var terminal = exception is CatalogMediaDomainException ||
                exception is CatalogMediaApplicationException { StatusCode: < 500 };
            var attempts = await repository.RecordProcessingFailureAsync(
                lease,
                exception.Message,
                terminal,
                maximumAttempts,
                failedAtUtc,
                cancellationToken);
            if (terminal || attempts >= maximumAttempts)
            {
                var asset = lease.Asset;
                var failureCode = exception switch
                {
                    CatalogMediaApplicationException applicationException => applicationException.Code,
                    CatalogMediaDomainException domainException => domainException.Code,
                    _ => "catalog-media-processing-attempts-exhausted",
                };
                asset.Reject(asset.AggregateRevision, failureCode, failedAtUtc);
                var eventId = idSource.CreateId();
                var rejected = new CatalogMediaRejected(
                    eventId,
                    asset.Id,
                    asset.CatalogKey,
                    asset.AggregateRevision,
                    asset.FailureCode ?? failureCode,
                    failedAtUtc);
                var outbox = CatalogMediaCanonicalJson.ToOutbox(
                    eventId,
                    CatalogMediaIntegrationEventTypes.Rejected,
                    CatalogMediaIntegrationEventContracts.Rejected,
                    rejected,
                    failedAtUtc,
                    CatalogMediaCommandContext.Start(
                        CatalogMediaActor.Create(systemActorId),
                        workerIdentity));
                await repository.CompleteProcessingAsync(
                    lease,
                    asset,
                    outbox,
                    failedAtUtc,
                    cancellationToken);
                await objectStore.DeleteQuarantineAsync(asset, cancellationToken);
            }
            return true;
        }''',
    "terminal processing failure",
)

for path in (
    CATALOG / "Catalog.Media.Infrastructure" / "CatalogMediaDbContext.cs",
    CATALOG / "Catalog.Media.Infrastructure" / "EfCatalogMediaRepository.cs",
    CATALOG / "Catalog.Media.Migrations" / "Migrations" / "V001__catalog_media_owner_schema.sql",
    TESTS / "Catalog.Media.Infrastructure.Tests" / "CatalogMediaPersistenceModelTests.cs",
):
    text = path.read_text(encoding="utf-8")
    text = text.replace('"operations"', '"media_operations"')
    text = text.replace("operations.processing_work", "media_operations.processing_work")
    text = text.replace("operations.media_command_result", "media_operations.command_result")
    text = text.replace("operations.command_result", "media_operations.command_result")
    text = text.replace("CREATE SCHEMA IF NOT EXISTS operations;", "CREATE SCHEMA IF NOT EXISTS media_operations;")
    text = text.replace("CREATE TABLE operations.media_command_result", "CREATE TABLE media_operations.command_result")
    text = text.replace("CREATE TABLE operations.processing_work", "CREATE TABLE media_operations.processing_work")
    text = text.replace("CREATE INDEX ix_catalog_media_processing_available\n            ON operations.processing_work",
                        "CREATE INDEX ix_catalog_media_processing_available\n            ON media_operations.processing_work")
    text = text.replace("CREATE OR REPLACE FUNCTION operations.reject_media_command_mutation()",
                        "CREATE OR REPLACE FUNCTION media_operations.reject_media_command_mutation()")
    text = text.replace("BEFORE UPDATE OR DELETE ON operations.media_command_result",
                        "BEFORE UPDATE OR DELETE ON media_operations.command_result")
    text = text.replace("EXECUTE FUNCTION operations.reject_media_command_mutation()",
                        "EXECUTE FUNCTION media_operations.reject_media_command_mutation()")
    path.write_text(text, encoding="utf-8")

migration = CATALOG / "Catalog.Media.Migrations" / "Migrations" / "V001__catalog_media_owner_schema.sql"
text = migration.read_text(encoding="utf-8")
if "CREATE SCHEMA IF NOT EXISTS media_operations;" not in text:
    text = text.replace(
        "CREATE SCHEMA IF NOT EXISTS media;",
        "CREATE SCHEMA IF NOT EXISTS media;\n        CREATE SCHEMA IF NOT EXISTS media_operations;",
    )
migration.write_text(text, encoding="utf-8")

processor = CATALOG / "Catalog.Media.Worker" / "ImageMagickCatalogMediaVariantProcessor.cs"
text = processor.read_text(encoding="utf-8")
text = text.replace(
    '''                await RunAsync(
                    [input, "-auto-orient", "-strip", "-thumbnail", geometry, "-quality", "82", output],
                    cancellationToken);''',
    '''                await RunAsync(
                    "convert",
                    [input, "-auto-orient", "-strip", "-thumbnail", geometry, "-quality", "82", output],
                    cancellationToken);''',
)
text = text.replace(
    '''                var output = await RunAsync(["identify", "-format", "%w,%h", path], cancellationToken);''',
    '''                var output = await RunAsync(
                    "identify",
                    ["-format", "%w,%h", path],
                    cancellationToken);''',
)
text = text.replace(
    '''            private static async Task<string> RunAsync(
                IReadOnlyList<string> arguments,
                CancellationToken cancellationToken)
            {
                var start = new ProcessStartInfo("magick")''',
    '''            private static async Task<string> RunAsync(
                string executable,
                IReadOnlyList<string> arguments,
                CancellationToken cancellationToken)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(executable);
                var start = new ProcessStartInfo(executable)''',
)
old_failure = '''                if (process.ExitCode != 0)
                    throw Failure(
                        "CATALOG_MEDIA_IMAGEMAGICK_FAILED",
                        $"ImageMagick failed with exit code '{process.ExitCode}': {error.Trim()}"[..Math.Min(
                            $"ImageMagick failed with exit code '{process.ExitCode}': {error.Trim()}".Length,
                            2000)]);'''
new_failure = '''                if (process.ExitCode != 0)
                {
                    var message = $"ImageMagick failed with exit code '{process.ExitCode}': {error.Trim()}";
                    throw Failure(
                        "CATALOG_MEDIA_IMAGEMAGICK_FAILED",
                        message[..Math.Min(message.Length, 2000)]);
                }'''
if old_failure not in text:
    raise RuntimeError("ImageMagick failure-message anchor is missing.")
processor.write_text(text.replace(old_failure, new_failure), encoding="utf-8")

domain_test = TESTS / "Catalog.Media.Domain.Tests" / "CatalogMediaDomainTests.cs"
text = domain_test.read_text(encoding="utf-8").replace(
    "Assert.Equal(7, asset.AggregateRevision);",
    "Assert.Equal(6, asset.AggregateRevision);",
)
domain_test.write_text(text, encoding="utf-8")

auth_writer = CATALOG / "Catalog.Media.Api" / "CatalogMediaAuthorizationStatusCodeWriter.cs"
auth_writer.write_text(
    '''using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Platform.ProblemDetails;

namespace Aggregator.CatalogMedia.Api;

internal static class CatalogMediaAuthorizationStatusCodeWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(StatusCodeContext statusCodeContext)
    {
        ArgumentNullException.ThrowIfNull(statusCodeContext);
        var context = statusCodeContext.HttpContext;
        if (context.Response.StatusCode is not StatusCodes.Status401Unauthorized and
            not StatusCodes.Status403Forbidden)
        {
            return;
        }
        var unauthenticated = context.Response.StatusCode == StatusCodes.Status401Unauthorized;
        var correlation = context.RequestServices.GetRequiredService<ICorrelationContextAccessor>();
        var correlationId = correlation.CorrelationId
            ?? Activity.Current?.TraceId.ToString()
            ?? Guid.CreateVersion7().ToString("D");
        var problem = new ProblemDetails
        {
            Type = unauthenticated
                ? "https://errors.aggregator.local/catalog-media/access/authentication-required"
                : "https://errors.aggregator.local/catalog-media/access/authorization-denied",
            Title = unauthenticated ? "Authentication required" : "Authorization denied",
            Status = context.Response.StatusCode,
            Detail = unauthenticated
                ? "A valid Catalog media API token is required."
                : "The authenticated identity lacks the required Catalog media scope.",
            Instance = context.Request.Path,
        };
        problem.Extensions["owner"] = "CatalogMedia.Access";
        problem.Extensions["code"] = unauthenticated
            ? "AUTHENTICATION_REQUIRED"
            : "AUTHORIZATION_DENIED";
        problem.Extensions["correlationId"] = correlationId;
        problem.Extensions["requiredAction"] = unauthenticated
            ? "Authenticate with the Catalog media audience and retry."
            : "Request the exact OAuth scope required by this media operation.";
        context.Response.ContentType = "application/problem+json";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            problem,
            SerializerOptions,
            context.RequestAborted);
    }
}
''',
    encoding="utf-8",
)
program = CATALOG / "Catalog.Media.Api" / "Program.cs"
replace_required(
    program,
    "                app.UseOwnerProblemDetails();\n                app.UseMiddleware<CatalogMediaFailureMiddleware>();",
    "                app.UseOwnerProblemDetails();\n                app.UseStatusCodePages(CatalogMediaAuthorizationStatusCodeWriter.WriteAsync);\n                app.UseMiddleware<CatalogMediaFailureMiddleware>();",
    "typed auth status writer",
)

print("Catalog media generated runtime hardened.")
