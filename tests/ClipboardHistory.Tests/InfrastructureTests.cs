namespace ClipboardHistory.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public void Queue_IsBoundedAndTryEnqueueNeverWaits()
    {
        ClipboardQueue queue = new(capacity: 1);
        ClipboardRecord first = new("first", DateTimeOffset.UtcNow);
        ClipboardRecord second = new("second", DateTimeOffset.UtcNow);

        Assert.True(queue.TryEnqueue(first));
        Assert.False(queue.TryEnqueue(second));
        queue.Complete();
    }

    [Fact]
    public void SingleInstanceGuard_AllowsOnlyOneOwnerForSameName()
    {
        string mutexName = $@"Local\ClipboardHistory.Tests.{Guid.NewGuid():N}";
        using SingleInstanceGuard first = new(mutexName);
        using SingleInstanceGuard second = new(mutexName);

        Assert.True(first.TryAcquire());
        Assert.False(second.TryAcquire());
    }

    [Fact]
    public void StartupManager_InstallsAndRemovesOnlyItsShortcut()
    {
        using TemporaryDirectory temporaryDirectory = new();
        string startupDirectory = Path.Combine(temporaryDirectory.Path, "Startup");
        string executablePath = Path.Combine(temporaryDirectory.Path, "ClipboardHistory.exe");
        File.WriteAllBytes(executablePath, [0]);

        using AppLogger logger = new(Path.Combine(temporaryDirectory.Path, "logs"));
        StartupManager manager = new(logger, startupDirectory, executablePath);

        Assert.True(manager.Install());
        Assert.True(File.Exists(manager.ShortcutPath));
        Assert.True(manager.Install());

        string unrelatedShortcut = Path.Combine(startupDirectory, "Unrelated.lnk");
        File.WriteAllText(unrelatedShortcut, "unrelated");

        Assert.True(manager.Remove());
        Assert.False(File.Exists(manager.ShortcutPath));
        Assert.True(File.Exists(unrelatedShortcut));
        Assert.True(manager.Remove());
    }
}
