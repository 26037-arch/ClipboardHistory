namespace ClipboardHistory;

internal sealed class ClipboardApplicationContext : ApplicationContext
{
    private static readonly TimeSpan CaptureDrainTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ImageDrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OcrDrainTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan WriterDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly ClipboardQueue _textQueue;
    private readonly ClipboardImageQueue _imageQueue;
    private readonly DatabaseWriter _databaseWriter;
    private readonly Task _databaseWriterTask;
    private readonly CancellationTokenSource _databaseWriterCancellation;
    private readonly CancellationTokenSource _imageCancellation = new();
    private readonly CancellationTokenSource _ocrCancellation = new();
    private readonly AppLogger _logger;
    private readonly ClipboardListener _listener;
    private readonly ClipboardPopupController _popupController;
    private readonly HistoryManagerController _historyManagerController;
    private readonly HistoryRetentionService _retentionService;
    private readonly OcrWorker _ocrWorker;
    private readonly Task _ocrTask;
    private readonly Task _imageProcessorTask;

    private bool _shutdownStarted;
    private bool _resourcesDisposed;

    public ClipboardApplicationContext(
        ClipboardQueue textQueue,
        ClipboardImageQueue imageQueue,
        DatabaseWriter databaseWriter,
        Task databaseWriterTask,
        CancellationTokenSource databaseWriterCancellation,
        TesseractOcrEngine ocrEngine,
        AppLogger logger)
    {
        _textQueue = textQueue;
        _imageQueue = imageQueue;
        _databaseWriter = databaseWriter;
        _databaseWriterTask = databaseWriterTask;
        _databaseWriterCancellation = databaseWriterCancellation;
        _logger = logger;

        ClipboardHistoryRepository repository = new(AppPaths.DatabasePath);
        ClipboardImageStore imageStore = new(AppPaths.ImagesDirectory);
        StorageUsageService storageUsage = new(AppPaths.DatabasePath, imageStore);
        AppSettingsRepository settingsRepository = new(AppPaths.DatabasePath, databaseWriter);
        ClipboardSelfWriteSuppressor textSuppressor = new();
        ImageClipboardSelfWriteSuppressor imageSuppressor = new();
        ClipboardRestoreService restoreService = new(imageStore, textSuppressor, imageSuppressor);
        ImageStorageReconciler reconciler = new(imageStore, repository, databaseWriter);

        _ocrWorker = new OcrWorker(repository, databaseWriter, imageStore, ocrEngine, logger);
        _ocrTask = Task.Run(() => _ocrWorker.RunAsync(_ocrCancellation.Token));
        ClipboardImageProcessor imageProcessor = new(
            imageQueue,
            imageStore,
            databaseWriter,
            repository,
            _ocrWorker,
            ocrEngine,
            imageSuppressor,
            storageUsage,
            logger);
        _imageProcessorTask = Task.Run(() => imageProcessor.RunAsync(_imageCancellation.Token));

        _retentionService = new HistoryRetentionService(
            databaseWriter,
            repository,
            settingsRepository,
            imageStore,
            reconciler,
            storageUsage,
            logger);
        _retentionService.Start();

        _historyManagerController = new HistoryManagerController(() => new HistoryManagerForm(
            repository,
            databaseWriter,
            settingsRepository,
            storageUsage,
            _retentionService,
            restoreService,
            imageStore,
            logger));
        _popupController = new ClipboardPopupController(() => new ClipboardHistoryPopup(
            repository,
            restoreService,
            imageStore,
            _historyManagerController.ShowOrActivate,
            logger));

        try
        {
            _listener = new ClipboardListener(
                textQueue,
                imageQueue,
                logger,
                textSuppressor,
                TogglePopup,
                RequestShutdown);
        }
        catch
        {
            _retentionService.Stop();
            _imageQueue.Complete();
            _ocrWorker.Complete();
            _imageCancellation.Cancel();
            _ocrCancellation.Cancel();
            _popupController.Dispose();
            _historyManagerController.Dispose();
            _retentionService.Dispose();
            throw;
        }
    }

    public async void RequestShutdown()
    {
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        RunCleanupStep(_listener.StopHotkey, "shutdown_hotkey");
        RunCleanupStep(_popupController.Dispose, "shutdown_popup");
        RunCleanupStep(_historyManagerController.Dispose, "shutdown_history_manager");
        RunCleanupStep(_listener.StopListening, "shutdown_clipboard_listener");
        _retentionService.Stop();

        try
        {
            await WaitOrCancelAsync(
                _listener.CaptureProcessingCompletion,
                CaptureDrainTimeout,
                cancellation: null,
                "clipboard_capture_task");
            _textQueue.Complete();
            _imageQueue.Complete();

            await WaitOrCancelAsync(
                _imageProcessorTask,
                ImageDrainTimeout,
                _imageCancellation,
                "image_processor_task");

            _ocrWorker.Complete();
            await WaitOrCancelAsync(
                _ocrTask,
                OcrDrainTimeout,
                _ocrCancellation,
                "ocr_worker_task");

            await WaitOrCancelAsync(
                _retentionService.Completion,
                TimeSpan.FromSeconds(1),
                cancellation: null,
                "retention_task");

            await _databaseWriter.CompleteAsync();
            await WaitOrCancelAsync(
                _databaseWriterTask,
                WriterDrainTimeout,
                _databaseWriterCancellation,
                "database_writer_task");
        }
        catch (Exception exception)
        {
            _logger.LogUnhandledException("shutdown", exception);
        }
        finally
        {
            DisposeResources();
            ExitThread();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeResources();
        }

        base.Dispose(disposing);
    }

    private async Task WaitOrCancelAsync(
        Task task,
        TimeSpan timeout,
        CancellationTokenSource? cancellation,
        string source)
    {
        try
        {
            await task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            cancellation?.Cancel();
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (Exception exception)
            {
                _logger.LogUnhandledException(source, exception);
            }
        }
        catch (Exception exception)
        {
            _logger.LogUnhandledException(source, exception.GetBaseException());
        }
    }

    private void DisposeResources()
    {
        if (_resourcesDisposed)
        {
            return;
        }

        _resourcesDisposed = true;
        RunCleanupStep(_listener.StopHotkey, "dispose_hotkey");
        RunCleanupStep(_popupController.Dispose, "dispose_popup");
        RunCleanupStep(_historyManagerController.Dispose, "dispose_history_manager");
        RunCleanupStep(_listener.StopListening, "dispose_clipboard_listener");
        _retentionService.Stop();
        _imageQueue.Complete();
        _ocrWorker.Complete();
        _imageCancellation.Cancel();
        _ocrCancellation.Cancel();
        RunCleanupStep(_listener.Dispose, "dispose_listener_window");
        RunCleanupStep(_retentionService.Dispose, "dispose_retention");
        _imageCancellation.Dispose();
        _ocrCancellation.Dispose();
    }

    private void TogglePopup()
    {
        if (_shutdownStarted)
        {
            return;
        }

        try
        {
            _popupController.Toggle();
        }
        catch (Exception exception)
        {
            _logger.LogUnhandledException("clipboard_popup", exception);
        }
    }

    private void RunCleanupStep(Action cleanup, string source)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            _logger.LogUnhandledException(source, exception);
        }
    }
}
