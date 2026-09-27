namespace ClipboardHistory;

internal sealed class ClipboardSelfWriteSuppressor
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(2);

    private readonly object _sync = new();
    private PendingWrite? _pending;
    private long _nextToken;

    public long Arm(string content, DateTimeOffset now, TimeSpan? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        return ArmHash(DatabaseWriter.ComputeContentHash(content), now, lifetime);
    }

    public long ArmHash(string contentHash, DateTimeOffset now, TimeSpan? lifetime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        TimeSpan effectiveLifetime = lifetime ?? DefaultLifetime;
        if (effectiveLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        lock (_sync)
        {
            long token = ++_nextToken;
            _pending = new PendingWrite(token, contentHash, now + effectiveLifetime);
            return token;
        }
    }

    public bool TryConsume(string content, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(content);

        PendingWrite? snapshot;
        lock (_sync)
        {
            snapshot = _pending;
            if (snapshot is null)
            {
                return false;
            }

            if (now >= snapshot.ExpiresAt)
            {
                _pending = null;
                return false;
            }
        }

        string actualHash = DatabaseWriter.ComputeContentHash(content);

        lock (_sync)
        {
            if (_pending is null || _pending.Token != snapshot.Token)
            {
                return false;
            }

            _pending = null;
            return string.Equals(actualHash, snapshot.ContentHash, StringComparison.Ordinal);
        }
    }

    public void Cancel(long token)
    {
        lock (_sync)
        {
            if (_pending?.Token == token)
            {
                _pending = null;
            }
        }
    }

    private sealed record PendingWrite(long Token, string ContentHash, DateTimeOffset ExpiresAt);
}
