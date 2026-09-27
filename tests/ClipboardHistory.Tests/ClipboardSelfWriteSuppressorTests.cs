namespace ClipboardHistory.Tests;

public sealed class ClipboardSelfWriteSuppressorTests
{
    [Fact]
    public void MatchingHash_IsSuppressedExactlyOnce()
    {
        ClipboardSelfWriteSuppressor suppressor = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        suppressor.Arm("restored text", now);

        Assert.True(suppressor.TryConsume("restored text", now.AddMilliseconds(10)));
        Assert.False(suppressor.TryConsume("restored text", now.AddMilliseconds(20)));
    }

    [Fact]
    public void DifferentHash_IsNotSuppressedAndClearsExpectation()
    {
        ClipboardSelfWriteSuppressor suppressor = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        suppressor.Arm("expected", now);

        Assert.False(suppressor.TryConsume("external", now.AddMilliseconds(10)));
        Assert.False(suppressor.TryConsume("expected", now.AddMilliseconds(20)));
    }

    [Fact]
    public void ExpiredExpectation_DoesNotSuppressClipboardChange()
    {
        ClipboardSelfWriteSuppressor suppressor = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        suppressor.Arm("expected", now, TimeSpan.FromMilliseconds(50));

        Assert.False(suppressor.TryConsume("expected", now.AddMilliseconds(50)));
    }
}
