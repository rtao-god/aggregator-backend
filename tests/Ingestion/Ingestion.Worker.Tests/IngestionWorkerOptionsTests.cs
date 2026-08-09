using Aggregator.Ingestion.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ingestion.Worker.Tests;

public sealed class IngestionWorkerOptionsTests
{
    [Fact]
    public void ExactConfigurationCreatesBoundedOptions()
    {
        var configuration = CreateConfiguration();

        var options = IngestionWorkerOptions.FromConfiguration(configuration);

        Assert.Equal("ingestion-validation-worker", options.WorkerIdentity);
        Assert.Equal(12, options.ValidationBatchSize);
        Assert.Equal(TimeSpan.FromMinutes(4), options.LeaseDuration);
        Assert.Equal(TimeSpan.FromSeconds(3), options.EmptyDelay);
        Assert.Equal("ingestion-catalog-delivery-worker", options.CatalogDeliveryWorkerIdentity);
        Assert.Equal(20, options.CatalogDeliveryBatchSize);
        Assert.Equal(TimeSpan.FromMinutes(2), options.CatalogDeliveryLeaseDuration);
        Assert.Equal(8, options.CatalogDeliveryMaximumAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), options.CatalogDeliveryEmptyDelay);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    public void UnsafeValidationBatchIsRejected(string batchSize)
    {
        var configuration = CreateConfiguration(
            new KeyValuePair<string, string?>(
                "IngestionWorker:ValidationBatchSize",
                batchSize));

        Assert.Throws<InvalidOperationException>(() =>
            IngestionWorkerOptions.FromConfiguration(configuration));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    public void UnsafeCatalogDeliveryAttemptLimitIsRejected(string attempts)
    {
        var configuration = CreateConfiguration(
            new KeyValuePair<string, string?>(
                "IngestionWorker:CatalogDeliveryMaximumAttempts",
                attempts));

        Assert.Throws<InvalidOperationException>(() =>
            IngestionWorkerOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void MissingOwnerSettingFailsClosed()
    {
        var values = ValidValues();
        values.Remove("IngestionWorker:CatalogDeliveryLeaseDuration");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        Assert.Throws<InvalidOperationException>(() =>
            IngestionWorkerOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void CompositionRegistersValidationAndCatalogDeliveryWorkers()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var options = IngestionWorkerOptions.FromConfiguration(CreateConfiguration());
        services.AddIngestionWorker(options);
        using var provider = services.BuildServiceProvider();

        var hosted = provider.GetServices<IHostedService>().ToArray();

        Assert.Collection(
            hosted,
            service => Assert.IsType<IngestionValidationWorker>(service),
            service => Assert.IsType<IngestionCatalogDeliveryWorker>(service));
    }

    private static IConfiguration CreateConfiguration(
        params KeyValuePair<string, string?>[] overrides)
    {
        var values = ValidValues();
        foreach (var value in overrides)
        {
            values[value.Key] = value.Value;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static Dictionary<string, string?> ValidValues() =>
        new(StringComparer.Ordinal)
        {
            ["IngestionWorker:WorkerIdentity"] = "ingestion-validation-worker",
            ["IngestionWorker:ValidationBatchSize"] = "12",
            ["IngestionWorker:LeaseDuration"] = "00:04:00",
            ["IngestionWorker:EmptyDelay"] = "00:00:03",
            ["IngestionWorker:CatalogDeliveryWorkerIdentity"] = "ingestion-catalog-delivery-worker",
            ["IngestionWorker:CatalogDeliveryBatchSize"] = "20",
            ["IngestionWorker:CatalogDeliveryLeaseDuration"] = "00:02:00",
            ["IngestionWorker:CatalogDeliveryMaximumAttempts"] = "8",
            ["IngestionWorker:CatalogDeliveryEmptyDelay"] = "00:00:02",
        };
}
