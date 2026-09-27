using System.Drawing;
using System.Threading.Channels;

namespace ClipboardHistory;

internal sealed class CapturedClipboardImage : IDisposable
{
    private Bitmap? _bitmap;

    public CapturedClipboardImage(Bitmap bitmap, DateTimeOffset copiedAt)
    {
        _bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
        CopiedAt = copiedAt;
    }

    public DateTimeOffset CopiedAt { get; }

    public Bitmap TakeBitmap() => Interlocked.Exchange(ref _bitmap, null)
        ?? throw new ObjectDisposedException(nameof(CapturedClipboardImage));

    public void Dispose() => Interlocked.Exchange(ref _bitmap, null)?.Dispose();
}

internal sealed class ClipboardImageQueue
{
    public const int DefaultCapacity = 8;

    private readonly Channel<CapturedClipboardImage> _channel;

    public ClipboardImageQueue(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _channel = Channel.CreateBounded<CapturedClipboardImage>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
    }

    public ChannelReader<CapturedClipboardImage> Reader => _channel.Reader;

    public bool TryEnqueue(CapturedClipboardImage image) => _channel.Writer.TryWrite(image);

    public void Complete() => _channel.Writer.TryComplete();
}
