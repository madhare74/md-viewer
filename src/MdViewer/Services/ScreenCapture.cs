using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace MdViewer.Services;

/// <summary>Снимок окна в PNG (для проверочных скриншотов в автотестах).</summary>
public static class ScreenCapture
{
    public static void SaveWindow(IntPtr hwnd, string path, bool fromScreen)
    {
        NativeMethods.GetWindowRect(hwnd, out var wr);
        var frame = wr;
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out var fb, Marshal.SizeOf<NativeMethods.RECT>()) == 0)
        {
            frame = fb;
        }

        int width = Math.Max(1, frame.Right - frame.Left);
        int height = Math.Max(1, frame.Bottom - frame.Top);
        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr memDc = NativeMethods.CreateCompatibleDC(screenDc);
        IntPtr bmp;
        if (fromScreen)
        {
            bmp = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
            IntPtr old = NativeMethods.SelectObject(memDc, bmp);
            NativeMethods.BitBlt(memDc, 0, 0, width, height, screenDc, frame.Left, frame.Top, NativeMethods.SRCCOPY);
            NativeMethods.SelectObject(memDc, old);
        }
        else
        {
            int fullW = Math.Max(1, wr.Right - wr.Left);
            int fullH = Math.Max(1, wr.Bottom - wr.Top);
            IntPtr full = NativeMethods.CreateCompatibleBitmap(screenDc, fullW, fullH);
            IntPtr old = NativeMethods.SelectObject(memDc, full);
            NativeMethods.PrintWindow(hwnd, memDc, NativeMethods.PW_RENDERFULLCONTENT);
            NativeMethods.SelectObject(memDc, old);

            // Обрезать невидимые рамки окна
            IntPtr cropDc = NativeMethods.CreateCompatibleDC(screenDc);
            bmp = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
            IntPtr oldCrop = NativeMethods.SelectObject(cropDc, bmp);
            IntPtr oldFull = NativeMethods.SelectObject(memDc, full);
            NativeMethods.BitBlt(cropDc, 0, 0, width, height, memDc, frame.Left - wr.Left, frame.Top - wr.Top, NativeMethods.SRCCOPY);
            NativeMethods.SelectObject(memDc, oldFull);
            NativeMethods.SelectObject(cropDc, oldCrop);
            NativeMethods.DeleteDC(cropDc);
            NativeMethods.DeleteObject(full);
        }

        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var fs = File.Create(path);
            enc.Save(fs);
        }
        finally
        {
            NativeMethods.DeleteObject(bmp);
            NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
