namespace Platform.Persistence;

/// <summary>Owns access to current UTC time for application and infrastructure code.</summary>
public interface IUtcClock
{
    public DateTimeOffset GetUtcNow();
}

public sealed class SystemUtcClock : IUtcClock
{
    public DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
}

/// <summary>Owns application-side UUIDv7 generation for business identifiers.</summary>
public interface IBusinessIdFactory
{
    public Guid Create();
}

public sealed class UuidV7BusinessIdFactory : IBusinessIdFactory
{
    public Guid Create() => Guid.CreateVersion7();
}
