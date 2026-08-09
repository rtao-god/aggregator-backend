using System.Net;
using Aggregator.Catalog.Application;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aggregator.Catalog.Infrastructure;

/// <summary>Classifies only proven transient infrastructure failures for Catalog publication retry.</summary>
public sealed class CatalogPublicationOperationFailureClassifier
    : ICatalogPublicationOperationFailureClassifier
{
    public CatalogPublicationOperationFailureDecision Classify(
        Exception exception,
        int attempt,
        int maximumAttempts,
        DateTimeOffset failedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumAttempts);
        if (failedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Failure timestamp must be normalized to UTC.", nameof(failedAtUtc));
        }

        if (CatalogFailureTranslator.TryTranslate(exception, out var ownerFailure))
        {
            return Terminal(CatalogPublicationOperationFailure.Create(
                ownerFailure.Owner,
                ownerFailure.Code,
                ownerFailure.Detail,
                ownerFailure.RequiredAction));
        }

        if (IsTransient(exception))
        {
            if (attempt < maximumAttempts)
            {
                return new CatalogPublicationOperationFailureDecision(
                    true,
                    failedAtUtc.Add(ComputeRetryDelay(attempt)),
                    CatalogPublicationOperationFailure.Create(
                        "Catalog.Publications",
                        "CATALOG_PUBLICATION_INFRASTRUCTURE_TRANSIENT",
                        exception.Message,
                        "Wait for the bounded retry or restore the unavailable Catalog database/object-storage dependency."));
            }

            return Terminal(CatalogPublicationOperationFailure.Create(
                "Catalog.Publications",
                "CATALOG_PUBLICATION_RETRY_EXHAUSTED",
                exception.Message,
                "Restore the failing Catalog infrastructure dependency and create a new publication request after reviewing the retained attempts."));
        }

        return Terminal(CatalogPublicationOperationFailure.Create(
            "Catalog.Publications",
            "CATALOG_PUBLICATION_UNCLASSIFIED_FAILURE",
            exception.Message,
            "Inspect the Catalog publication worker failure and correct the owning code or state before submitting another request."));
    }

    private static CatalogPublicationOperationFailureDecision Terminal(
        CatalogPublicationOperationFailure failure) =>
        new(false, null, failure);

    private static bool IsTransient(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is NpgsqlException { IsTransient: true } ||
                current is TimeoutException or HttpRequestException or IOException or OperationCanceledException)
            {
                return true;
            }

            if (current is DbUpdateException dbUpdate &&
                dbUpdate.InnerException is NpgsqlException { IsTransient: true })
            {
                return true;
            }

            if (current is AmazonS3Exception s3 && IsTransientStatusCode(s3.StatusCode))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static TimeSpan ComputeRetryDelay(int attempt)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 5);
        var seconds = Math.Min(120, 5 * (1 << exponent));
        return TimeSpan.FromSeconds(seconds);
    }
}
