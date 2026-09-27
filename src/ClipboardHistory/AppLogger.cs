using System.Globalization;

namespace ClipboardHistory;

internal sealed class AppLogger : IDisposable
{
    private const long DefaultMaximumBytes = 1_048_576;
    private const int DefaultRetainedFiles = 5;

    private readonly object _sync = new();
    private readonly string _logPath;
    private readonly long _maximumBytes;
    private readonly int _retainedFiles;
    private bool _enabled;

    public AppLogger(
        string logsDirectory,
        long maximumBytes = DefaultMaximumBytes,
        int retainedFiles = DefaultRetainedFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);

        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        if (retainedFiles <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedFiles));
        }

        _logPath = Path.Combine(logsDirectory, "clipboard-history.log");
        _maximumBytes = maximumBytes;
        _retainedFiles = retainedFiles;

        try
        {
            Directory.CreateDirectory(logsDirectory);
            RotateIfNecessary();
            _enabled = true;
        }
        catch
        {
            _enabled = false;
        }
    }

    public void LogProgramStarted() => Write("program_started");

    public void LogProgramStopped() => Write("program_stopped");

    public void LogDatabaseInitializationFailure(Exception exception) =>
        WriteException("database_initialization_failed", exception);

    public void LogUnhandledException(string source, Exception exception) =>
        WriteException($"unhandled_exception source={SanitizeToken(source)}", exception);

    public void LogStartupResult(string operation, bool succeeded, Exception? exception = null)
    {
        string prefix = $"startup_{SanitizeToken(operation)} result={(succeeded ? "success" : "failure")}";
        if (exception is null)
        {
            Write(prefix);
        }
        else
        {
            WriteException(prefix, exception);
        }
    }

    public void LogConsecutiveClipboardAccessFailures(int failureCount) =>
        Write($"clipboard_access_failed_consecutively count={failureCount.ToString(CultureInfo.InvariantCulture)}");

    public void LogHotkeyRegistrationFailure(int errorCode) =>
        Write($"hotkey_registration_failed win32_error={errorCode.ToString(CultureInfo.InvariantCulture)}");

    public void LogHotkeyUnregistrationFailure(int errorCode) =>
        Write($"hotkey_unregistration_failed win32_error={errorCode.ToString(CultureInfo.InvariantCulture)}");

    public void LogClipboardSearchFailure(Exception exception) =>
        WriteException("clipboard_search_failed", exception);

    public void LogClipboardRestoreFailure(Exception exception) =>
        WriteException("clipboard_restore_failed", exception);

    public void LogOcrAvailability(bool available, string? errorCode) =>
        Write(
            $"ocr_availability available={(available ? "true" : "false")} " +
            $"code={SanitizeToken(errorCode ?? "none")}");

    public void Dispose()
    {
        // Each write opens and closes the file so that a forced process exit does not retain a handle.
    }

    private void WriteException(string eventName, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // Do not include exception messages or data: they can contain user-controlled clipboard text.
        Write(
            $"{eventName} exception={SanitizeToken(exception.GetType().FullName ?? exception.GetType().Name)} " +
            $"hresult=0x{exception.HResult:X8}");
    }

    private void Write(string message)
    {
        lock (_sync)
        {
            if (!_enabled)
            {
                return;
            }

            try
            {
                RotateIfNecessary();
                string line = $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}";
                File.AppendAllText(_logPath, line);
            }
            catch
            {
                _enabled = false;
            }
        }
    }

    private void RotateIfNecessary()
    {
        if (!File.Exists(_logPath) || new FileInfo(_logPath).Length < _maximumBytes)
        {
            return;
        }

        string oldest = GetArchivePath(_retainedFiles - 1);
        if (_retainedFiles > 1 && File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (int index = _retainedFiles - 2; index >= 1; index--)
        {
            string source = GetArchivePath(index);
            if (File.Exists(source))
            {
                File.Move(source, GetArchivePath(index + 1), overwrite: true);
            }
        }

        if (_retainedFiles == 1)
        {
            File.Delete(_logPath);
        }
        else
        {
            File.Move(_logPath, GetArchivePath(1), overwrite: true);
        }
    }

    private string GetArchivePath(int index) => $"{_logPath}.{index}";

    private static string SanitizeToken(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        int length = 0;

        foreach (char character in value)
        {
            buffer[length++] = char.IsLetterOrDigit(character) || character is '.' or '_' or '-'
                ? character
                : '_';
        }

        return new string(buffer[..length]);
    }
}
