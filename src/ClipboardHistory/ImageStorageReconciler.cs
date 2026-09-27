namespace ClipboardHistory;

internal sealed class ImageStorageReconciler
{
    private readonly ClipboardImageStore _store;
    private readonly ClipboardHistoryRepository _repository;
    private readonly DatabaseWriter _writer;

    public ImageStorageReconciler(
        ClipboardImageStore store,
        ClipboardHistoryRepository repository,
        DatabaseWriter writer)
    {
        _store = store;
        _repository = repository;
        _writer = writer;
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_store.RootPath);
        foreach (string temporaryFile in Directory.EnumerateFiles(
                     _store.RootPath,
                     ".*.tmp",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryDeleteFullPath(temporaryFile);
        }

        HashSet<string> referenced = await _repository
            .GetReferencedImagePathsAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (string file in Directory.EnumerateFiles(
                     _store.RootPath,
                     "*.png",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = Path.GetFileName(file);
            try
            {
                _ = _store.ResolveManagedPath(relativePath);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            if (referenced.Contains(relativePath))
            {
                await _writer.ClearOrphanAsync(relativePath, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (_store.TryDelete(relativePath))
            {
                await _writer.ClearOrphanAsync(relativePath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _writer.MarkOrphanAsync(relativePath, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void TryDeleteFullPath(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Stale temporary files are retried on a later maintenance pass.
        }
    }
}
