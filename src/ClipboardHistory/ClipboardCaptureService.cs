using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace ClipboardHistory;

internal enum ClipboardReadResult
{
    Success,
    NoUnicodeText,
    AccessFailed,
}

internal interface IClipboardTextReader
{
    ClipboardReadResult TryReadUnicodeText(IntPtr clipboardOwner, out string? text);
}

internal sealed class NativeClipboardTextReader : IClipboardTextReader
{
    public ClipboardReadResult TryReadUnicodeText(IntPtr clipboardOwner, out string? text)
    {
        text = null;

        if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CfUnicodeText))
        {
            return ClipboardReadResult.NoUnicodeText;
        }

        if (!NativeMethods.OpenClipboard(clipboardOwner))
        {
            return ClipboardReadResult.AccessFailed;
        }

        try
        {
            IntPtr clipboardData = NativeMethods.GetClipboardData(NativeMethods.CfUnicodeText);
            if (clipboardData == IntPtr.Zero)
            {
                return ClipboardReadResult.AccessFailed;
            }

            UIntPtr byteSize = NativeMethods.GlobalSize(clipboardData);
            ulong byteCount = byteSize.ToUInt64();
            if (byteCount < sizeof(char) || byteCount > int.MaxValue)
            {
                return ClipboardReadResult.AccessFailed;
            }

            IntPtr memory = NativeMethods.GlobalLock(clipboardData);
            if (memory == IntPtr.Zero)
            {
                return ClipboardReadResult.AccessFailed;
            }

            try
            {
                int characterCount = checked((int)(byteCount / sizeof(char)));
                string copiedValue = Marshal.PtrToStringUni(memory, characterCount) ?? string.Empty;
                int terminatorIndex = copiedValue.IndexOf('\0');
                text = terminatorIndex >= 0 ? copiedValue[..terminatorIndex] : copiedValue;
                return ClipboardReadResult.Success;
            }
            finally
            {
                _ = NativeMethods.GlobalUnlock(clipboardData);
            }
        }
        finally
        {
            _ = NativeMethods.CloseClipboard();
        }
    }
}

internal sealed class ClipboardCaptureService : IDisposable
{
    private static readonly int[] RetryDelaysMilliseconds = [20, 50, 100];

    private readonly IntPtr _clipboardOwner;
    private readonly ClipboardQueue _queue;
    private readonly AppLogger _logger;
    private readonly IClipboardTextReader _reader;
    private readonly ClipboardSelfWriteSuppressor _selfWriteSuppressor;
    private readonly ClipboardImageQueue? _imageQueue;
    private readonly IClipboardImageReader? _imageReader;
    private readonly System.Windows.Forms.Timer _retryTimer;
    private readonly Channel<ClipboardRecord> _pendingCaptures;
    private readonly Task _processingTask;

    private bool _captureInProgress;
    private bool _pendingUpdate;
    private bool _stopped;
    private int _retryIndex;
    private int _consecutiveFailedEvents;

