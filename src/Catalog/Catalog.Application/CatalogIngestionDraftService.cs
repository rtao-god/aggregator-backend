using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

public interface ICatalogIngestionDraftClock
{
    public DateTimeOffset GetUtcNow();
}

public sealed record CatalogIngestionDraftRegistrationResult(
    CatalogIngestionDraftProposal Proposal,
    bool Replayed);

public interface ICatalogIngestionDraftRepository
{
    public Task<CatalogIngestionDraftRegistrationResult> RegisterAsync(
        CatalogIngestionDraftProposal proposal,
        byte[] canonicalCandidateDocument,
        string requestDigest,
        string callerIdentity,
        CancellationToken cancellationToken);
}

public sealed record RegisterCatalogIngestionDraftCommand(
    RegisterCatalogIngestionDraftRequest Request,
    string CallerIdentity);

/// <summary>
/// Registers one Catalog-owned draft proposal from an exact accepted Ingestion item. This service
/// reserves draft identities only; it has no publication dependency or public-read side effect.
/// </summary>
public sealed class CatalogIngestionDraftService(
    ICatalogRepository catalogRepository,
    ICatalogIngestionDraftRepository draftRepository,
    ICatalogIdSource idSource,
    ICatalogIngestionDraftClock clock)
{
    public async Task<CatalogIngestionDraftResponse> RegisterAsync(
        RegisterCatalogIngestionDraftCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Request);
        var request = command.Request;
        ValidateRequest(request);
        var caller = ValidateCaller(command.CallerIdentity);
        var canonicalCandidate = CatalogIngestionDraftCanonicalJson.Serialize(request.CandidateDocument);
        var actualContentDigest = CatalogIngestionDraftCanonicalJson.ComputeDigest(canonicalCandidate);
        if (!string.Equals(actualContentDigest, request.ContentDigest, StringComparison.Ordinal))
        {
            throw new CatalogContractException(
                "catalog.ingestion_draft_content_digest_mismatch",
                "The canonical candidate document does not match the declared content digest.");
        }

        var catalogKey = CatalogKeyFactory.Create(request.CatalogKey);
        var activeConfiguration = await catalogRepository.GetActiveConfigurationAsync(
            catalogKey,
            cancellationToken)
            ?? throw new CatalogNotFoundException(
                "Active catalog configuration",
                request.CatalogKey);
        if (activeConfiguration.RevisionId != request.ConfigurationRevisionId)
        {
            throw new CatalogContractException(
                "catalog.ingestion_draft_configuration_mismatch",
                "The Ingestion draft command does not target the exact active Catalog configuration revision.");
        }

        var createdAtUtc = clock.GetUtcNow();
        if (createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("The Catalog ingestion draft clock must return UTC.");
        }

        var proposal = CatalogIngestionDraftProposal.Create(
            RequireId(idSource.CreateId(), "draft proposal"),
            RequireId(idSource.CreateId(), "subject"),
            RequireId(idSource.CreateId(), "listing"),
            RequireId(idSource.CreateId(), "listing revision"),
            request.CommandId,
            request.CatalogKey,
            request.ConfigurationRevisionId,
            request.ImportBatchId,
            request.ItemKey,
            request.EntityKind,
            request.ContentDigest,
            createdAtUtc);
        var requestDigest = CatalogIngestionDraftCanonicalJson.ComputeDigest(new
        {
            Contract = CatalogIngestionDraftContract.Identity,
            request.CommandId,
            request.CatalogKey,
            request.ConfigurationRevisionId,
            request.ImportBatchId,
            request.ItemKey,
            request.EntityKind,
            request.ContentDigest,
            CandidateDocument = request.CandidateDocument,
        });
        var result = await draftRepository.RegisterAsync(
            proposal,
            canonicalCandidate,
            requestDigest,
            caller,
            cancellationToken);
        return ToResponse(result);
    }

    private static CatalogIngestionDraftResponse ToResponse(
        CatalogIngestionDraftRegistrationResult result)
    {
        var proposal = result.Proposal;
        return new CatalogIngestionDraftResponse(
            proposal.CommandId,
            proposal.Id,
            proposal.SubjectId,
            proposal.ListingId,
            proposal.ListingRevisionId,
            proposal.CatalogKey,
            proposal.ConfigurationRevisionId,
            proposal.ImportBatchId,
            proposal.ItemKey,
            proposal.ContentDigest,
            proposal.CreatedAtUtc,
            result.Replayed);
    }

    private static void ValidateRequest(RegisterCatalogIngestionDraftRequest request)
    {
        if (request.CommandId == Guid.Empty ||
            request.ConfigurationRevisionId == Guid.Empty ||
            request.ImportBatchId == Guid.Empty)
        {
            throw new CatalogContractException(
                "catalog.ingestion_draft_identity_required",
                "Command, configuration and import-batch identities must be non-empty.");
        }

        if (string.IsNullOrWhiteSpace(request.CatalogKey) ||
            string.IsNullOrWhiteSpace(request.ItemKey) ||
            request.ItemKey.Length > 300 ||
            request.ItemKey.Any(char.IsControl) ||
            request.EntityKind is not 1 and not 2 ||
            request.ContentDigest is not { Length: 64 } ||
            request.ContentDigest.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            request.CandidateDocument.ValueKind != JsonValueKind.Object)
        {
            throw new CatalogContractException(
                "catalog.ingestion_draft_contract_invalid",
                "The Ingestion draft command contains invalid Catalog key, item, entity kind, digest or candidate document data.");
        }

        if (request.RequestedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new CatalogContractException(
                "catalog.ingestion_draft_requested_at_invalid",
                "The Ingestion draft request timestamp must be UTC.");
        }
    }

    private static string ValidateCaller(string callerIdentity)
    {
        if (string.IsNullOrWhiteSpace(callerIdentity) ||
            callerIdentity.Length > 200 ||
            callerIdentity.Any(char.IsControl))
        {
            throw new CatalogContractException(
                "catalog.ingestion_draft_caller_invalid",
                "The authenticated Ingestion caller identity is invalid.");
        }

        return callerIdentity.Trim();
    }

    private static Guid RequireId(Guid value, string owner)
    {
        if (value == Guid.Empty)
        {
            throw new InvalidOperationException($"The Catalog {owner} ID source returned an empty identity.");
        }

        return value;
    }
}

