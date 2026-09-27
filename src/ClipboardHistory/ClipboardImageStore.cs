using System.Drawing;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;

namespace ClipboardHistory;

internal sealed record StoredImageArtifact(
    string Sha256,
    string RelativePath,
    string FullPath,
    long ByteLength,
    int Width,
    int Height,
    bool NewlyCreated);

internal sealed partial class ClipboardImageStore
{
    public const long MaximumPngBytes = 100_000_000;

    private readonly string _rootPath;
    private readonly string _rootWithSeparator;

    public ClipboardImageStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _rootWithSeparator = _rootPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    public string RootPath => _rootPath;

    public async Task<StoredImageArtifact> SaveAsync(
        Bitmap bitmap,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (!ClipboardImageCapture.IsSafeSize(bitmap.Width, bitmap.Height))
        {
            throw new InvalidDataException("The image dimensions exceed the configured safety limit.");
        }

        byte[] pngBytes;
        using (MemoryStream stream = new())
        {
            bitmap.Save(stream, ImageFormat.Png);
            if (stream.Length <= 0 || stream.Length > MaximumPngBytes)
            {
                throw new InvalidDataException("The encoded PNG size exceeds the configured safety limit.");
            }

            pngBytes = stream.ToArray();
        }

        string sha256 = DatabaseWriter.ComputeHash(pngBytes);
        string relativePath = $"{sha256}.png";
        string fullPath = ResolveManagedPath(relativePath);
        Directory.CreateDirectory(_rootPath);

        if (File.Exists(fullPath))
        {
            return new StoredImageArtifact(
                sha256,
                relativePath,
                fullPath,
                pngBytes.LongLength,
                bitmap.Width,
                bitmap.Height,
                NewlyCreated: false);
        }

        string temporaryPath = Path.Combine(_rootPath, $".{sha256}.{Guid.NewGuid():N}.tmp");
        bool moved = false;
        try
        {
            await using (FileStream file = new(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(pngBytes, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporaryPath, fullPath, overwrite: false);
                moved = true;
            }
            catch (IOException) when (File.Exists(fullPath))
            {
                File.Delete(temporaryPath);
            }

            return new StoredImageArtifact(
                sha256,
                relativePath,
                fullPath,
                pngBytes.LongLength,
                bitmap.Width,
                bitmap.Height,
                NewlyCreated: moved);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // Reconciliation removes stale temporary files on the next start.
                }
            }
        }
    }

    public string ResolveManagedPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            !ManagedFileNameRegex().IsMatch(relativePath) ||
            !string.Equals(Path.GetFileName(relativePath), relativePath, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The image path is not a managed ClipboardHistory image path.");
        }

        string fullPath = Path.GetFullPath(Path.Combine(_rootPath, relativePath));
        if (!fullPath.StartsWith(_rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The image path escapes the managed image directory.");
        }

        return fullPath;
    }

    public async Task<Bitmap> LoadBitmapAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        string fullPath = ResolveManagedPath(relativePath);
        byte[] bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (bytes.LongLength <= 0 || bytes.LongLength > MaximumPngBytes)
        {
            throw new InvalidDataException("The stored image has an invalid size.");
        }

        using MemoryStream stream = new(bytes, writable: false);
        using Image decoded = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
        if (!ClipboardImageCapture.IsSafeSize(decoded.Width, decoded.Height))
        {
            throw new InvalidDataException("The stored image dimensions exceed the safety limit.");
        }

        return new Bitmap(decoded);
    }

    public bool TryDelete(string relativePath)
    {
        string fullPath = ResolveManagedPath(relativePath);
        if (!File.Exists(fullPath))
        {
            return true;
        }

        try
        {
            File.Delete(fullPath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void DeleteNewArtifactAfterDatabaseFailure(StoredImageArtifact artifact)
    {
        if (!artifact.NewlyCreated)
        {
            return;
        }

        try
        {
            string validatedPath = ResolveManagedPath(artifact.RelativePath);
            if (File.Exists(validatedPath))
            {
                File.Delete(validatedPath);
            }
        }
        catch
        {
            // Startup reconciliation handles a newly-created unreferenced file.
        }
    }

    [GeneratedRegex("^[0-9a-f]{64}\\.png$", RegexOptions.CultureInvariant)]
    private static partial Regex ManagedFileNameRegex();
}
