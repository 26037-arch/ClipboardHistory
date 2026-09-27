namespace ClipboardHistory;

internal static class Program
{
    private static readonly TimeSpan DatabaseStartupTimeout = TimeSpan.FromSeconds(15);
    private static AppLogger? _logger;

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        using AppLogger logger = new(AppPaths.LogsDirectory);
        _logger = logger;
        RegisterGlobalExceptionHandlers();
        logger.LogProgramStarted();

        try
        {
            if (TryHandleStartupCommand(args, logger, out int exitCode))
            {
                return exitCode;
            }

            using SingleInstanceGuard instanceGuard = new();
            if (!instanceGuard.TryAcquire())
            {
                return 0;
            }

            ClipboardQueue queue = new();
            ClipboardImageQueue imageQueue = new();
            using CancellationTokenSource databaseWriterCancellation = new();
            DatabaseWriter databaseWriter = new(
                queue,
                AppPaths.DatabasePath,
                logger,
                completeWithTextQueue: false);
            Task databaseWriterTask = Task.Run(
                () => databaseWriter.RunAsync(databaseWriterCancellation.Token));

            try
            {
                databaseWriter.Ready
                    .WaitAsync(DatabaseStartupTimeout)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception)
            {
                logger.LogDatabaseInitializationFailure(exception);
                queue.Complete();
                databaseWriterCancellation.Cancel();
                WaitWithoutThrowing(databaseWriterTask, TimeSpan.FromSeconds(1));
                return 1;
            }

            TesseractOcrEngine ocrEngine = new(TesseractConfiguration.Default);
            try
            {
                TesseractAvailability availability = ocrEngine.ValidateAsync()
                    .WaitAsync(TimeSpan.FromSeconds(12))
                    .GetAwaiter()
                    .GetResult();
                logger.LogOcrAvailability(availability.IsAvailable, availability.ErrorCode);
            }
            catch (Exception exception)
            {
                logger.LogOcrAvailability(false, "validation_exception");
                logger.LogUnhandledException("ocr_validation", exception);
            }

            try
            {
                using ClipboardApplicationContext applicationContext = new(
                    queue,
                    imageQueue,
                    databaseWriter,
                    databaseWriterTask,
                    databaseWriterCancellation,
                    ocrEngine,
                    logger);

                Application.Run(applicationContext);
            }
            finally
            {
                queue.Complete();
                imageQueue.Complete();
                try
                {
                    databaseWriter.CompleteAsync()
                        .WaitAsync(TimeSpan.FromSeconds(2))
                        .GetAwaiter()
                        .GetResult();
                }
                catch
                {
                    // The application context owns the graceful writer drain.
                }
                if (!databaseWriterTask.IsCompleted)
                {
                    databaseWriterCancellation.Cancel();
                }

                WaitWithoutThrowing(databaseWriterTask, TimeSpan.FromSeconds(1));
            }

            return 0;
        }
        catch (Exception exception)
        {
            logger.LogUnhandledException("main", exception);
            return 1;
        }
        finally
        {
            logger.LogProgramStopped();
            UnregisterGlobalExceptionHandlers();
            _logger = null;
        }
    }

    private static bool TryHandleStartupCommand(
        IReadOnlyList<string> args,
        AppLogger logger,
        out int exitCode)
    {
        exitCode = 0;
        if (args.Count != 1)
        {
            return false;
        }

        StartupManager startupManager = new(logger);

        if (string.Equals(args[0], "--install-startup", StringComparison.OrdinalIgnoreCase))
        {
            exitCode = startupManager.Install() ? 0 : 1;
            return true;
        }

        if (string.Equals(args[0], "--remove-startup", StringComparison.OrdinalIgnoreCase))
        {
            exitCode = startupManager.Remove() ? 0 : 1;
            return true;
        }

        return false;
    }

    private static void RegisterGlobalExceptionHandlers()
    {
        Application.ThreadException += ApplicationOnThreadException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomainOnUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskSchedulerOnUnobservedTaskException;
    }

    private static void UnregisterGlobalExceptionHandlers()
    {
        Application.ThreadException -= ApplicationOnThreadException;
        AppDomain.CurrentDomain.UnhandledException -= CurrentDomainOnUnhandledException;
        TaskScheduler.UnobservedTaskException -= TaskSchedulerOnUnobservedTaskException;
    }

    private static void ApplicationOnThreadException(object sender, ThreadExceptionEventArgs eventArgs) =>
        _logger?.LogUnhandledException("winforms_thread", eventArgs.Exception);

    private static void CurrentDomainOnUnhandledException(object sender, UnhandledExceptionEventArgs eventArgs)
    {
        if (eventArgs.ExceptionObject is Exception exception)
        {
            _logger?.LogUnhandledException("app_domain", exception);
        }
    }

    private static void TaskSchedulerOnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs eventArgs)
    {
        _logger?.LogUnhandledException("task_scheduler", eventArgs.Exception.GetBaseException());
        eventArgs.SetObserved();
    }

    private static void WaitWithoutThrowing(Task task, TimeSpan timeout)
    {
        try
        {
            _ = task.Wait(timeout);
        }
        catch
        {
            // The originating exception is logged at its source.
        }
    }
}
