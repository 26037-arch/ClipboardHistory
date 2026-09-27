using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace ClipboardHistory;

internal sealed class StartupManager
{
    private const string ShortcutFileName = "ClipboardHistory.lnk";

    private readonly string _startupDirectory;
    private readonly string _executablePath;
    private readonly AppLogger _logger;

    public StartupManager(
        AppLogger logger,
        string? startupDirectory = null,
        string? executablePath = null)
    {
        _logger = logger;
        _startupDirectory = startupDirectory ?? AppPaths.StartupDirectory;
        _executablePath = executablePath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("The executable path is unavailable.");
    }

    internal string ShortcutPath => Path.Combine(_startupDirectory, ShortcutFileName);

    public bool Install()
    {
        try
        {
            Directory.CreateDirectory(_startupDirectory);
            CreateShortcut(ShortcutPath, _executablePath);
            _logger.LogStartupResult("install", succeeded: true);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogStartupResult("install", succeeded: false, exception);
            return false;
        }
    }

    public bool Remove()
    {
        try
        {
            if (File.Exists(ShortcutPath))
            {
                File.Delete(ShortcutPath);
            }

            _logger.LogStartupResult("remove", succeeded: true);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogStartupResult("remove", succeeded: false, exception);
            return false;
        }
    }

    private static void CreateShortcut(string shortcutPath, string executablePath)
    {
        object comObject = new ShellLink();

        try
        {
            IShellLinkW shellLink = (IShellLinkW)comObject;
            shellLink.SetPath(executablePath);
            shellLink.SetWorkingDirectory(Path.GetDirectoryName(executablePath) ?? string.Empty);
            shellLink.SetDescription("Clipboard History background application");
            shellLink.SetArguments(string.Empty);
            shellLink.SetShowCmd(7); // SW_SHOWMINNOACTIVE; the application itself has no window.

            IPersistFile persistFile = (IPersistFile)shellLink;
            persistFile.Save(shortcutPath, true);
        }
        finally
        {
            _ = Marshal.FinalReleaseComObject(comObject);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
            int maximumPath,
            IntPtr findData,
            uint flags);

        void GetIDList(out IntPtr itemIdList);

        void SetIDList(IntPtr itemIdList);

        void GetDescription(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name,
            int maximumName);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetWorkingDirectory(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory,
            int maximumPath);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);

        void GetArguments(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments,
            int maximumPath);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);

        void GetHotkey(out short hotkey);

        void SetHotkey(short hotkey);

        void GetShowCmd(out int showCommand);

        void SetShowCmd(int showCommand);

        void GetIconLocation(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath,
            int iconPathLength,
            out int iconIndex);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);

        void Resolve(IntPtr windowHandle, uint flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
