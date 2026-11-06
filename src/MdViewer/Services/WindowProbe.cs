using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MdViewer.Services;

/// <summary>
/// Диагностика: какое окно на самом деле находится под кнопками заголовка и под меню
/// (своё, WebView2 или чужая программа), и какие окна есть у процесса. Пишется в журнал.
/// </summary>
public static class WindowProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT p);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    public static string Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return "(нет окна)";
        }

        var cls = new StringBuilder(256);
        GetClassName(hwnd, cls, cls.Capacity);
        var text = new StringBuilder(256);
        GetWindowText(hwnd, text, text.Capacity);
        GetWindowThreadProcessId(hwnd, out uint pid);
        string proc;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            proc = p.ProcessName;
        }
        catch
        {
            proc = "?";
        }

        NativeMethods.GetWindowRect(hwnd, out var r);
        int ex = GetWindowLong(hwnd, -20);
        int style = GetWindowLong(hwnd, -16);
        return $"0x{hwnd.ToInt64():X} класс «{cls}» текст «{text}» процесс {proc} ({pid}) [{r.Left},{r.Top} — {r.Right},{r.Bottom}] стиль 0x{style:X8} доп.0x{ex:X8} видимо {IsWindowVisible(hwnd)}";
    }

    /// <summary>Записывает в журнал, что находится под точками кнопок заголовка и меню.</summary>
    public static void LogPoints(IntPtr main, string when)
    {
        try
        {
            NativeMethods.GetWindowRect(main, out var r);
            int top = Math.Max(r.Top, 0) + 8;
            var sb = new StringBuilder();
            sb.AppendLine($"Проверка окон ({when}). Главное окно: {Describe(main)}");
            void Probe(string name, int x, int y)
            {
                var h = WindowFromPoint(new POINT { X = x, Y = y });
                var root = GetAncestor(h, 2);
                // WM_NCHITTEST: что окно сообщает о точке (20 = кнопка закрытия, 8 = свернуть, 2 = заголовок)
                int hit = root == main ? (int)SendMessage(main, 0x0084, IntPtr.Zero, (IntPtr)((y << 16) | (x & 0xFFFF))) : -1;
                sb.AppendLine($"  {name} ({x},{y}): {Describe(h)}; верхнее окно {(root == main ? "наше главное" : Describe(root))}; NCHITTEST {hit}");
            }

            int right = r.Right - 8;
            Probe("кнопка «Закрыть»", right - 23, top + 7);
            Probe("кнопка «Свернуть»", right - 115, top + 7);
            Probe("заголовок", (r.Left + r.Right) / 2, top + 7);
            Probe("меню «Файл»", Math.Max(r.Left, 0) + 30, top + 35);
            Probe("панель инструментов", Math.Max(r.Left, 0) + 300, top + 65);

            uint myPid = (uint)Environment.ProcessId;
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pid == myPid && IsWindowVisible(h))
                {
                    sb.AppendLine("  окно процесса: " + Describe(h));
                }

                return true;
            }, IntPtr.Zero);
            EnumChildWindows(main, (h, _) =>
            {
                sb.AppendLine("  дочернее: " + Describe(h));
                return true;
            }, IntPtr.Zero);
            Log.Info(sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            Log.Error("Проверка окон", ex);
        }
    }
}
