using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using MdViewer.Core.Documents;
using MdViewer.Dialogs;
using MdViewer.Services;
using Microsoft.Web.WebView2.Core;

namespace MdViewer;

public partial class App : Application
{
    private SingleInstance? _instance;

    public const string WebView2DownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Необработанная ошибка", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error("Необработанная ошибка задачи", ex.Exception);
            ex.SetObserved();
        };

        var files = CommandLine.ParseFiles(e.Args, Environment.CurrentDirectory);
        bool automation = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MDVIEWER_AUTOMATION_PIPE"));

        _instance = SingleInstance.TryAcquire();
        if (_instance is null)
        {
            // Приложение уже запущено — передать файлы и выйти.
            if (SingleInstance.SendToPrimary(files))
            {
                Shutdown(0);
                return;
            }

            Log.Info("Запущенный экземпляр не ответил — открываю новое окно");
        }

        ThemeManager.Initialize();

        if (!IsWebView2Available(out string? error))
        {
            Log.Error("WebView2 не найден: " + error);
            int r = ChoiceDialog.Show(null,
                "Для работы MD Viewer нужен компонент Microsoft Edge WebView2 Runtime. Он входит в Windows 10 и 11, но на этом компьютере не найден.",
                [new DialogButton("Открыть страницу загрузки", "DownloadButton", IsDefault: true), new DialogButton("Закрыть", "CloseButton", IsCancel: true)],
                "Установите «Evergreen Standalone Installer» с сайта Microsoft и запустите MD Viewer снова.");
            if (r == 0)
            {
                OpenUrl(WebView2DownloadUrl);
            }

            Shutdown(1);
            return;
        }

        var window = new MainWindow(files, automation);
        MainWindow = window;
        window.Show();
        _instance?.StartListening(paths => Dispatcher.InvokeAsync(() => window.OpenFromOtherInstance(paths)));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }

    public static bool IsWebView2Available(out string? error)
    {
        error = null;
        try
        {
            string? v = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (string.IsNullOrEmpty(v))
            {
                error = "версия не определена";
                return false;
            }

            return true;
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось открыть адрес " + url, ex);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Ошибка в интерфейсе", e.Exception);
        e.Handled = true;
        try
        {
            ChoiceDialog.Info(MainWindow, "Произошла непредвиденная ошибка. Приложение продолжит работу.", e.Exception.Message + Environment.NewLine + "Подробности: " + Log.FilePath);
        }
        catch
        {
            // ignore
        }
    }
}
