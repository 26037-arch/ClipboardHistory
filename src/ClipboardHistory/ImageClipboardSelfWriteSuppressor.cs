namespace ClipboardHistory;

internal sealed class ImageClipboardSelfWriteSuppressor
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(3);

    private readonly object _sync = new();
    private PendingImageWrite? _pending;
    private long _nextToken;

    public long ArmHash(string imageSha256, DateTimeOffset now, TimeSpan? lifetime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageSha256);
        TimeSpan effectiveLifetime = lifetime ?? DefaultLifetime;
        if (effectiveLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        lock (_sync)
        {
            long token = ++_nextToken;
            _pending = new PendingImageWrite(token, imageSha256, now + effectiveLifetime);
            return token;
        }
    }

    public bool TryConsumeHash(string imageSha256, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageSha256);

        lock (_sync)
        {
            if (_pending is null)
            {
                return false;
            }

            PendingImageWrite pending = _pending;
            _pending = null;
            return now < pending.ExpiresAt &&
                   string.Equals(imageSha256, pending.ImageSha256, StringComparison.Ordinal);
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

    private sealed record PendingImageWrite(long Token, string ImageSha256, DateTimeOffset ExpiresAt);
}
