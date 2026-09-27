using System.Threading.Channels;

namespace ClipboardHistory;

internal sealed class OcrWorker
{
    public const int QueueCapacity = 128;

    private readonly Channel<PendingOcrRecord> _queue;
    private readonly ClipboardHistoryRepository _repository;
    private readonly DatabaseWriter _writer;
    private readonly ClipboardImageStore _imageStore;
    private readonly IOcrEngine _engine;
    private readonly AppLogger _logger;
    private int _runningRecognitions;
    private int _maximumConcurrentRecognitions;

    public OcrWorker(
        ClipboardHistoryRepository repository,
        DatabaseWriter writer,
        ClipboardImageStore imageStore,
        IOcrEngine engine,
        AppLogger logger)
    {
        _repository = repository;
        _writer = writer;
        _imageStore = imageStore;
        _engine = engine;
        _logger = logger;
        _queue = Channel.CreateBounded<PendingOcrRecord>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
    }

    internal int MaximumConcurrentRecognitions => Volatile.Read(ref _maximumConcurrentRecognitions);

    public bool TryEnqueue(PendingOcrRecord record) => _queue.Writer.TryWrite(record);

    public void Complete() => _queue.Writer.TryComplete();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _writer.ResetInterruptedOcrAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<PendingOcrRecord> pending = await _repository
                .GetPendingOcrAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!_engine.Availability.IsAvailable)
            {
                foreach (PendingOcrRecord record in pending)
                {
                    await _writer.UpdateOcrAsync(
                        record.Id,
                        OcrStatus.Unavailable,
                        null,
                        _engine.Availability.ErrorCode ?? "unavailable",
                        cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                foreach (PendingOcrRecord record in pending)
                {
                    await ProcessOneAsync(record, cancellationToken).ConfigureAwait(false);
                }
            }

            await foreach (PendingOcrRecord record in _queue.Reader
                               .ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                await ProcessOneAsync(record, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown intentionally stops new OCR and kills the active process tree.
        }
        catch (Exception exception)
        {
            _logger.LogUnhandledException("ocr_worker", exception);
        }
    }

    private async Task ProcessOneAsync(PendingOcrRecord record, CancellationToken cancellationToken)
    {
        await _writer.UpdateOcrAsync(
            record.Id,
            OcrStatus.Processing,
            null,
            null,
            cancellationToken).ConfigureAwait(false);

        int running = Interlocked.Increment(ref _runningRecognitions);
        UpdateMaximum(running);
        try
        {
            string fullPath;
            try
            {
                fullPath = _imageStore.ResolveManagedPath(record.RelativePath);
            }
            catch (InvalidDataException)
            {
                await _writer.UpdateOcrAsync(
                    record.Id,
                    OcrStatus.Failed,
                    null,
                    "invalid_path",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!File.Exists(fullPath))
            {
                await _writer.UpdateOcrAsync(
                    record.Id,
                    OcrStatus.Failed,
                    null,
                    "file_missing",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            OcrExecutionResult result = await _engine
                .RecognizeAsync(fullPath, cancellationToken)
                .ConfigureAwait(false);
            await _writer.UpdateOcrAsync(
                record.Id,
                result.Succeeded ? OcrStatus.Completed : OcrStatus.Failed,
                result.Succeeded ? result.Text : null,
                result.ErrorCode,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _writer.UpdateOcrAsync(
                    record.Id,
                    OcrStatus.Pending,
                    null,
                    null,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // A processing row is reset to pending by the next startup migration path.
            }

            throw;
        }
        catch (Exception exception)
        {
            _logger.LogUnhandledException("ocr_record", exception);
            try
            {
                await _writer.UpdateOcrAsync(
                    record.Id,
                    OcrStatus.Failed,
                    null,
                    "processing_error",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _runningRecognitions);
        }
    }

    private void UpdateMaximum(int value)
    {
        int snapshot;
        while (value > (snapshot = Volatile.Read(ref _maximumConcurrentRecognitions)) &&
               Interlocked.CompareExchange(ref _maximumConcurrentRecognitions, value, snapshot) != snapshot)
        {
        }
    }
}
