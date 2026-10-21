using System.IO;
using MdViewer.Core.Settings;

namespace MdViewer.Services;

/// <summary>Простой журнал работы приложения (app.log в папке настроек).</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(SettingsStore.DefaultDirectory, "app.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : message + ": " + ex);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                {
                    File.Move(FilePath, FilePath + ".old", true);
                }

                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // журнал не должен ломать приложение
        }
    }
}
