namespace ClipboardHistory;

internal sealed class HistoryRetentionService : IDisposable
{
    public static readonly TimeSpan TrashRetention = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(24);

    private readonly DatabaseWriter _writer;
    private readonly ClipboardHistoryRepository _repository;
    private readonly AppSettingsRepository _settingsRepository;
    private readonly ClipboardImageStore _imageStore;
    private readonly ImageStorageReconciler _reconciler;
    private readonly StorageUsageService _storageUsage;
    private readonly AppLogger _logger;
    private readonly TimeZoneInfo _timeZone;
    private readonly SemaphoreSlim _maintenanceGate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loopTask;
    private int _disposed;

    public HistoryRetentionService(
        DatabaseWriter writer,
        ClipboardHistoryRepository repository,
        AppSettingsRepository settingsRepository,
        ClipboardImageStore imageStore,
        ImageStorageReconciler reconciler,
        StorageUsageService storageUsage,
        AppLogger logger,
        TimeZoneInfo? timeZone = null)
    {
        _writer = writer;
        _repository = repository;
        _settingsRepository = settingsRepository;
        _imageStore = imageStore;
        _reconciler = reconciler;
        _storageUsage = storageUsage;
        _logger = logger;
        _timeZone = timeZone ?? TimeZoneInfo.Local;
    }

    public Task Completion => _loopTask ?? Task.CompletedTask;

    internal bool IsMaintenanceRunning => _maintenanceGate.CurrentCount == 0;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _loopTask ??= Task.Run(() => RunLoopAsync(_stopping.Token));
    }

    public async Task<bool> RunNowAsync(CancellationToken cancellationToken = default)
    {
        if (_stopping.IsCancellationRequested ||
            !await _maintenanceGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            AppSettings settings = await _settingsRepository.LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (settings.AutomaticDeletionEnabled)
            {
                DateTimeOffset cutoff = HistoryDateRangeCalculator.CalculateRetentionCutoffUtc(
                    nowUtc,
                    _timeZone,
                    settings.RetentionPeriod);
                await _writer.SoftDeleteOlderThanAsync(
                    cutoff,
                    nowUtc,
                    cancellationToken).ConfigureAwait(false);
            }

            IReadOnlyList<ImageFileReference> unreferenced = await _writer
                .PermanentlyDeleteExpiredAsync(
                    nowUtc - TrashRetention,
                    cancellationToken)
                .ConfigureAwait(false);
            await DeleteUnreferencedFilesAsync(unreferenced, cancellationToken).ConfigureAwait(false);
            await _reconciler.ReconcileAsync(cancellationToken).ConfigureAwait(false);
            _storageUsage.Invalidate();
            return true;
        }
        finally
        {
            _maintenanceGate.Release();
        }
    }

    public async Task EmptyTrashAsync(CancellationToken cancellationToken = default)
    {
        await _maintenanceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<ImageFileReference> unreferenced = await _writer
                .EmptyTrashAsync(cancellationToken)
                .ConfigureAwait(false);
            await DeleteUnreferencedFilesAsync(unreferenced, cancellationToken).ConfigureAwait(false);
            _storageUsage.Invalidate();
        }
        finally
        {
            _maintenanceGate.Release();
        }
    }

    public void Stop() => _stopping.Cancel();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopping.Cancel();
        // The background loop may still be unwinding after a forced shutdown timeout.
        // Avoid disposing synchronization primitives while it can still release them.
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunSafelyAsync(cancellationToken).ConfigureAwait(false);
            using PeriodicTimer timer = new(MaintenanceInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await RunSafelyAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunNowAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogUnhandledException("retention_service", exception);
        }
    }

    private async Task DeleteUnreferencedFilesAsync(
        IReadOnlyList<ImageFileReference> files,
        CancellationToken cancellationToken)
    {
        foreach (ImageFileReference file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (_imageStore.TryDelete(file.RelativePath))
                {
                    await _writer.ClearOrphanAsync(file.RelativePath, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await _writer.MarkOrphanAsync(file.RelativePath, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (InvalidDataException)
            {
                // A database path outside the managed root is never touched.
            }
        }
    }
}
