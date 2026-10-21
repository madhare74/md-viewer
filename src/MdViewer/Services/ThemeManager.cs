using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MdViewer.Core.Settings;
using Microsoft.Win32;

namespace MdViewer.Services;

/// <summary>Светлая и тёмная тема окна приложения. «Как в системе» — по настройке Windows.</summary>
public static class ThemeManager
{
    public static event Action? SystemThemeChanged;

    public static bool IsDark { get; private set; }

    public static void Initialize()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color)
            {
                Application.Current?.Dispatcher.InvokeAsync(() => SystemThemeChanged?.Invoke());
            }
        };
    }

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool ResolveDark(ThemeSetting setting) => setting switch
    {
        ThemeSetting.Dark => true,
        ThemeSetting.Light => false,
        _ => SystemPrefersDark(),
    };

    public static void Apply(bool dark)
    {
        IsDark = dark;
        var r = Application.Current.Resources;
        Set(r, "Brush.Window", dark ? "#0d1117" : "#ffffff");
        Set(r, "Brush.Panel", dark ? "#161b22" : "#f6f8fa");
        Set(r, "Brush.Panel2", dark ? "#21262d" : "#eaeef2");
        Set(r, "Brush.Border", dark ? "#30363d" : "#d0d7de");
        Set(r, "Brush.Text", dark ? "#e6edf3" : "#1f2328");
        Set(r, "Brush.Muted", dark ? "#9198a1" : "#59636e");
        Set(r, "Brush.Accent", dark ? "#4493f8" : "#0969da");
        Set(r, "Brush.AccentText", "#ffffff");
        Set(r, "Brush.Hover", dark ? "#262c36" : "#e6ebf1");
        Set(r, "Brush.Selected", dark ? "#0d1117" : "#ffffff");
        Set(r, "Brush.Dirty", dark ? "#d29922" : "#9a6700");
        foreach (Window w in Application.Current.Windows)
        {
            ApplyTitleBar(w);
        }
    }

    public static void ApplyTitleBar(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            int value = IsDark ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch
        {
            // старые сборки Windows 10 — заголовок останется светлым
        }
    }

    private static void Set(ResourceDictionary r, string key, string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        r[key] = brush;
    }
}
