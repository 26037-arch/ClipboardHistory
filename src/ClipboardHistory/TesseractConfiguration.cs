namespace ClipboardHistory;

internal sealed record TesseractConfiguration(
    string ExecutablePath,
    string TessdataDirectory,
    string Languages,
    TimeSpan Timeout)
{
    public const string DefaultExecutablePath = @"C:\Program Files\Tesseract-OCR\tesseract.exe";
    public const string DefaultTessdataDirectory = @"C:\Program Files\Tesseract-OCR\tessdata";
    public const string DefaultLanguages = "kor+eng";

    public static TesseractConfiguration Default { get; } = new(
        DefaultExecutablePath,
        DefaultTessdataDirectory,
        DefaultLanguages,
        TimeSpan.FromSeconds(30));
}

internal sealed record TesseractAvailability(bool IsAvailable, string? ErrorCode)
{
    public static TesseractAvailability Available { get; } = new(true, null);
}

internal sealed record OcrExecutionResult(bool Succeeded, string? Text, string? ErrorCode);

internal interface IOcrEngine
{
    TesseractAvailability Availability { get; }

    Task<OcrExecutionResult> RecognizeAsync(
        string imagePath,
        CancellationToken cancellationToken = default);
}
