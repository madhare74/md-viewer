using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using MdViewer.Core.Export;
using MdViewer.Dialogs;
using MdViewer.Services;
using Microsoft.Web.WebView2.Core;

namespace MdViewer;

public partial class MainWindow
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private bool _exporting;

    private void OnExportPdf(object sender, RoutedEventArgs e) => _ = ExportPdfAsync(null);

    private void OnExportHtml(object sender, RoutedEventArgs e) => _ = ExportHtmlAsync(null);

    private void OnPrint(object sender, RoutedEventArgs e) => _ = PrintAsync();

    /// <summary>Подготовить страницу к экспорту: светлая тема, все схемы и формулы отрисованы.</summary>
    private static async Task<bool> BeginExportAsync(DocumentTab tab)
    {
        tab.ExportReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tab.Post(new { type = "beginExport" });
        var done = await Task.WhenAny(tab.ExportReady.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        return done == tab.ExportReady.Task;
    }

    private static void EndExport(DocumentTab tab)
    {
        tab.Post(new { type = "endExport" });
        tab.ExportReady = null;
    }

    /// <summary>Экспорт в PDF (A4, как в просмотре, светлая тема). path == null — спросить имя файла.</summary>
    internal async Task<bool> ExportPdfAsync(string? path)
    {
        var tab = ActiveTab;
        if (tab?.Core is null || _exporting)
        {
            return false;
        }

        path ??= AskSavePath(tab, ".pdf", "Документ PDF (*.pdf)|*.pdf");
        if (path is null)
        {
            return false;
        }

        if (!CanWrite(path, out string? why))
        {
            ChoiceDialog.Info(this, $"Не удалось экспортировать в «{Path.GetFileName(path)}».", why);
            return false;
        }

        _exporting = true;
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            await BeginExportAsync(tab);
            var env = await GetEnvironmentAsync();
            var settings = env.CreatePrintSettings();
            settings.Orientation = CoreWebView2PrintOrientation.Portrait;
            settings.PageWidth = 8.27;
            settings.PageHeight = 11.69;
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = false;
            settings.ShouldPrintSelectionOnly = false;
            bool ok = await tab.Core.PrintToPdfAsync(path, settings);
            if (!ok)
            {
                ChoiceDialog.Info(this, "Не удалось создать PDF.", "Проверьте, что файл не открыт в другой программе и на диске достаточно места.");
            }
            else
            {
                Log.Info("Экспорт PDF: " + path);
            }

            return ok;
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка экспорта PDF", ex);
            ChoiceDialog.Info(this, "Не удалось создать PDF.", ex.Message);
            return false;
        }
        finally
        {
            EndExport(tab);
            _exporting = false;
            Cursor = null;
        }
    }

    /// <summary>Экспорт в один самодостаточный HTML-файл.</summary>
    internal async Task<bool> ExportHtmlAsync(string? path)
    {
        var tab = ActiveTab;
        if (tab?.Core is null || _exporting)
        {
            return false;
        }

        path ??= AskSavePath(tab, ".html", "Веб-страница (*.html)|*.html");
        if (path is null)
        {
            return false;
        }

        if (!CanWrite(path, out string? why))
        {
            ChoiceDialog.Info(this, $"Не удалось экспортировать в «{Path.GetFileName(path)}».", why);
            return false;
        }

        _exporting = true;
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            await BeginExportAsync(tab);
            string json = await tab.Core.ExecuteScriptAsync("mdv.getExportHtml()");
            string body = JsonSerializer.Deserialize<string>(json) ?? string.Empty;
            EndExport(tab);

            string title = tab.FilePath is null ? tab.Title : Path.GetFileNameWithoutExtension(tab.FilePath);
            string docDir = tab.DocumentDirectory;
            string css = await Task.Run(BuildExportCss);
            string html = await Task.Run(() => HtmlExporter.Build(new HtmlExportOptions
            {
                Title = title,
                BodyHtml = body,
                Css = css,
                DocumentDirectory = docDir,
                DownloadRemote = DownloadImage,
            }));
            await File.WriteAllTextAsync(path, html, new UTF8Encoding(false));
            Log.Info("Экспорт HTML: " + path);
            return true;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            ChoiceDialog.Info(this, "Не удалось сохранить HTML.", DescribeWriteError(ex));
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка экспорта HTML", ex);
            ChoiceDialog.Info(this, "Не удалось экспортировать в HTML.", ex.Message);
            return false;
        }
        finally
        {
            if (tab.ExportReady is not null)
            {
                EndExport(tab);
            }

            _exporting = false;
            Cursor = null;
        }
    }

    private static (byte[] Data, string? Mime)? DownloadImage(string url)
    {
        try
        {
            using var resp = Http.GetAsync(url).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            byte[] data = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            if (data.Length > 20 * 1024 * 1024)
            {
                return null;
            }

            return (data, resp.Content.Headers.ContentType?.MediaType);
        }
        catch
        {
            return null;
        }
    }

    internal static string BuildExportCss()
    {
        string lib = Path.Combine(WwwRoot, "lib");
        var sb = new StringBuilder();
        sb.AppendLine(ReadOrEmpty(Path.Combine(lib, "github-markdown-light.css")));
        sb.AppendLine(ReadOrEmpty(Path.Combine(lib, "hljs-github.css")));
        string katexDir = Path.Combine(lib, "katex");
        sb.AppendLine(HtmlExporter.EmbedFonts(ReadOrEmpty(Path.Combine(katexDir, "katex.min.css")), katexDir));
        return sb.ToString();
    }

    private static string ReadOrEmpty(string path) => File.Exists(path) ? File.ReadAllText(path) : string.Empty;

    private static bool CanWrite(string path, out string? reason)
    {
        reason = null;
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (dir is not null && !Directory.Exists(dir))
            {
                reason = "Папка не найдена.";
                return false;
            }

            using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            reason = DescribeWriteError(ex);
            return false;
        }
    }

    /// <summary>Печать через стандартное окно печати Windows.</summary>
    internal async Task PrintAsync()
    {
        var tab = ActiveTab;
        if (tab?.Core is null || _exporting)
        {
            return;
        }

        _exporting = true;
        try
        {
            await BeginExportAsync(tab);
            void Restore(object? s, EventArgs e)
            {
                Activated -= Restore;
                Dispatcher.InvokeAsync(async () =>
                {
                    await Task.Delay(500);
                    EndExport(tab);
                    _exporting = false;
                });
            }

            Activated += Restore;
            tab.Core.ShowPrintUI(CoreWebView2PrintDialogKind.System);
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка печати", ex);
            EndExport(tab);
            _exporting = false;
            ChoiceDialog.Info(this, "Не удалось открыть окно печати.", ex.Message);
        }
    }
}
