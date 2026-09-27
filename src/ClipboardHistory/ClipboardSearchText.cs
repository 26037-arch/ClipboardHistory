using System.Text;
using System.Text.RegularExpressions;

namespace ClipboardHistory;

internal static partial class ClipboardSearchText
{
    public const int MaximumPreviewLength = 160;

    public static string NormalizeForSearch(string? value)
    {
        string normalized = (value ?? string.Empty)
            .Normalize(NormalizationForm.FormC)
            .ToUpperInvariant();

        return WhitespaceRegex().Replace(normalized, " ").Trim();
    }

    public static string CreatePreview(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        string preview = WhitespaceRegex().Replace(content, " ").Trim();
        if (preview.Length <= MaximumPreviewLength)
        {
            return preview;
        }

        return string.Concat(preview.AsSpan(0, MaximumPreviewLength - 1), "…");
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
