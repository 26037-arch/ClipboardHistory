namespace ClipboardHistory;

internal static class AppPaths
{
    private const string ApplicationFolderName = "ClipboardHistory";

    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ApplicationFolderName);

    public static string DatabasePath => Path.Combine(DataDirectory, "clipboard.db");

    public static string ImagesDirectory => Path.Combine(DataDirectory, "images");

    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public static string StartupDirectory => Environment.GetFolderPath(
        Environment.SpecialFolder.Startup);
}
