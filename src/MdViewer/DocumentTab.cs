using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using MdViewer.Core.Documents;
using MdViewer.Core.Markdown;
using MdViewer.Core.Settings;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MdViewer;

/// <summary>
/// Вкладка документа: файл, его состояние (кодировка, переводы строк, правки) и собственный WebView2
/// со страницей редактора и просмотра.
/// </summary>
public sealed class DocumentTab : INotifyPropertyChanged, IDisposable
{
    public const string AppHost = "app.mdviewer.example";
    public const string DocHost = "doc.mdviewer.example";
    public const string PageUrl = "https://" + AppHost + "/page.html";

    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly MarkdownRenderer _renderer;
    private string? _filePath;
    private bool _isDirty;
    private string _untitledName;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _watchTimer;
    private int _renderRequested;
    private int _renderRunning;
    private string? _mappedFolder;

    public DocumentTab(MarkdownRenderer renderer, string untitledName)
    {
        _renderer = renderer;
        _untitledName = untitledName;
        Web = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.Transparent,
            Visibility = Visibility.Collapsed,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(Web, "DocumentView");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Сообщение со страницы (тип, данные, дополнительные объекты — например, перетащенные файлы).</summary>
    public event Action<DocumentTab, JsonElement, IEnumerable<object>?>? MessageReceived;

    /// <summary>Файл изменён другой программой (вызывается в потоке интерфейса после паузы).</summary>
    public event Action<DocumentTab>? ExternalChange;

    public WebView2 Web { get; }

    public string? FilePath
    {
        get => _filePath;
        private set
        {
            _filePath = value;
            OnChanged();
            OnChanged(nameof(Title));
            OnChanged(nameof(DisplayTitle));
            OnChanged(nameof(ToolTipText));
        }
    }

    public string Title => _filePath is null ? _untitledName : Path.GetFileName(_filePath);

    public string DisplayTitle => (IsDirty ? "● " : string.Empty) + Title;

    public string ToolTipText => _filePath ?? "Новый документ (не сохранён)";

    public bool IsDirty
    {
        get => _isDirty;
        set
        {
            if (_isDirty == value)
            {
                return;
            }

            _isDirty = value;
            OnChanged();
            OnChanged(nameof(DisplayTitle));
        }
    }

    public bool IsUntitled => _filePath is null;

    public string DocumentDirectory => _filePath is null ? DefaultUntitledDirectory : Path.GetDirectoryName(_filePath)!;

    public static string DefaultUntitledDirectory => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    /// <summary>Текст при последней загрузке или сохранении (переводы строк — \n).</summary>
    public string SavedText { get; private set; } = string.Empty;

    /// <summary>Текст на диске при последней загрузке или сохранении — для проверки внешних изменений.</summary>
    public string LastKnownDiskText { get; set; } = string.Empty;

    /// <summary>Текущий текст редактора (переводы строк — \n).</summary>
    public string CurrentText { get; private set; } = string.Empty;

    public Encoding Encoding { get; private set; } = new UTF8Encoding(false);

    public string EncodingName { get; private set; } = "UTF-8";

    public LineEnding LineEnding { get; private set; } = LineEnding.CrLf;

    public ViewMode Mode { get; set; } = ViewMode.View;

    public int TextVersion { get; private set; }

    public bool IsReady { get; private set; }

    public bool IsEmptyUntitled => IsUntitled && !IsDirty && CurrentText.Length == 0;

    public TaskCompletionSource ReadyTask { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource? ExportReady { get; set; }

    public bool SuppressExternalCheck { get; set; }

    public bool DeletedNoticeShown { get; set; }

    public CoreWebView2? Core => Web.CoreWebView2;

    /// <summary>Создаёт WebView2 и загружает страницу. Начальные данные отправляются после сообщения ready.</summary>
    public async Task InitializeAsync(CoreWebView2Environment env, string wwwroot)
    {
        await Web.EnsureCoreWebView2Async(env);
        var core = Web.CoreWebView2;
        var s = core.Settings;
        s.AreDevToolsEnabled = Environment.GetEnvironmentVariable("MDVIEWER_DEVTOOLS") == "1";
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.IsPinchZoomEnabled = false;
        s.IsBuiltInErrorPageEnabled = false;
        s.AreDefaultScriptDialogsEnabled = false;
        s.AreHostObjectsAllowed = false;
        core.SetVirtualHostNameToFolderMapping(AppHost, wwwroot, CoreWebView2HostResourceAccessKind.Allow);
        MapDocumentFolder();

        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(PageUrl, StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
            }
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (LinkTargets.IsSafeExternal(e.Uri))
            {
                App.OpenUrl(e.Uri);
            }
        };
        core.ContextMenuRequested += OnContextMenuRequested;
        core.WebMessageReceived += OnWebMessage;
        core.ProcessFailed += (_, e) =>
        {
            Services.Log.Error($"Сбой процесса WebView2: {e.ProcessFailedKind}");
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited || e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            {
                IsReady = false;
                core.Reload();
            }
        };
        core.Navigate(PageUrl);
    }

    /// <summary>Папка документа доступна странице по адресу https://doc.mdviewer.example/ — для картинок и ссылок.</summary>
    public void MapDocumentFolder()
    {
        var core = Web.CoreWebView2;
        if (core is null)
        {
            return;
        }

        string folder = DocumentDirectory;
        if (string.Equals(folder, _mappedFolder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_mappedFolder is not null)
        {
            core.ClearVirtualHostNameToFolderMapping(DocHost);
        }

        if (Directory.Exists(folder))
        {
            core.SetVirtualHostNameToFolderMapping(DocHost, folder, CoreWebView2HostResourceAccessKind.Allow);
            _mappedFolder = folder;
        }
    }

    private void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "copy", "cut", "paste", "pasteAndMatchStyle", "selectAll", "copyLinkLocation", "copyImage", "copyImageLocation", "undo", "redo", "emoji",
        };
        var items = e.MenuItems;
        for (int i = items.Count - 1; i >= 0; i--)
        {
            var item = items[i];
            if (item.Kind == CoreWebView2ContextMenuItemKind.Separator)
            {
                continue;
            }

            if (!allowed.Contains(item.Name))
            {
                items.RemoveAt(i);
            }
        }

        // Убрать лишние разделители
        for (int i = items.Count - 1; i >= 0; i--)
        {
            bool sep = items[i].Kind == CoreWebView2ContextMenuItemKind.Separator;
            bool edge = i == 0 || i == items.Count - 1;
            if (sep && (edge || items[i - 1].Kind == CoreWebView2ContextMenuItemKind.Separator))
            {
                items.RemoveAt(i);
            }
        }

        if (items.Count == 0)
        {
            e.Handled = true;
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement msg;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            msg = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        string type = msg.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        switch (type)
        {
            case "ready":
                IsReady = true;
                ReadyTask.TrySetResult();
                break;
            case "textChanged":
                CurrentText = msg.GetProperty("text").GetString() ?? string.Empty;
                TextVersion = msg.TryGetProperty("version", out var v) ? v.GetInt32() : TextVersion + 1;
                IsDirty = !string.Equals(CurrentText, SavedText, StringComparison.Ordinal);

                RequestRender();
                break;
            case "exportReady":
                ExportReady?.TrySetResult();
                break;
        }

        IEnumerable<object>? extra = null;
        try
        {
            extra = e.AdditionalObjects;
        }
        catch
        {
            // старые версии WebView2
        }

        MessageReceived?.Invoke(this, msg, extra);
    }

    /// <summary>Устанавливает загруженный документ.</summary>
    public void SetLoaded(LoadedDocument doc)
    {
        FilePath = doc.Path;
        Encoding = doc.Encoding;
        EncodingName = doc.EncodingName;
        LineEnding = doc.LineEnding;
        string lf = TextFileCodec.NormalizeToLf(doc.Text);
        SavedText = lf;
        CurrentText = lf;
        LastKnownDiskText = doc.Text;
        IsDirty = false;
        DeletedNoticeShown = false;
        MapDocumentFolder();
        Watch();
        OnChanged(nameof(EncodingName));
    }

    /// <summary>После сохранения: новый путь, текст сохранён.</summary>
    public void MarkSaved(string path, string text)
    {
        bool pathChanged = !string.Equals(path, _filePath, StringComparison.OrdinalIgnoreCase);
        FilePath = Path.GetFullPath(path);
        SavedText = text;
        LastKnownDiskText = text;
        if (!string.Equals(EncodingName, "UTF-8", StringComparison.Ordinal))
        {
            Encoding = new UTF8Encoding(false);
            EncodingName = "UTF-8";
            OnChanged(nameof(EncodingName));
        }

        IsDirty = !string.Equals(CurrentText, text, StringComparison.Ordinal);
        DeletedNoticeShown = false;
        if (pathChanged)
        {
            MapDocumentFolder();
            Watch();
            RequestRender();
        }
    }

    /// <summary>Текст в редакторе (запрос к странице, чтобы учесть последние нажатия).</summary>
    public async Task<string> GetTextAsync()
    {
        if (Core is null || !IsReady)
        {
            return CurrentText;
        }

        try
        {
            string json = await Core.ExecuteScriptAsync("window.mdv ? mdv.getText() : null");
            string? text = JsonSerializer.Deserialize<string?>(json);
            if (text is not null)
            {
                CurrentText = text;
            }
        }
        catch (Exception ex)
        {
            Services.Log.Error("Не удалось получить текст редактора", ex);
        }

        return CurrentText;
    }

    public void Post(object message)
    {
        if (Core is null)
        {
            return;
        }

        try
        {
            Core.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
        }
        catch (Exception ex)
        {
            Services.Log.Error("Не удалось отправить сообщение странице", ex);
        }
    }

    /// <summary>Перерисовать просмотр по текущему тексту (в фоне, последний запрос побеждает).</summary>
    public void RequestRender()
    {
        Interlocked.Exchange(ref _renderRequested, 1);
        if (Interlocked.CompareExchange(ref _renderRunning, 1, 0) != 0)
        {
            return;
        }

        var dispatcher = Web.Dispatcher;
        Task.Run(() =>
        {
            try
            {
                while (Interlocked.Exchange(ref _renderRequested, 0) == 1)
                {
                    string text = CurrentText;
                    int version = TextVersion;
                    RenderResult r;
                    try
                    {
                        r = _renderer.Render(text);
                    }
                    catch (Exception ex)
                    {
                        Services.Log.Error("Ошибка разбора Markdown", ex);
                        r = new RenderResult("<div class=\"mdv-error\">Не удалось показать документ: " + System.Net.WebUtility.HtmlEncode(ex.Message) + "</div>", Array.Empty<TocEntry>(), false, false);
                    }

                    dispatcher.InvokeAsync(() => Post(new { type = "render", html = r.Html, toc = r.Toc, version }));
                }
            }
            finally
            {
                Interlocked.Exchange(ref _renderRunning, 0);
                if (Volatile.Read(ref _renderRequested) == 1)
                {
                    dispatcher.InvokeAsync(RequestRender);
                }
            }
        });
    }

    /// <summary>Заменить текст (перезагрузка с диска).</summary>
    public void ReplaceText(LoadedDocument doc)
    {
        SetLoaded(doc);
        TextVersion++;
        Post(new { type = "version", version = TextVersion });
        Post(new { type = "setText", text = doc.Text, keepScroll = true });
        RequestRender();
    }

    /// <summary>Создаёт пустой документ (новая вкладка).</summary>
    public void SetUntitled()
    {
        FilePath = null;
        SavedText = string.Empty;
        CurrentText = string.Empty;
        LastKnownDiskText = string.Empty;
        LineEnding = LineEnding.CrLf;
        IsDirty = false;
    }

    public void SetUntitledName(string name)
    {
        _untitledName = name;
        OnChanged(nameof(Title));
        OnChanged(nameof(DisplayTitle));
    }

    private void Watch()
    {
        StopWatching();
        if (_filePath is null)
        {
            return;
        }

        try
        {
            string dir = Path.GetDirectoryName(_filePath)!;
            if (!Directory.Exists(dir))
            {
                return;
            }

            _watcher = new FileSystemWatcher(dir, Path.GetFileName(_filePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
            };
            FileSystemEventHandler h = (_, _) => ScheduleExternalCheck();
            _watcher.Changed += h;
            _watcher.Created += h;
            _watcher.Deleted += h;
            _watcher.Renamed += (_, _) => ScheduleExternalCheck();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Services.Log.Error("Не удалось следить за файлом " + _filePath, ex);
        }
    }

    private void ScheduleExternalCheck()
    {
        Web.Dispatcher.InvokeAsync(() =>
        {
            _watchTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Normal, (_, _) =>
            {
                _watchTimer!.Stop();
                ExternalChange?.Invoke(this);
            }, Web.Dispatcher);
            _watchTimer.Stop();
            _watchTimer.Start();
        });
    }

    private void StopWatching()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    public void Dispose()
    {
        StopWatching();
        _watchTimer?.Stop();
        try
        {
            Web.Dispose();
        }
        catch
        {
            // ignore
        }
    }

    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
