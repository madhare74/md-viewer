using System.IO;
using Microsoft.Win32;

namespace MdViewer.Services;

/// <summary>Связь расширений .md и .markdown с приложением (для текущего пользователя, без прав администратора).</summary>
public static class FileAssociation
{
    public const string ProgId = "MDViewer.Markdown";
    public static readonly string[] Extensions = [".md", ".markdown"];

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "MDViewer.exe");

    public static bool IsRegistered()
    {
        try
        {
            using var cmd = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
            string? value = cmd?.GetValue(null) as string;
            return value != null && value.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void Register()
    {
        string exe = ExePath;
        using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            prog.SetValue(null, "Документ Markdown");
            prog.SetValue("FriendlyTypeName", "Документ Markdown");
            using (var icon = prog.CreateSubKey("DefaultIcon"))
            {
                icon.SetValue(null, $"\"{exe}\",0");
            }

            using var cmd = prog.CreateSubKey(@"shell\open\command");
            cmd.SetValue(null, $"\"{exe}\" \"%1\"");
        }

        using (var app = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\MDViewer.exe"))
        {
            app.SetValue("FriendlyAppName", "MD Viewer");
            using (var cmd = app.CreateSubKey(@"shell\open\command"))
            {
                cmd.SetValue(null, $"\"{exe}\" \"%1\"");
            }

            using var types = app.CreateSubKey("SupportedTypes");
            foreach (string ext in Extensions)
            {
                types.SetValue(ext, string.Empty);
            }
        }

        foreach (string ext in Extensions)
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}");
            key.SetValue(null, ProgId);
            using var owp = key.CreateSubKey("OpenWithProgids");
            owp.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        Notify();
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Applications\MDViewer.exe", false);
        foreach (string ext in Extensions)
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}", true);
            if (key is null)
            {
                continue;
            }

            if (string.Equals(key.GetValue(null) as string, ProgId, StringComparison.OrdinalIgnoreCase))
            {
                key.DeleteValue(string.Empty, false);
            }

            using var owp = key.OpenSubKey("OpenWithProgids", true);
            owp?.DeleteValue(ProgId, false);
        }

        Notify();
    }

    private static void Notify()
    {
        const int SHCNE_ASSOCCHANGED = 0x08000000;
        NativeMethods.SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
    }
}
