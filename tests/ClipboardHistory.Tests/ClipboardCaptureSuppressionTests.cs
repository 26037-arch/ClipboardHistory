namespace ClipboardHistory.Tests;

public sealed class ClipboardCaptureSuppressionTests
{
    [Fact]
    public async Task ExpectedSelfWrite_IsNotPlacedInDatabaseQueue()
    {
        using TemporaryDirectory temporaryDirectory = new();
        using AppLogger logger = new(Path.Combine(temporaryDirectory.Path, "logs"));
        ClipboardQueue queue = new(capacity: 4);
        ClipboardSelfWriteSuppressor suppressor = new();
        suppressor.Arm("restored value", DateTimeOffset.UtcNow);

        using ClipboardCaptureService service = new(
            IntPtr.Zero,
            queue,
            logger,
            suppressor,
            new FixedClipboardReader("restored value"));

        service.NotifyClipboardChanged();
        service.Stop();
        await service.ProcessingCompletion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(queue.Reader.TryRead(out _));
    }

    [Fact]
    public async Task DifferentExternalWrite_IsPlacedInDatabaseQueue()
    {
        using TemporaryDirectory temporaryDirectory = new();
        using AppLogger logger = new(Path.Combine(temporaryDirectory.Path, "logs"));
        ClipboardQueue queue = new(capacity: 4);
        ClipboardSelfWriteSuppressor suppressor = new();
        suppressor.Arm("restored value", DateTimeOffset.UtcNow);

        using ClipboardCaptureService service = new(
            IntPtr.Zero,
            queue,
            logger,
            suppressor,
            new FixedClipboardReader("external value"));

        service.NotifyClipboardChanged();
        service.Stop();
        await service.ProcessingCompletion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(queue.Reader.TryRead(out ClipboardRecord? record));
        Assert.NotNull(record);
        Assert.Equal("external value", record.Content);
    }

    [Fact]
    public async Task SingleEvent_CapturesTextAndImageIndependently()
    {
        using TemporaryDirectory temporaryDirectory = new();
        using AppLogger logger = new(Path.Combine(temporaryDirectory.Path, "logs"));
        ClipboardQueue textQueue = new(capacity: 4);
        ClipboardImageQueue imageQueue = new(capacity: 2);
        using ClipboardCaptureService service = new(
            IntPtr.Zero,
            textQueue,
            logger,
            new ClipboardSelfWriteSuppressor(),
            new FixedClipboardReader("text and image"),
            imageQueue,
            new FixedImageReader());

        service.NotifyClipboardChanged();
        service.Stop();
        await service.ProcessingCompletion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(textQueue.Reader.TryRead(out ClipboardRecord? text));
        Assert.Equal("text and image", text!.Content);
        Assert.True(imageQueue.Reader.TryRead(out CapturedClipboardImage? image));
        using (image)
        using (System.Drawing.Bitmap bitmap = image!.TakeBitmap())
        {
            Assert.Equal(new System.Drawing.Size(10, 8), bitmap.Size);
        }
    }

    [Fact]
    public void ImageQueue_IsBoundedAndRejectsWithoutWaiting()
    {
        ClipboardImageQueue queue = new(capacity: 1);
        using CapturedClipboardImage first = new(new System.Drawing.Bitmap(1, 1), DateTimeOffset.UtcNow);
        using CapturedClipboardImage second = new(new System.Drawing.Bitmap(1, 1), DateTimeOffset.UtcNow);

        Assert.True(queue.TryEnqueue(first));
        Assert.False(queue.TryEnqueue(second));
        queue.Complete();
    }

    [Theory]
    [InlineData(0, 10, false)]
    [InlineData(10, 0, false)]
    [InlineData(20000, 10, false)]
    [InlineData(8000, 5000, true)]
    [InlineData(8001, 5000, false)]
    public void ImageCapture_EnforcesDimensionAndMemoryLimits(int width, int height, bool expected) =>
        Assert.Equal(expected, ClipboardImageCapture.IsSafeSize(width, height));

    private sealed class FixedClipboardReader : IClipboardTextReader
    {
        private readonly string _value;

        public FixedClipboardReader(string value)
        {
            _value = value;
        }

        public ClipboardReadResult TryReadUnicodeText(IntPtr clipboardOwner, out string? text)
        {
            text = _value;
            return ClipboardReadResult.Success;
        }
    }

    private sealed class FixedImageReader : IClipboardImageReader
    {
        public ClipboardImageReadResult TryCapture(out System.Drawing.Bitmap? bitmap)
        {
            bitmap = new System.Drawing.Bitmap(10, 8);
            return ClipboardImageReadResult.Success;
        }
    }
}
