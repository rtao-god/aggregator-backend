using Aggregator.Query.Application;

namespace Aggregator.Query.Infrastructure;

public sealed class SystemQueryClock : IQueryClock
{
    public DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
}

public sealed class UuidV7QueryIdFactory : IQueryIdFactory
{
    public Guid Create() => Guid.CreateVersion7();
}
