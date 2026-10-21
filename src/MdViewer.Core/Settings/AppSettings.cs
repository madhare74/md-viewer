using System.Text.Json;
using System.Text.Json.Serialization;

namespace MdViewer.Core.Settings;

public enum ThemeSetting
{
    System,
    Light,
    Dark,
}

public enum ViewMode
{
    /// <summary>Просмотр (Ctrl+1).</summary>
    View,

    /// <summary>Правка и просмотр (Ctrl+2).</summary>
    Split,

    /// <summary>Правка (Ctrl+3).</summary>
    Edit,
}

public sealed class WindowPlacement
{
    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public bool Maximized { get; set; }
}

/// <summary>Настройки приложения (%AppData%\MD Viewer\settings.json).</summary>
public sealed class AppSettings
{
    public const double MinZoom = 0.5;
    public const double MaxZoom = 3.0;

    public ThemeSetting Theme { get; set; } = ThemeSetting.System;

    public string EditorFontFamily { get; set; } = "Consolas";

    public double EditorFontSize { get; set; } = 14;

    public double PreviewZoom { get; set; } = 1.0;

    public bool FullWidth { get; set; }

    public bool WordWrap { get; set; } = true;

    public ViewMode DefaultMode { get; set; } = ViewMode.View;

    public bool TocVisible { get; set; } = true;

    public double TocWidth { get; set; } = 240;

    public double SplitRatio { get; set; } = 0.5;

    public List<string> RecentFiles { get; set; } = new();

    public List<string> OpenTabs { get; set; } = new();

    public int ActiveTab { get; set; }

    public WindowPlacement? Window { get; set; }

    /// <summary>Приводит значения к допустимым границам после чтения файла.</summary>
    public AppSettings Normalize()
    {
        EditorFontFamily = string.IsNullOrWhiteSpace(EditorFontFamily) ? "Consolas" : EditorFontFamily.Trim();
        EditorFontSize = Clamp(EditorFontSize, 8, 48, 14);
        PreviewZoom = Clamp(PreviewZoom, MinZoom, MaxZoom, 1.0);
        TocWidth = Clamp(TocWidth, 120, 600, 240);
        SplitRatio = Clamp(SplitRatio, 0.15, 0.85, 0.5);
        RecentFiles ??= new();
        OpenTabs ??= new();
        RecentFiles = RecentFiles.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).Take(RecentFilesList.Max).ToList();
        OpenTabs = OpenTabs.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!Enum.IsDefined(Theme))
        {
            Theme = ThemeSetting.System;
        }

        if (!Enum.IsDefined(DefaultMode))
        {
            DefaultMode = ViewMode.View;
        }

        return this;
    }

    private static double Clamp(double v, double min, double max, double fallback) =>
        double.IsNaN(v) || double.IsInfinity(v) ? fallback : Math.Clamp(v, min, max);
}

/// <summary>Шаги масштаба просмотра.</summary>
public static class Zoom
{
    public static double Step(double current, int direction)
    {
        double next = Math.Round(current + (0.1 * Math.Sign(direction)), 2);
        return Math.Clamp(next, AppSettings.MinZoom, AppSettings.MaxZoom);
    }
}

/// <summary>Список недавних файлов (до 10, последний — первым).</summary>
public static class RecentFilesList
{
    public const int Max = 10;

    public static void Add(List<string> list, string path)
    {
        string full = Path.GetFullPath(path);
        list.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, full);
        if (list.Count > Max)
        {
            list.RemoveRange(Max, list.Count - Max);
        }
    }

    public static void Remove(List<string> list, string path)
    {
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Восстановление вкладок при запуске: только сохранённые и существующие файлы.</summary>
public static class SessionTabs
{
    public static List<string> Capture(IEnumerable<string?> tabPaths) =>
        tabPaths.Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static List<string> Restore(IEnumerable<string> saved, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        return saved.Where(p => !string.IsNullOrWhiteSpace(p) && exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>Чтение и запись настроек. Испорченный файл не ломает запуск — берутся настройки по умолчанию.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public SettingsStore(string directory)
    {
        Directory = directory;
        FilePath = Path.Combine(directory, "settings.json");
    }

    public string Directory { get; }

    public string FilePath { get; }

    /// <summary>Папка настроек: переменная MDVIEWER_DATA_DIR (для тестов) или %AppData%\MD Viewer.</summary>
    public static string DefaultDirectory
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("MDVIEWER_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(env))
            {
                return env;
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MD Viewer");
        }
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(FilePath);
            var s = JsonSerializer.Deserialize<AppSettings>(json, Options);
            return (s ?? new AppSettings()).Normalize();
        }
        catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
        {
            TryBackupCorrupt();
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        System.IO.Directory.CreateDirectory(Directory);
        string json = JsonSerializer.Serialize(settings, Options);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, FilePath, overwrite: true);
    }

    private void TryBackupCorrupt()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Copy(FilePath, FilePath + ".bad", overwrite: true);
            }
        }
        catch
        {
            // не важно
        }
    }
}
