using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Aggregator.Ingestion.Collector.Contracts;
using Aggregator.Ingestion.Collector.Domain;

namespace Aggregator.Ingestion.Collector.Application;

public sealed record CollectorCandidateRegistration(
    CollectorCandidate Candidate,
    bool Replayed);

public interface ICollectorCandidateStore
{
    public Task<CollectorCandidateRegistration> RegisterAsync(
        Guid commandId,
        string commandDigest,
        CollectorCandidate candidate,
        CancellationToken cancellationToken);

    public Task<bool> CheckReadinessAsync(CancellationToken cancellationToken);
}

public interface ICollectorCandidateIdSource
{
    public Guid CreateId();
}

public sealed record CollectorCandidateOptions
{
    public TimeSpan MaximumFutureSkew { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan MaximumObservationAge { get; init; } = TimeSpan.FromDays(366);

    public void Validate()
    {
        if (MaximumFutureSkew < TimeSpan.Zero || MaximumFutureSkew > TimeSpan.FromHours(1))
        {
            throw new InvalidOperationException(
                "Collector maximum future skew must be between zero and one hour.");
        }

        if (MaximumObservationAge < TimeSpan.FromDays(1) ||
            MaximumObservationAge > TimeSpan.FromDays(3660))
        {
            throw new InvalidOperationException(
                "Collector maximum observation age must be between one and 3,660 days.");
        }
    }
}

public sealed class CollectorCandidateException : InvalidOperationException
{
    public CollectorCandidateException(
        string code,
        int statusCode,
        string message,
        string requiredAction,
        IReadOnlyDictionary<string, object?>? context = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredAction);
        Code = code;
        StatusCode = statusCode;
        RequiredAction = requiredAction;
        Context = context ?? new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    public string Code { get; }

    public int StatusCode { get; }

    public string RequiredAction { get; }

    public IReadOnlyDictionary<string, object?> Context { get; }
}

public sealed class CollectorCandidateService(
    ICollectorCandidateStore store,
    ICollectorCandidateIdSource idSource,
    CollectorCandidateOptions options,
    TimeProvider timeProvider)
{
    private static readonly Regex SourceSystemPattern = new(
        "^[a-z0-9]+(?:-[a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public async Task<CollectorCandidateResponse> SubmitAsync(
        SubmitCollectorCandidateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        options.Validate();
        if (request.CommandId == Guid.Empty)
        {
            throw Failure(
                "COLLECTOR_COMMAND_ID_INVALID",
                400,
                "Collector command ID is required.",
                "Generate one UUIDv7 command ID and replay only the exact same request under it.");
        }

        var normalized = Normalize(request);
        var now = timeProvider.GetUtcNow();
        if (normalized.ObservedAtUtc > now + options.MaximumFutureSkew)
        {
            throw Failure(
                "COLLECTOR_OBSERVATION_FROM_FUTURE",
                422,
                "Collector observation timestamp exceeds the accepted future skew.",
                "Correct the collector clock and submit a new exact command.");
        }

        if (normalized.ObservedAtUtc < now - options.MaximumObservationAge)
        {
            throw Failure(
                "COLLECTOR_OBSERVATION_TOO_OLD",
                422,
                "Collector observation is outside the configured intake window.",
                "Do not submit observations outside the owner-approved intake period.");
        }

        var commandDigest = ComputeDigest(normalized);
        var contentDigest = ComputeDigest(new
        {
            normalized.SourceSystem,
            normalized.SourceReference,
            normalized.ObservedAtUtc,
            normalized.Kind,
            normalized.ExternalId,
            normalized.Title,
            normalized.Website,
            normalized.HourlyPrice,
            normalized.EvidenceDigest,
        });
        var candidate = CollectorCandidate.Create(
            idSource.CreateId(),
            idSource.CreateId(),
            idSource.CreateId(),
            normalized.SourceSystem,
            normalized.SourceReference,
            normalized.ObservedAtUtc,
            MapKind(normalized.Kind),
            normalized.ExternalId,
            normalized.Title,
            new Uri(normalized.Website, UriKind.Absolute),
            normalized.HourlyPrice,
            normalized.EvidenceDigest,
            contentDigest,
            now);
        var registration = await store.RegisterAsync(
            normalized.CommandId,
            commandDigest,
            candidate,
            cancellationToken);
        return MapResponse(normalized.CommandId, registration);
    }

    private static SubmitCollectorCandidateRequest Normalize(
        SubmitCollectorCandidateRequest request)
    {
        var sourceSystem = RequireText(request.SourceSystem, 96, "sourceSystem")
            .ToLowerInvariant();
        if (!SourceSystemPattern.IsMatch(sourceSystem))
        {
            throw Failure(
                "COLLECTOR_SOURCE_SYSTEM_INVALID",
                400,
                "Collector source system is not a normalized lower-case identifier.",
                "Submit a lower-case hyphen-separated source system key.");
        }

        var sourceReference = RequireText(request.SourceReference, 2048, "sourceReference");
        var externalId = RequireText(request.ExternalId, 256, "externalId");
        var title = RequireText(request.Title, 300, "title");
        if (!Enum.IsDefined(request.Kind))
        {
            throw Failure(
                "COLLECTOR_CANDIDATE_KIND_INVALID",
                400,
                "Collector candidate kind is unsupported.",
                "Submit one of the declared collector candidate kinds.");
        }

        if (request.ObservedAtUtc.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "COLLECTOR_OBSERVATION_NOT_UTC",
                400,
                "Collector observation timestamp must use UTC.",
                "Normalize the observed timestamp to UTC.");
        }

        if (!Uri.TryCreate(request.Website?.Trim(), UriKind.Absolute, out var website) ||
            website.Scheme is not ("http" or "https"))
        {
            throw Failure(
                "COLLECTOR_WEBSITE_INVALID",
                400,
                "Collector website must be an absolute HTTP URL.",
                "Submit the canonical public website reference.");
        }

        if (request.HourlyPrice is < 0 or > 1_000_000)
        {
            throw Failure(
                "COLLECTOR_HOURLY_PRICE_INVALID",
                400,
                "Collector hourly price must be between zero and 1,000,000.",
                "Correct the normalized monetary observation.");
        }

        var evidenceDigest = RequireDigest(request.EvidenceDigest);
        return new SubmitCollectorCandidateRequest(
            request.CommandId,
            sourceSystem,
            sourceReference,
            request.ObservedAtUtc,
            request.Kind,
            externalId,
            title,
            website.AbsoluteUri,
            request.HourlyPrice,
            evidenceDigest);
    }

