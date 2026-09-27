using System.Drawing;
using System.Runtime.InteropServices;

namespace ClipboardHistory;

internal enum ClipboardImageReadResult
{
    Success,
    NoImage,
    AccessFailed,
    Rejected,
}

internal interface IClipboardImageReader
{
    ClipboardImageReadResult TryCapture(out Bitmap? bitmap);
}

internal sealed class ClipboardImageCapture : IClipboardImageReader
{
    public const int MaximumDimension = 16_384;
    public const long MaximumPixelCount = 40_000_000;
    public const long MaximumEstimatedRawBytes = 160_000_000;

    public ClipboardImageReadResult TryCapture(out Bitmap? bitmap)
    {
        bitmap = null;

        try
        {
            if (!Clipboard.ContainsImage())
            {
                return ClipboardImageReadResult.NoImage;
            }

            using Image? clipboardImage = Clipboard.GetImage();
            if (clipboardImage is null)
            {
                return ClipboardImageReadResult.AccessFailed;
            }

            if (!IsSafeSize(clipboardImage.Width, clipboardImage.Height))
            {
                return ClipboardImageReadResult.Rejected;
            }

            bitmap = new Bitmap(clipboardImage);
            return ClipboardImageReadResult.Success;
        }
        catch (ExternalException)
        {
            return ClipboardImageReadResult.AccessFailed;
        }
        catch (ArgumentException)
        {
            return ClipboardImageReadResult.Rejected;
        }
        catch (OutOfMemoryException)
        {
            return ClipboardImageReadResult.Rejected;
        }
    }

    internal static bool IsSafeSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > MaximumDimension || height > MaximumDimension)
        {
            return false;
        }

        long pixels = (long)width * height;
        return pixels <= MaximumPixelCount && pixels * 4 <= MaximumEstimatedRawBytes;
    }
}
