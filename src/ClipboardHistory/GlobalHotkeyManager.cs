using System.Runtime.InteropServices;

namespace ClipboardHistory;

internal sealed class GlobalHotkeyManager : IDisposable
{
    internal const int HotkeyId = 0x4348;

    private readonly IntPtr _windowHandle;
    private readonly AppLogger _logger;
    private bool _registered;
    private bool _disposed;

    public GlobalHotkeyManager(IntPtr windowHandle, AppLogger logger)
    {
        if (windowHandle == IntPtr.Zero)
        {
            throw new ArgumentException("A valid window handle is required.", nameof(windowHandle));
        }

        _windowHandle = windowHandle;
        _logger = logger;
    }

    public bool TryRegister()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(GlobalHotkeyManager));
        }

        if (_registered)
        {
            return true;
        }

        uint modifiers = NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModNoRepeat;
        _registered = NativeMethods.RegisterHotKey(
            _windowHandle,
            HotkeyId,
            modifiers,
            NativeMethods.VkH);

        if (!_registered)
        {
            _logger.LogHotkeyRegistrationFailure(Marshal.GetLastWin32Error());
        }

        return _registered;
    }

    public bool Matches(Message message) =>
        message.Msg == NativeMethods.WmHotkey && message.WParam.ToInt32() == HotkeyId;

    public void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        _registered = false;
        if (!NativeMethods.UnregisterHotKey(_windowHandle, HotkeyId))
        {
            _logger.LogHotkeyUnregistrationFailure(Marshal.GetLastWin32Error());
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();
    }
}
