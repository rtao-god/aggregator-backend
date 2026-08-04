using Platform.Messaging;

namespace Platform.Messaging.Tests;

public sealed class OutboxDispatcherOptionsTests
{
    [Fact]
    public void ValidOwnerOptionsAreAccepted()
    {
        var options = CreateOptions();

        options.Validate();

        Assert.Equal(8, options.MaximumDeliveryAttempts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void InvalidDeliveryAttemptBudgetIsRejected(int maximumDeliveryAttempts)
    {
        var options = CreateOptions() with
        {
            MaximumDeliveryAttempts = maximumDeliveryAttempts,
        };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);

        Assert.Equal(nameof(OutboxDispatcherOptions.MaximumDeliveryAttempts), exception.ParamName);
    }

    [Fact]
    public void UnsafeSchemaIdentifierIsRejected()
    {
        var options = CreateOptions() with
        {
            Schema = "catalog;drop schema public",
        };

        var exception = Assert.Throws<ArgumentException>(options.Validate);

        Assert.Equal(nameof(OutboxDispatcherOptions.Schema), exception.ParamName);
    }

    private static OutboxDispatcherOptions CreateOptions() =>
        new()
        {
            ConnectionString = "Host=localhost;Database=catalog;Username=catalog_app;Password=test",
            Schema = "catalog",
            DispatcherIdentity = "catalog-worker-test",
        };
}
