using System.Drawing;
using System.Runtime.InteropServices;

namespace ClipboardHistory;

internal interface IClipboardWriter
{
    void SetText(string text);

    void SetImage(Image image);
}

internal sealed class SystemClipboardWriter : IClipboardWriter
{
    public void SetText(string text) => Clipboard.SetText(text, TextDataFormat.UnicodeText);

    public void SetImage(Image image) => Clipboard.SetImage(image);
}

internal sealed class ClipboardRestoreService
{
    private static readonly int[] RetryDelaysMilliseconds = [20, 50, 100];

    private readonly ClipboardImageStore _imageStore;
    private readonly ClipboardSelfWriteSuppressor _textSuppressor;
    private readonly ImageClipboardSelfWriteSuppressor _imageSuppressor;
    private readonly IClipboardWriter _clipboardWriter;

    public ClipboardRestoreService(
        ClipboardImageStore imageStore,
        ClipboardSelfWriteSuppressor textSuppressor,
        ImageClipboardSelfWriteSuppressor imageSuppressor,
        IClipboardWriter? clipboardWriter = null)
    {
        _imageStore = imageStore;
        _textSuppressor = textSuppressor;
        _imageSuppressor = imageSuppressor;
        _clipboardWriter = clipboardWriter ?? new SystemClipboardWriter();
    }

    public async Task RestoreAsync(
        ClipboardHistoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        if (entry.RecordType == ClipboardRecordType.Text)
        {
            string hash = await Task.Run(
                () => DatabaseWriter.ComputeContentHash(entry.Content),
                cancellationToken);
            await SetClipboardWithRetryAsync(
                () => _textSuppressor.ArmHash(hash, DateTimeOffset.UtcNow),
                token => _textSuppressor.Cancel(token),
                () => _clipboardWriter.SetText(entry.Content),
                cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(entry.ImageRelativePath) ||
            string.IsNullOrWhiteSpace(entry.ImageSha256))
        {
            throw new FileNotFoundException("저장된 이미지 정보가 없습니다.");
        }

        using Bitmap bitmap = await Task.Run(
            () => _imageStore.LoadBitmapAsync(entry.ImageRelativePath, cancellationToken),
            cancellationToken);
        await SetClipboardWithRetryAsync(
            () => _imageSuppressor.ArmHash(entry.ImageSha256, DateTimeOffset.UtcNow),
            token => _imageSuppressor.Cancel(token),
            () => _clipboardWriter.SetImage(bitmap),
            cancellationToken);
    }

    private static async Task SetClipboardWithRetryAsync(
        Func<long> armSuppression,
        Action<long> cancelSuppression,
        Action setClipboard,
        CancellationToken cancellationToken)
    {
        Exception? finalException = null;
        for (int attempt = 0; attempt <= RetryDelaysMilliseconds.Length; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long token = armSuppression();
            try
            {
                setClipboard();
                return;
            }
            catch (ExternalException exception)
            {
                cancelSuppression(token);
                finalException = exception;
            }
            catch
            {
                cancelSuppression(token);
                throw;
            }

            if (attempt < RetryDelaysMilliseconds.Length)
            {
                await Task.Delay(RetryDelaysMilliseconds[attempt], cancellationToken);
            }
        }

        throw finalException ?? new ExternalException("Could not write to the clipboard.");
    }
}
