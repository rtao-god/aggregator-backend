using Aggregator.Catalog.Contracts;

namespace Aggregator.Query.Application;

public interface IQueryCatalogPublicationHandler
{
    public Task HandleAsync(
        CatalogPublicationActivated publication,
        CancellationToken cancellationToken);
}

public sealed class QueryPublicationConsumerException : InvalidOperationException
{
    public QueryPublicationConsumerException(
        string code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}
