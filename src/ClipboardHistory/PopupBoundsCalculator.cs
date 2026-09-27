using System.Drawing;

namespace ClipboardHistory;

internal static class PopupBoundsCalculator
{
    public const int DefaultOffset = 12;

    public static Rectangle Calculate(
        Point cursor,
        Rectangle workingArea,
        Size desiredSize,
        int offset = DefaultOffset)
    {
        if (workingArea.Width <= 0 || workingArea.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workingArea));
        }

        if (desiredSize.Width <= 0 || desiredSize.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(desiredSize));
        }

        int width = Math.Min(desiredSize.Width, workingArea.Width);
        int height = Math.Min(desiredSize.Height, workingArea.Height);

        int x = cursor.X + offset;
        if (x + width > workingArea.Right)
        {
            x = cursor.X - width - offset;
        }

        int y = cursor.Y + offset;
        if (y + height > workingArea.Bottom)
        {
            y = cursor.Y - height - offset;
        }

        x = Math.Clamp(x, workingArea.Left, workingArea.Right - width);
        y = Math.Clamp(y, workingArea.Top, workingArea.Bottom - height);

        return new Rectangle(x, y, width, height);
    }
}
