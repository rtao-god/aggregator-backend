using Platform.Messaging;

namespace Platform.Messaging.Tests;

public sealed class OutboxDispatchFailurePolicyTests
{
    private static readonly Guid MessageId =
        Guid.Parse("01990000-0000-7000-8000-000000000001");
    private static readonly Guid LeaseToken =
        Guid.Parse("01990000-0000-7000-8000-000000000002");

    [Fact]
    public void RecordedDispatchAttemptIsRecoverableWithExactTerminalState()
    {
        var inner = new InvalidOperationException("payload digest mismatch");
        var exception = new OutboxDispatchAttemptException(
            MessageId,
            deadLettered: true,
            inner);

        Assert.True(OutboxDispatchFailurePolicy.IsRecoverable(exception));
        Assert.Equal(MessageId, exception.MessageId);
        Assert.True(exception.DeadLettered);
        Assert.Same(inner, exception.InnerException);
        Assert.Contains("payload digest mismatch", exception.Message, StringComparison.Ordinal);
        Assert.Contains("was dead-lettered", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LostLeaseAndTransientIoRemainRecoverable()
    {
        var leaseLost = new OutboxLeaseLostException(
            MessageId,
            LeaseToken,
            "messaging-test");

        Assert.True(OutboxDispatchFailurePolicy.IsRecoverable(leaseLost));
        Assert.True(OutboxDispatchFailurePolicy.IsRecoverable(new TimeoutException()));
        Assert.True(OutboxDispatchFailurePolicy.IsRecoverable(new IOException("network reset")));
    }

    [Fact]
    public void UnknownProgrammingFailureRemainsFailFast()
    {
        var exception = new InvalidOperationException("unexpected mapping defect");

        Assert.False(OutboxDispatchFailurePolicy.IsRecoverable(exception));
    }

    [Fact]
    public void AggregateFailureIsRecoverableOnlyWhenEveryInnerFailureIsRecoverable()
    {
        var recoverable = new AggregateException(
            new TimeoutException(),
            new IOException("network reset"));
        var mixed = new AggregateException(
            new TimeoutException(),
            new InvalidOperationException("programming defect"));

        Assert.True(OutboxDispatchFailurePolicy.IsRecoverable(recoverable));
        Assert.False(OutboxDispatchFailurePolicy.IsRecoverable(mixed));
    }
}