    private static string RequireText(string value, int maximumLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Failure(
                "COLLECTOR_TEXT_REQUIRED",
                400,
                $"Collector field '{field}' is required.",
                "Submit a non-empty bounded value.");
        }

        var normalized = value.Trim().Normalize(NormalizationForm.FormC);
        if (normalized.Length > maximumLength)
        {
            throw Failure(
                "COLLECTOR_TEXT_TOO_LONG",
                400,
                $"Collector field '{field}' exceeds {maximumLength} characters.",
                "Reduce the field to the declared contract limit.");
        }

        return normalized;
    }

    private static string RequireDigest(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length != 64 ||
            value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw Failure(
                "COLLECTOR_EVIDENCE_DIGEST_INVALID",
                400,
                "Evidence digest must be a lowercase SHA-256 hexadecimal value.",
                "Submit the digest of the exact collector evidence payload.");
        }

        return value;
    }

    private static CollectorCandidateKind MapKind(CollectorCandidateKindContract kind) =>
        kind switch
        {
            CollectorCandidateKindContract.Place => CollectorCandidateKind.Place,
            CollectorCandidateKindContract.Provider => CollectorCandidateKind.Provider,
            _ => throw Failure(
                "COLLECTOR_CANDIDATE_KIND_UNSUPPORTED",
                400,
                $"Collector candidate kind '{kind}' is unsupported.",
                "Upgrade the collector contract before submitting this candidate kind."),
        };

    private static CollectorCandidateResponse MapResponse(
        Guid commandId,
        CollectorCandidateRegistration registration)
    {
        var candidate = registration.Candidate;
        return new CollectorCandidateResponse(
            commandId,
            candidate.CandidateId,
            candidate.SubjectId,
            candidate.SubjectRevisionId,
            candidate.SourceSystem,
            candidate.SourceReference,
            candidate.ObservedAtUtc,
            candidate.Kind switch
            {
                CollectorCandidateKind.Place => CollectorCandidateKindContract.Place,
                CollectorCandidateKind.Provider => CollectorCandidateKindContract.Provider,
                _ => throw new InvalidOperationException(
                    $"Collector domain kind '{candidate.Kind}' cannot be mapped."),
            },
            candidate.ExternalId,
            candidate.Title,
            candidate.Website.AbsoluteUri,
            candidate.HourlyPrice,
            candidate.EvidenceDigest,
            candidate.ContentDigest,
            candidate.AcceptedAtUtc,
            registration.Replayed);
    }

    private static string ComputeDigest<T>(T value) =>
        Convert.ToHexStringLower(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions)));

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };
        serializerOptions.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return serializerOptions;
    }

    private static CollectorCandidateException Failure(
        string code,
        int statusCode,
        string message,
        string requiredAction,
        IReadOnlyDictionary<string, object?>? context = null,
        Exception? innerException = null) =>
        new(code, statusCode, message, requiredAction, context, innerException);
}

public sealed class UuidV7CollectorCandidateIdSource : ICollectorCandidateIdSource
{
    public Guid CreateId() => Guid.CreateVersion7();
}
