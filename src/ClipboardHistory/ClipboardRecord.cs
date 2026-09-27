namespace ClipboardHistory;

internal sealed record ClipboardRecord(string Content, DateTimeOffset CopiedAt);

internal enum ClipboardRecordType
{
    Text,
    Image,
}

internal static class OcrStatus
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Unavailable = "unavailable";
}

internal sealed record StoredImageRecord(
    string Sha256,
    string RelativePath,
    long ByteLength,
    int Width,
    int Height,
    DateTimeOffset CopiedAt,
    string InitialOcrStatus);

internal sealed record PendingOcrRecord(long Id, string RelativePath);

internal sealed record ImageFileReference(string Sha256, string RelativePath);
