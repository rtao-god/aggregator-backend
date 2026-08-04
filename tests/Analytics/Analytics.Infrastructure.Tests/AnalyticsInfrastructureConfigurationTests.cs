using Aggregator.Analytics.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Analytics.Infrastructure.Tests;

public sealed class AnalyticsInfrastructureConfigurationTests
{
    [Fact]
    public void MissingOwnerConnectionStringFailsBeforeServiceProviderBuild()
    {
        var services = new ServiceCollection();
        var configuration = new EmptyConfiguration();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddAnalyticsInfrastructure(configuration));

        Assert.Contains("Connection string 'Analytics' is required", exception.Message, StringComparison.Ordinal);
    }

    private sealed class EmptyConfiguration : IConfiguration
    {
        public string? this[string key]
        {
            get => null;
            set => throw new NotSupportedException();
        }

        public IEnumerable<IConfigurationSection> GetChildren() => [];

        public IChangeToken GetReloadToken() => NeverChangeToken.Instance;

        public IConfigurationSection GetSection(string key) =>
            new EmptyConfigurationSection(key, key);
    }

    private sealed class EmptyConfigurationSection(string key, string path) : IConfigurationSection
    {
        public string Key { get; } = key;

        public string Path { get; } = path;

        public string? Value { get; set; }

        public string? this[string key]
        {
            get => null;
            set => throw new NotSupportedException();
        }

        public IEnumerable<IConfigurationSection> GetChildren() => [];

        public IChangeToken GetReloadToken() => NeverChangeToken.Instance;

        public IConfigurationSection GetSection(string key) =>
            new EmptyConfigurationSection(key, $"{Path}:{key}");
    }

    private sealed class NeverChangeToken : IChangeToken
    {
        public static NeverChangeToken Instance { get; } = new();

        public bool HasChanged => false;

        public bool ActiveChangeCallbacks => false;

        public IDisposable RegisterChangeCallback(Action<object?> callback, object? state)
        {
            ArgumentNullException.ThrowIfNull(callback);
            _ = state;
            return NoopDisposable.Instance;
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
