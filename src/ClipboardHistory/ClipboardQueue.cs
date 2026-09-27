using System.Threading.Channels;

namespace ClipboardHistory;

internal sealed class ClipboardQueue
{
    public const int DefaultCapacity = 512;

    private readonly Channel<ClipboardRecord> _channel;

    public ClipboardQueue(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _channel = Channel.CreateBounded<ClipboardRecord>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
    }

    public ChannelReader<ClipboardRecord> Reader => _channel.Reader;

    public bool TryEnqueue(ClipboardRecord record) => _channel.Writer.TryWrite(record);

    public void Complete() => _channel.Writer.TryComplete();
}