    public ClipboardCaptureService(
        IntPtr clipboardOwner,
        ClipboardQueue queue,
        AppLogger logger,
        ClipboardSelfWriteSuppressor selfWriteSuppressor,
        IClipboardTextReader? reader = null,
        ClipboardImageQueue? imageQueue = null,
        IClipboardImageReader? imageReader = null)
    {
        _clipboardOwner = clipboardOwner;
        _queue = queue;
        _logger = logger;
        _selfWriteSuppressor = selfWriteSuppressor;
        _reader = reader ?? new NativeClipboardTextReader();
        _imageQueue = imageQueue;
        _imageReader = imageQueue is null ? null : imageReader ?? new ClipboardImageCapture();
        _retryTimer = new System.Windows.Forms.Timer();
        _retryTimer.Tick += RetryTimerOnTick;
        _pendingCaptures = Channel.CreateBounded<ClipboardRecord>(new BoundedChannelOptions(
            ClipboardQueue.DefaultCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        _processingTask = Task.Run(ProcessPendingCapturesAsync);
    }

    public Task ProcessingCompletion => _processingTask;

    public void NotifyClipboardChanged()
    {
        if (_stopped)
        {
            return;
        }

        if (_captureInProgress)
        {
            _pendingUpdate = true;
            return;
        }

        _captureInProgress = true;
        _retryIndex = 0;
        AttemptCapture();
    }

    public void Stop()
    {
        _stopped = true;
        _pendingUpdate = false;
        _retryTimer.Stop();
        _pendingCaptures.Writer.TryComplete();
    }

    public void Dispose()
    {
        Stop();
        _retryTimer.Tick -= RetryTimerOnTick;
        _retryTimer.Dispose();
    }

    private void AttemptCapture()
    {
        ClipboardReadResult result;
        string? text;
        ClipboardImageReadResult imageResult = ClipboardImageReadResult.NoImage;
        Bitmap? bitmap = null;

        try
        {
            result = _reader.TryReadUnicodeText(_clipboardOwner, out text);
            if (_imageReader is not null)
            {
                imageResult = _imageReader.TryCapture(out bitmap);
            }
        }
        catch
        {
            result = ClipboardReadResult.AccessFailed;
            text = null;
            imageResult = ClipboardImageReadResult.AccessFailed;
            bitmap?.Dispose();
            bitmap = null;
        }

        if (result == ClipboardReadResult.AccessFailed ||
            imageResult == ClipboardImageReadResult.AccessFailed)
        {
            bitmap?.Dispose();
            ScheduleRetryOrSkip();
            return;
        }

        switch (result)
        {
            case ClipboardReadResult.Success:
                _consecutiveFailedEvents = 0;
                if (!string.IsNullOrEmpty(text))
                {
                    _pendingCaptures.Writer.TryWrite(new ClipboardRecord(text, DateTimeOffset.UtcNow));
                }
                break;

            case ClipboardReadResult.NoUnicodeText:
                _consecutiveFailedEvents = 0;
                break;

            default:
                throw new InvalidOperationException("Unknown clipboard read result.");
        }

        if (imageResult == ClipboardImageReadResult.Success && bitmap is not null)
        {
            CapturedClipboardImage captured = new(bitmap, DateTimeOffset.UtcNow);
            bitmap = null;
            if (_imageQueue?.TryEnqueue(captured) != true)
            {
                captured.Dispose();
            }
        }

        bitmap?.Dispose();
        _consecutiveFailedEvents = 0;
        CompleteCapture();
    }

    private void ScheduleRetryOrSkip()
    {
        if (_retryIndex < RetryDelaysMilliseconds.Length)
        {
            _retryTimer.Interval = RetryDelaysMilliseconds[_retryIndex++];
            _retryTimer.Start();
            return;
        }

        _consecutiveFailedEvents++;
        if (_consecutiveFailedEvents == 3 || _consecutiveFailedEvents % 10 == 0)
        {
            _logger.LogConsecutiveClipboardAccessFailures(_consecutiveFailedEvents);
        }

        CompleteCapture();
    }

    private void CompleteCapture()
    {
        _captureInProgress = false;
        _retryIndex = 0;

        if (_pendingUpdate && !_stopped)
        {
            _pendingUpdate = false;
            NotifyClipboardChanged();
        }
    }

    private void RetryTimerOnTick(object? sender, EventArgs eventArgs)
    {
        _retryTimer.Stop();
        if (!_stopped)
        {
            AttemptCapture();
        }
    }

    private async Task ProcessPendingCapturesAsync()
    {
        try
        {
            await foreach (ClipboardRecord record in _pendingCaptures.Reader.ReadAllAsync()
                               .ConfigureAwait(false))
            {
                if (_selfWriteSuppressor.TryConsume(record.Content, DateTimeOffset.UtcNow))
                {
                    continue;
                }

                _queue.TryEnqueue(record);
            }
        }
        catch (Exception exception)
        {
            _logger.LogUnhandledException("clipboard_capture_processor", exception);
        }
    }
}
