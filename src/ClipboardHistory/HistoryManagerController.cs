namespace ClipboardHistory;

internal sealed class HistoryManagerController : IDisposable
{
    private readonly Func<HistoryManagerForm> _factory;
    private HistoryManagerForm? _form;
    private bool _disposed;

    public HistoryManagerController(Func<HistoryManagerForm> factory)
    {
        _factory = factory;
    }

    public void ShowOrActivate()
    {
        if (_disposed)
        {
            return;
        }

        if (_form is { IsDisposed: false })
        {
            if (_form.WindowState == FormWindowState.Minimized)
            {
                _form.WindowState = FormWindowState.Normal;
            }

            _form.Show();
            _form.Activate();
            return;
        }

        _form = _factory();
        _form.FormClosed += FormOnClosed;
        _form.Show();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_form is not null)
        {
            _form.FormClosed -= FormOnClosed;
            _form.Close();
            _form.Dispose();
            _form = null;
        }
    }

    private void FormOnClosed(object? sender, FormClosedEventArgs eventArgs)
    {
        if (sender is HistoryManagerForm form && ReferenceEquals(form, _form))
        {
            form.FormClosed -= FormOnClosed;
            _form = null;
            form.Dispose();
        }
    }
}
