namespace ClipboardHistory;

internal sealed class SingleInstanceGuard : IDisposable
{
    internal const string DefaultMutexName = @"Local\ClipboardHistory.SingleInstance";

    private readonly string _mutexName;
    private Mutex? _mutex;
    private bool _ownsMutex;

    public SingleInstanceGuard(string? mutexName = null)
    {
        _mutexName = string.IsNullOrWhiteSpace(mutexName) ? DefaultMutexName : mutexName;
    }

    public bool TryAcquire()
    {
        if (_mutex is not null)
        {
            return _ownsMutex;
        }

        Mutex mutex = new(initiallyOwned: true, _mutexName, out bool createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return false;
        }

        _mutex = mutex;
        _ownsMutex = true;
        return true;
    }

    public void Dispose()
    {
        if (_mutex is null)
        {
            return;
        }

        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The operating system has already released ownership.
            }
        }

        _mutex.Dispose();
        _mutex = null;
        _ownsMutex = false;
    }
}
