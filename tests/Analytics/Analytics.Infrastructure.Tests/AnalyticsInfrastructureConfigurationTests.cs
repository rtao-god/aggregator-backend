using Aggregator.Analytics.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Analytics.Infrastructure.Tests;

public sealed class AnalyticsInfrastructureConfigurationTests
{
    [Fact]
    public void MissingOwnerConnectionStringFailsBeforeServiceProviderBuild()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationManager();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddAnalyticsInfrastructure(configuration));

        Assert.Contains("Connection string 'Analytics' is required", exception.Message, StringComparison.Ordinal);
    }
}
