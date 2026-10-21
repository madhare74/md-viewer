using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using MdViewer.Core.Settings;
using MdViewer.Services;

namespace MdViewer;

/// <summary>Команды для UI-тестов (канал включается только переменной окружения).</summary>
public partial class MainWindow
{
    private static string Ok(object? data = null) => JsonSerializer.Serialize(new { ok = true, data }, DocumentTab.JsonOptions);

    private static string Fail(string error) => JsonSerializer.Serialize(new { ok = false, error }, DocumentTab.JsonOptions);

    internal async Task<string> HandleAutomationAsync(JsonElement req)
    {
        string cmd = req.GetProperty("cmd").GetString() ?? string.Empty;
        string? Str(string name) => req.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double Num(string name, double def) => req.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : def;
        List<string> Paths() => req.TryGetProperty("paths", out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList() : new List<string>();

        switch (cmd)
        {
            case "ping":
                return Ok("pong");
            case "state":
                return Ok(State());
            case "open":
                await OpenFilesAsync(Paths(), activate: true, quiet: false);
                return Ok(State());
            case "drop":
                OnFilesDropped(Paths());
                await WaitTabsReadyAsync();
                return Ok(State());
            case "new":
                await NewDocumentAsync();
                return Ok(State());
            case "select":
                TabStrip.SelectedIndex = (int)Num("index", 0);
                return Ok(State());
            case "mode":
                SetMode(Str("mode") switch { "split" => ViewMode.Split, "edit" => ViewMode.Edit, _ => ViewMode.View });
                await Task.Delay(150);
                return Ok(State());
            case "shortcut":
                {
                    string key = Str("key") ?? string.Empty;
                    _ = Dispatcher.InvokeAsync(() => HandleShortcut(key, fromPage: true));
                    return Ok();
                }

            case "theme":
                SetTheme(Enum.TryParse<ThemeSetting>(Str("theme"), true, out var th) ? th : ThemeSetting.System);
                await Task.Delay(300);
                return Ok(State());
            case "uiScale":
                SetUiScale(Num("scale", 1.0));
                await Task.Delay(300);
                return Ok();
            case "resize":
                WindowState = WindowState.Normal;
                Left = Num("left", 0);
                Top = Num("top", 0);
                Width = Num("width", 1280);
                Height = Num("height", 800);
                await Task.Delay(300);
                return Ok();
            case "eval":
                {
                    var tab = ActiveTab;
                    if (tab?.Core is null)
                    {
                        return Fail("нет вкладки");
                    }

                    string raw = await tab.Core.ExecuteScriptAsync(Str("js") ?? "null");
                    return "{\"ok\":true,\"data\":" + raw + "}";
                }

            case "waitRendered":
                return await WaitRenderedAsync(Num("timeout", 15000)) ? Ok(State()) : Fail("не дождались отрисовки");
            case "setText":
                {
                    var tab = ActiveTab;
                    if (tab?.Core is null)
                    {
                        return Fail("нет вкладки");
                    }

                    await tab.Core.ExecuteScriptAsync("mdv.setTextAsUser(" + JsonSerializer.Serialize(Str("text") ?? string.Empty) + "); mdv.flush();");
                    await Task.Delay(200);
                    return await WaitRenderedAsync(10000) ? Ok(State()) : Fail("не дождались отрисовки");
                }

            case "save":
                {
                    var tab = ActiveTab;
                    if (tab is null)
                    {
                        return Fail("нет вкладки");
                    }

                    bool ok = await SaveTabAsync(tab, false, Str("path") ?? tab.FilePath);
                    return ok ? Ok(State()) : Fail("не сохранено");
                }

            case "export":
                {
                    string kind = Str("kind") ?? "pdf";
                    string path = Str("path") ?? throw new ArgumentException("path");
                    bool ok = kind == "html" ? await ExportHtmlAsync(path) : await ExportPdfAsync(path);
                    return ok ? Ok(new { path, size = new FileInfo(path).Length }) : Fail("экспорт не удался");
                }

            case "screenshot":
                {
                    string path = Str("path") ?? throw new ArgumentException("path");
                    await Task.Delay((int)Num("delay", 400));
                    var hwnd = new WindowInteropHelper(this).Handle;
                    ScreenCapture.SaveWindow(hwnd, path, Str("method") == "screen");
                    return Ok(new { path });
                }

            case "screenshotWindow":
                {
                    // Снимок другого окна приложения (диалог, настройки) по заголовку.
                    string path = Str("path") ?? throw new ArgumentException("path");
                    string title = Str("title") ?? string.Empty;
                    await Task.Delay((int)Num("delay", 400));
                    var w = Application.Current.Windows.OfType<Window>().FirstOrDefault(x => x != this && x.IsVisible && (title.Length == 0 || x.Title == title));
                    if (w is null)
                    {
                        return Fail("окно не найдено");
                    }

                    ScreenCapture.SaveWindow(new WindowInteropHelper(w).Handle, path, Str("method") == "screen");
                    return Ok(new { path });
                }

            case "showSettings":
                _ = Dispatcher.InvokeAsync(ShowSettings);
                return Ok();
            case "closeTab":
                {
                    var tab = ActiveTab;
                    if (tab is not null)
                    {
                        _ = Dispatcher.InvokeAsync(() => CloseTabAsync(tab));
                    }

                    return Ok();
                }

            case "closeWindow":
                _ = Dispatcher.InvokeAsync(Close);
                return Ok();
            case "moveTab":
                MoveTab((int)Num("from", 0), (int)Num("to", 0));
                return Ok(State());
            case "log":
                return Ok(File.Exists(Log.FilePath) ? File.ReadAllText(Log.FilePath) : string.Empty);
            default:
                return Fail("неизвестная команда " + cmd);
        }
    }

    private object State() => new
    {
        title = Title,
        active = TabStrip.SelectedIndex,
        tabs = _tabs.Select(t => new { title = t.Title, path = t.FilePath, dirty = t.IsDirty, mode = ModeName(t.Mode), encoding = t.EncodingName, lineEnding = t.LineEnding.ToString(), ready = t.IsReady }).ToList(),
        theme = ThemeManager.IsDark ? "dark" : "light",
        tocVisible = _settings.TocVisible,
        zoom = _settings.PreviewZoom,
        recent = _settings.RecentFiles,
        welcomeVisible = WelcomePanel.Visibility == Visibility.Visible,
    };

    private async Task WaitTabsReadyAsync()
    {
        for (int i = 0; i < 100; i++)
        {
            if (_tabs.All(t => t.IsReady))
            {
                return;
            }

            await Task.Delay(100);
        }
    }

    /// <summary>Ждёт, пока страница отрисует последнюю версию текста (и схемы Mermaid).</summary>
    private async Task<bool> WaitRenderedAsync(double timeoutMs)
    {
        var tab = ActiveTab;
        if (tab?.Core is null)
        {
            return false;
        }

        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (tab.IsReady)
            {
                string raw = await tab.Core.ExecuteScriptAsync("JSON.stringify(window.mdv ? mdv.getState() : null)");
                string? json = JsonSerializer.Deserialize<string?>(raw);
                if (json is not null)
                {
                    using var doc = JsonDocument.Parse(json);
                    var st = doc.RootElement;
                    int rendered = st.GetProperty("renderedVersion").GetInt32();
                    int pending = st.GetProperty("mermaidPending").GetInt32();
                    if (rendered >= tab.TextVersion && pending == 0)
                    {
                        await Task.Delay(150);
                        return true;
                    }
                }
            }

            await Task.Delay(100);
        }

        return false;
    }
}
