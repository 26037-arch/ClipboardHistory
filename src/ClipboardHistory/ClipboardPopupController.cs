namespace ClipboardHistory;

internal interface IClipboardHistoryPopup : IDisposable
{
    event EventHandler? PopupClosed;

    void ShowPopup();

    void ClosePopup();
}

internal sealed class ClipboardPopupController : IDisposable
{
    private readonly Func<IClipboardHistoryPopup> _popupFactory;
    private IClipboardHistoryPopup? _currentPopup;
    private bool _disposed;

    public ClipboardPopupController(Func<IClipboardHistoryPopup> popupFactory)
    {
        _popupFactory = popupFactory;
    }

    internal bool HasOpenPopup => _currentPopup is not null;

    public void Toggle()
    {
        if (_disposed)
        {
            return;
        }

        if (_currentPopup is not null)
        {
            CloseCurrentPopup();
            return;
        }

        IClipboardHistoryPopup popup = _popupFactory();
        _currentPopup = popup;
        popup.PopupClosed += PopupOnClosed;

        try
        {
            popup.ShowPopup();
        }
        catch
        {
            popup.PopupClosed -= PopupOnClosed;
            _currentPopup = null;
            popup.Dispose();
            throw;
        }
    }

    public void CloseCurrentPopup()
    {
        IClipboardHistoryPopup? popup = _currentPopup;
        if (popup is null)
        {
            return;
        }

        _currentPopup = null;
        popup.PopupClosed -= PopupOnClosed;

        try
        {
            popup.ClosePopup();
        }
        finally
        {
            popup.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseCurrentPopup();
    }

    private void PopupOnClosed(object? sender, EventArgs eventArgs)
    {
        if (sender is not IClipboardHistoryPopup popup || !ReferenceEquals(popup, _currentPopup))
        {
            return;
        }

        _currentPopup = null;
        popup.PopupClosed -= PopupOnClosed;
        popup.Dispose();
    }
}