internal static class CatalogKeyFactory
{
    public static CatalogKey Create(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var type = typeof(CatalogKey);
        var factory = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SingleOrDefault(method =>
                method.Name == "Create" &&
                method.ReturnType == type &&
                method.GetParameters() is [{ ParameterType: var parameterType }] &&
                parameterType == typeof(string));
        if (factory?.Invoke(null, [value]) is CatalogKey created)
        {
            return created;
        }

        var constructor = type.GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .SingleOrDefault(candidate =>
                candidate.GetParameters() is [{ ParameterType: var parameterType }] &&
                parameterType == typeof(string));
        if (constructor?.Invoke([value]) is CatalogKey constructed)
        {
            return constructed;
        }

        throw new InvalidOperationException(
            "CatalogKey exposes no supported string factory for the ingestion draft boundary.");
    }
}

internal static class CatalogIngestionDraftCanonicalJson
{
    public static byte[] Serialize<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var element = value is JsonElement jsonElement
            ? jsonElement
            : JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            Write(element, writer);
        }

        return buffer.ToArray();
    }

    public static string ComputeDigest<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(Serialize(value))).ToLowerInvariant();

    public static string ComputeDigest(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static void Write(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(value => value.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name.Normalize(NormalizationForm.FormC));
                    Write(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Write(item, writer);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString()!.Normalize(NormalizationForm.FormC));
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new CatalogContractException(
                    "catalog.ingestion_draft_json_invalid",
                    $"JSON value kind '{element.ValueKind}' is unsupported.");
        }
    }
}
