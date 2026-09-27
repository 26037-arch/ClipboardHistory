using System.Text;

namespace ClipboardHistory.Tests;

public sealed class ClipboardSearchTextTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("  Hello\t\r\nworld  ", "HELLO WORLD")]
    [InlineData("가나다", "가나다")]
    public void NormalizeForSearch_AppliesSpecifiedNormalization(string? value, string expected) =>
        Assert.Equal(expected, ClipboardSearchText.NormalizeForSearch(value));

    [Fact]
    public void NormalizeForSearch_UsesNfc()
    {
        string decomposed = "가나다".Normalize(NormalizationForm.FormD);

        Assert.Equal("가나다", ClipboardSearchText.NormalizeForSearch(decomposed));
    }

    [Fact]
    public void CreatePreview_CollapsesWhitespaceWithoutChangingOriginal()
    {
        string original = "첫째\r\n둘째\t   셋째";

        string preview = ClipboardSearchText.CreatePreview(original);

        Assert.Equal("첫째 둘째 셋째", preview);
        Assert.Equal("첫째\r\n둘째\t   셋째", original);
    }

    [Fact]
    public void CreatePreview_TruncatesToOneHundredSixtyCharactersWithEllipsis()
    {
        string preview = ClipboardSearchText.CreatePreview(new string('a', 200));

        Assert.Equal(ClipboardSearchText.MaximumPreviewLength, preview.Length);
        Assert.EndsWith("…", preview, StringComparison.Ordinal);
    }
}
