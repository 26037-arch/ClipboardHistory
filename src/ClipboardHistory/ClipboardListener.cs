using System.ComponentModel;

namespace ClipboardHistory;

internal sealed class ClipboardListener : NativeWindow, IDisposable
{
    private readonly ClipboardQueue _queue;
    private readonly AppLogger _logger;
    private readonly Action _shutdownRequested;
    private readonly Action _hotkeyPressed;
    private ClipboardCaptureService? _captureService;
    private GlobalHotkeyManager? _hotkeyManager;
    private bool _listening;
    private bool _disposed;

    public ClipboardListener(
        ClipboardQueue queue,
        ClipboardImageQueue imageQueue,
        AppLogger logger,
        ClipboardSelfWriteSuppressor selfWriteSuppressor,
        Action hotkeyPressed,
        Action shutdownRequested)
    {
        _queue = queue;
        _logger = logger;
        _hotkeyPressed = hotkeyPressed;
        _shutdownRequested = shutdownRequested;

        CreateHandle(new CreateParams
        {
            Caption = "ClipboardHistoryMessageWindow",
            X = 0,
            Y = 0,
            Width = 0,
            Height = 0,
            ExStyle = NativeMethods.WsExToolWindow,
        });

        _captureService = new ClipboardCaptureService(
            Handle,
            _queue,
            _logger,
            selfWriteSuppressor,
            imageQueue: imageQueue);

        if (!NativeMethods.AddClipboardFormatListener(Handle))
        {
            int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Dispose();
            throw new Win32Exception(error, "Could not register the clipboard listener.");
        }

        _listening = true;
        _hotkeyManager = new GlobalHotkeyManager(Handle, logger);
        _hotkeyManager.TryRegister();
    }

    public Task CaptureProcessingCompletion =>
        _captureService?.ProcessingCompletion ?? Task.CompletedTask;

    public void StopHotkey() => _hotkeyManager?.Unregister();

    public void StopListening()
    {
        if (!_listening)
        {
            return;
        }

        _listening = false;
        _captureService?.Stop();
        _ = NativeMethods.RemoveClipboardFormatListener(Handle);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hotkeyManager?.Dispose();
        _hotkeyManager = null;
        StopListening();
        _captureService?.Dispose();
        _captureService = null;

        if (Handle != IntPtr.Zero)
        {
            DestroyHandle();
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (_hotkeyManager?.Matches(message) == true)
        {
            _hotkeyPressed();
            return;
        }

        switch (message.Msg)
        {
            case NativeMethods.WmClipboardUpdate:
                _captureService?.NotifyClipboardChanged();
                return;

            case NativeMethods.WmQueryEndSession:
                _shutdownRequested();
                message.Result = new IntPtr(1);
                return;

            case NativeMethods.WmEndSession when message.WParam != IntPtr.Zero:
            case NativeMethods.WmClose:
                _shutdownRequested();
                return;
        }

        base.WndProc(ref message);
    }
}
