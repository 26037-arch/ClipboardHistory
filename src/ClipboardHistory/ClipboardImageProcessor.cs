using System.Drawing;

namespace ClipboardHistory;

internal sealed class ClipboardImageProcessor
{
    private readonly ClipboardImageQueue _queue;
    private readonly ClipboardImageStore _store;
    private readonly DatabaseWriter _writer;
    private readonly ClipboardHistoryRepository _repository;
    private readonly OcrWorker _ocrWorker;
    private readonly IOcrEngine _ocrEngine;
    private readonly ImageClipboardSelfWriteSuppressor _selfWriteSuppressor;
    private readonly StorageUsageService _storageUsage;
    private readonly AppLogger _logger;

    public ClipboardImageProcessor(
        ClipboardImageQueue queue,
        ClipboardImageStore store,
        DatabaseWriter writer,
        ClipboardHistoryRepository repository,
        OcrWorker ocrWorker,
        IOcrEngine ocrEngine,
        ImageClipboardSelfWriteSuppressor selfWriteSuppressor,
        StorageUsageService storageUsage,
        AppLogger logger)
    {
        _queue = queue;
        _store = store;
        _writer = writer;
        _repository = repository;
        _ocrWorker = ocrWorker;
        _ocrEngine = ocrEngine;
        _selfWriteSuppressor = selfWriteSuppressor;
        _storageUsage = storageUsage;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (CapturedClipboardImage capture in _queue.Reader
                               .ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                using (capture)
                using (Bitmap bitmap = capture.TakeBitmap())
                {
                    await ProcessOneAsync(bitmap, capture.CopiedAt, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DrainAndDispose();
        }
        catch (Exception exception)
        {
            _logger.LogUnhandledException("image_processor", exception);
            DrainAndDispose();
        }
    }

    private async Task ProcessOneAsync(
        Bitmap bitmap,
        DateTimeOffset copiedAt,
        CancellationToken cancellationToken)
    {
        StoredImageArtifact? artifact = null;
        try
        {
            artifact = await _store.SaveAsync(bitmap, cancellationToken).ConfigureAwait(false);
            if (_selfWriteSuppressor.TryConsumeHash(artifact.Sha256, DateTimeOffset.UtcNow))
            {
                return;
            }

            string ocrStatus = _ocrEngine.Availability.IsAvailable
                ? OcrStatus.Pending
                : OcrStatus.Unavailable;
            long recordId = await _writer.InsertImageAsync(new StoredImageRecord(
                    artifact.Sha256,
                    artifact.RelativePath,
                    artifact.ByteLength,
                    artifact.Width,
                    artifact.Height,
                    copiedAt,
                    ocrStatus), cancellationToken)
                .ConfigureAwait(false);

            if (_ocrEngine.Availability.IsAvailable)
            {
                _ocrWorker.TryEnqueue(new PendingOcrRecord(recordId, artifact.RelativePath));
            }

            _storageUsage.Invalidate();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (artifact is not null && artifact.NewlyCreated &&
                !await IsReferencedWithoutThrowingAsync(artifact.RelativePath).ConfigureAwait(false))
            {
                _store.DeleteNewArtifactAfterDatabaseFailure(artifact);
            }

            throw;
        }
        catch (Exception exception)
        {
            if (artifact is not null && artifact.NewlyCreated &&
                !await IsReferencedWithoutThrowingAsync(artifact.RelativePath).ConfigureAwait(false))
            {
                _store.DeleteNewArtifactAfterDatabaseFailure(artifact);
            }

            _logger.LogUnhandledException("image_record", exception);
        }
    }

    private async Task<bool> IsReferencedWithoutThrowingAsync(string relativePath)
    {
        try
        {
            return await _repository.HasImageReferenceAsync(relativePath).ConfigureAwait(false);
        }
        catch
        {
            return true;
        }
    }

    private void DrainAndDispose()
    {
        while (_queue.Reader.TryRead(out CapturedClipboardImage? capture))
        {
            capture.Dispose();
        }
    }
}
