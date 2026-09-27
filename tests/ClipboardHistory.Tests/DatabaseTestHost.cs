namespace ClipboardHistory.Tests;

internal sealed class DatabaseTestHost : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellation;

    private DatabaseTestHost(
        string databasePath,
        AppLogger logger,
        ClipboardQueue queue,
        DatabaseWriter writer,
        Task writerTask,
        CancellationTokenSource cancellation)
    {
        DatabasePath = databasePath;
        Logger = logger;
        Queue = queue;
        Writer = writer;
        WriterTask = writerTask;
        _cancellation = cancellation;
        Repository = new ClipboardHistoryRepository(databasePath);
    }

    public string DatabasePath { get; }

    public AppLogger Logger { get; }

    public ClipboardQueue Queue { get; }

    public DatabaseWriter Writer { get; }

    public Task WriterTask { get; }

    public ClipboardHistoryRepository Repository { get; }

    public static async Task<DatabaseTestHost> CreateAsync(string rootPath)
    {
        string databasePath = Path.Combine(rootPath, "clipboard.db");
        AppLogger logger = new(Path.Combine(rootPath, "logs"));
        ClipboardQueue queue = new(capacity: 32);
        DatabaseWriter writer = new(
            queue,
            databasePath,
            logger,
            completeWithTextQueue: false);
        CancellationTokenSource startCancellation = new(TimeSpan.FromSeconds(20));
        Task writerTask = Task.Run(() => writer.RunAsync(startCancellation.Token));
        await writer.Ready.WaitAsync(startCancellation.Token);
        return new DatabaseTestHost(
            databasePath,
            logger,
            queue,
            writer,
            writerTask,
            startCancellation);
    }

    public async ValueTask DisposeAsync()
    {
        Queue.Complete();
        await Writer.CompleteAsync();
        try
        {
            await WriterTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
            _cancellation.Cancel();
        }

        Logger.Dispose();
        _cancellation.Dispose();
    }
}
