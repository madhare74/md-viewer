using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MdViewer.Core.Markdown;
using MdViewer.Core.Settings;
using MdViewer.Dialogs;
using MdViewer.Services;
using Microsoft.Web.WebView2.Core;

namespace MdViewer;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<DocumentTab> _tabs = new();
    private readonly MarkdownRenderer _renderer = new();
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly IReadOnlyList<string> _startupFiles;
    private readonly bool _automation;
    private CoreWebView2Environment? _env;
    private Task<CoreWebView2Environment>? _envTask;
    private bool _closeConfirmed;
    private bool _closing;
    private int _untitledCounter;
    private double _uiScale = 1.0;
    private Point _dragStart;
    private DocumentTab? _dragTab;
    private AutomationServer? _automationServer;

    public MainWindow(IReadOnlyList<string> startupFiles, bool automation)
    {
        _startupFiles = startupFiles;
        _automation = automation;
        _store = new SettingsStore(SettingsStore.DefaultDirectory);
        _settings = _store.Load();
        InitializeComponent();
        TabStrip.ItemsSource = _tabs;
        ApplyTheme();
        RestorePlacement();
        UpdateMenus();
        UpdateWelcome();

        if (double.TryParse(Environment.GetEnvironmentVariable("MDVIEWER_UI_SCALE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double scale) && scale > 0.5 && scale <= 3)
        {
            _uiScale = scale;
        }

        SourceInitialized += (_, _) =>
        {
            ThemeManager.ApplyTitleBar(this);
            ApplyUiScale();
        };
        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        DragEnter += OnWindowDragEnter;
        DragOver += OnWindowDragOver;
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        Drop += OnWindowDrop;
        TabStrip.PreviewMouseLeftButtonDown += OnTabStripMouseDown;
        TabStrip.PreviewMouseMove += OnTabStripMouseMove;
        TabStrip.PreviewMouseUp += OnTabStripMouseUp;
        TabStrip.Drop += OnTabStripDrop;
        TabStrip.DragOver += OnTabStripDragOver;
        ThemeManager.SystemThemeChanged += () =>
        {
            if (_settings.Theme == ThemeSetting.System)
            {
                ApplyTheme();
            }
        };

        string? pipe = Environment.GetEnvironmentVariable("MDVIEWER_AUTOMATION_PIPE");
        if (!string.IsNullOrEmpty(pipe))
        {
            _automationServer = new AutomationServer(pipe, this);
            _automationServer.Start();
        }
    }

    public DocumentTab? ActiveTab => TabStrip.SelectedItem as DocumentTab;

    public IReadOnlyList<DocumentTab> Tabs => _tabs;

    public AppSettings Settings => _settings;

    public bool EffectiveDark => ThemeManager.IsDark;

    internal static string WwwRoot => Path.Combine(AppContext.BaseDirectory, "wwwroot");

    // ---------------- Запуск ----------------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await GetEnvironmentAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось запустить WebView2", ex);
            ChoiceDialog.Info(this, "Не удалось запустить компонент WebView2, который показывает документы.", ex.Message);
            return;
        }

        var toOpen = new List<string>();
        toOpen.AddRange(SessionTabs.Restore(_settings.OpenTabs));
        int active = _settings.ActiveTab;
        if (Environment.GetEnvironmentVariable("MDVIEWER_NO_RESTORE") == "1")
        {
            toOpen.Clear();
        }

        foreach (string f in _startupFiles)
        {
            if (!toOpen.Contains(f, StringComparer.OrdinalIgnoreCase))
            {
                toOpen.Add(f);
            }
        }

        if (toOpen.Count > 0)
        {
            await OpenFilesAsync(toOpen, activate: false, quiet: true);
            if (_startupFiles.Count > 0)
            {
                SelectTabByPath(_startupFiles[^1]);
            }
            else if (active >= 0 && active < _tabs.Count)
            {
                TabStrip.SelectedIndex = active;
            }
            else if (_tabs.Count > 0)
            {
                TabStrip.SelectedIndex = 0;
            }

            // Файлы из командной строки, которые не открылись, показывают сообщение; восстановленные — нет.
        }

        UpdateWelcome();
        Log.Info($"Запуск: открыто вкладок {_tabs.Count}");
    }

    internal Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        if (_env is not null)
        {
            return Task.FromResult(_env);
        }

        _envTask ??= CreateEnvironmentAsync();
        return _envTask;
    }

    private async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        string dataDir = Environment.GetEnvironmentVariable("MDVIEWER_DATA_DIR") is { Length: > 0 } d
            ? Path.Combine(d, "WebView2")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MD Viewer", "WebView2");
        Directory.CreateDirectory(dataDir);
        var options = new CoreWebView2EnvironmentOptions { Language = "ru-RU" };
        _env = await CoreWebView2Environment.CreateAsync(null, dataDir, options);
        return _env;
    }

    // ---------------- Вкладки ----------------

    private async Task<DocumentTab> CreateTabAsync(Action<DocumentTab> setup, int? index = null)
    {
        var env = await GetEnvironmentAsync();
        var tab = new DocumentTab(_renderer, NextUntitledName());
        setup(tab);
        tab.Mode = _settings.DefaultMode;
        tab.MessageReceived += OnTabMessage;
        tab.ExternalChange += OnExternalChange;
        tab.PropertyChanged += OnTabPropertyChanged;
        ContentHost.Children.Add(tab.Web);
        if (index is int i && i >= 0 && i <= _tabs.Count)
        {
            _tabs.Insert(i, tab);
        }
        else
        {
            _tabs.Add(tab);
        }

        await tab.InitializeAsync(env, WwwRoot);
        if (_uiScale != 1.0)
        {
            tab.Web.ZoomFactor = _uiScale;
        }

        // Начальные данные — после того как страница сообщит о готовности.
        await WaitReadyAsync(tab);
        var render = await Task.Run(() => _renderer.Render(tab.CurrentText));
        tab.Post(new
        {
            type = "init",
            text = tab.CurrentText,
            html = render.Html,
            toc = render.Toc,
            version = tab.TextVersion,
            mode = ModeName(tab.Mode),
            theme = ThemeManager.IsDark ? "dark" : "light",
            settings = PageSettings(),
        });
        UpdateWelcome();
        return tab;
    }

    private static async Task WaitReadyAsync(DocumentTab tab)
    {
        var done = await Task.WhenAny(tab.ReadyTask.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        if (done != tab.ReadyTask.Task)
        {
            Log.Error("Страница документа не ответила за 30 секунд");
        }
    }

    private string NextUntitledName()
    {
        _untitledCounter++;
        return _untitledCounter == 1 ? "Без имени" : $"Без имени {_untitledCounter}";
    }

    internal object PageSettings() => new
    {
        wrap = _settings.WordWrap,
        zoom = _settings.PreviewZoom,
        fullWidth = _settings.FullWidth,
        tocVisible = _settings.TocVisible,
        tocWidth = _settings.TocWidth,
        split = _settings.SplitRatio,
        editorFont = _settings.EditorFontFamily,
        editorFontSize = _settings.EditorFontSize,
    };

    internal static string ModeName(ViewMode m) => m switch
    {
        ViewMode.Split => "split",
        ViewMode.Edit => "edit",
        _ => "view",
    };

    private static ViewMode ParseMode(string? s) => s switch
    {
        "split" => ViewMode.Split,
        "edit" => ViewMode.Edit,
        _ => ViewMode.View,
    };

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var t in _tabs)
        {
            t.Web.Visibility = t == ActiveTab ? Visibility.Visible : Visibility.Collapsed;
        }

        if (ActiveTab is { } tab)
        {
            TabStrip.ScrollIntoView(tab);
            Dispatcher.InvokeAsync(() =>
            {
                if (ActiveTab == tab && !_closing)
                {
                    tab.Web.Focus();
                    tab.Post(new { type = "command", name = "focus" });
                }
            }, System.Windows.Threading.DispatcherPriority.Input);
        }

        UpdateWelcome();
        UpdateMenus();
        UpdateStatus();
        UpdateTitle();
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender == ActiveTab)
        {
            UpdateTitle();
            UpdateStatus();
        }
    }

    private void UpdateWelcome()
    {
        bool empty = _tabs.Count == 0;
        WelcomePanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        TabStripBorder.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        var recent = _settings.RecentFiles.Take(10).ToList();
        WelcomeRecent.ItemsSource = recent;
        WelcomeRecentHeader.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateTitle()
    {
        var t = ActiveTab;
        Title = t is null ? "MD Viewer" : $"{(t.IsDirty ? "● " : string.Empty)}{t.Title} — MD Viewer";
    }

    private void UpdateStatus()
    {
        var t = ActiveTab;
        StatusPath.Text = t is null ? string.Empty : t.FilePath ?? "Новый документ — не сохранён";
        StatusPath.ToolTip = t?.FilePath;
        StatusEncoding.Text = t?.EncodingName ?? string.Empty;
        StatusLineEnding.Text = t is null ? string.Empty : Core.Documents.TextFileCodec.LineEndingName(t.LineEnding);
        StatusZoom.Text = $"{Math.Round(_settings.PreviewZoom * 100)} %";
    }

    private void UpdateMenus()
    {
        var mode = ActiveTab?.Mode;
        ModeViewButton.IsChecked = mode == ViewMode.View;
        ModeSplitButton.IsChecked = mode == ViewMode.Split;
        ModeEditButton.IsChecked = mode == ViewMode.Edit;
        ViewModeMenu.IsChecked = mode == ViewMode.View;
        SplitModeMenu.IsChecked = mode == ViewMode.Split;
        EditModeMenu.IsChecked = mode == ViewMode.Edit;
        TocButton.IsChecked = _settings.TocVisible;
        TocMenu.IsChecked = _settings.TocVisible;
        FullWidthMenu.IsChecked = _settings.FullWidth;
        WrapMenu.IsChecked = _settings.WordWrap;
        ThemeLightMenu.IsChecked = _settings.Theme == ThemeSetting.Light;
        ThemeDarkMenu.IsChecked = _settings.Theme == ThemeSetting.Dark;
        ThemeSystemMenu.IsChecked = _settings.Theme == ThemeSetting.System;
        bool has = ActiveTab is not null;
        ModeViewButton.IsEnabled = ModeSplitButton.IsEnabled = ModeEditButton.IsEnabled = has;
        SaveButton.IsEnabled = has;
        SaveMenuItem.IsEnabled = SaveAsMenuItem.IsEnabled = PrintMenuItem.IsEnabled = ExportMenu.IsEnabled = has;
    }

    private void SelectTabByPath(string path)
    {
        var t = FindTab(path);
        if (t is not null)
        {
            TabStrip.SelectedItem = t;
        }
    }

    private DocumentTab? FindTab(string path) =>
        _tabs.FirstOrDefault(t => t.FilePath is not null && string.Equals(t.FilePath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));

    internal void SelectNextTab(int dir)
    {
        if (_tabs.Count < 2)
        {
            return;
        }

        int i = TabStrip.SelectedIndex;
        TabStrip.SelectedIndex = ((i + dir) % _tabs.Count + _tabs.Count) % _tabs.Count;
    }

    // ---------------- Режимы ----------------

    internal void SetMode(ViewMode mode)
    {
        var t = ActiveTab;
        if (t is null)
        {
            UpdateMenus();
            return;
        }

        t.Mode = mode;
        t.Post(new { type = "mode", mode = ModeName(mode) });
        UpdateMenus();
    }

    private void OnModeView(object sender, RoutedEventArgs e) => SetMode(ViewMode.View);

    private void OnModeSplit(object sender, RoutedEventArgs e) => SetMode(ViewMode.Split);

    private void OnModeEdit(object sender, RoutedEventArgs e) => SetMode(ViewMode.Edit);

    // ---------------- Настройки и тема ----------------

    internal void ApplyTheme()
    {
        bool dark = ThemeManager.ResolveDark(_settings.Theme);
        ThemeManager.Apply(dark);
        foreach (var t in _tabs)
        {
            t.Post(new { type = "theme", theme = dark ? "dark" : "light" });
        }

        UpdateMenus();
    }

    internal void BroadcastSettings()
    {
        var s = PageSettings();
        foreach (var t in _tabs)
        {
            t.Post(new { type = "settings", settings = s });
        }

        UpdateMenus();
        UpdateStatus();
        SaveSettings();
    }

    internal void SaveSettings()
    {
        try
        {
            _settings.OpenTabs = SessionTabs.Capture(_tabs.Select(t => t.FilePath));
            _settings.ActiveTab = Math.Max(0, _tabs.Where(t => t.FilePath is not null).ToList().IndexOf(ActiveTab!));
            _store.Save(_settings);
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось сохранить настройки", ex);
        }
    }

    private void OnThemeMenu(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<ThemeSetting>(tag, out var theme))
        {
            SetTheme(theme);
        }
    }

    internal void SetTheme(ThemeSetting theme)
    {
        _settings.Theme = theme;
        ApplyTheme();
        SaveSettings();
    }

    private void OnToggleToc(object sender, RoutedEventArgs e) => ToggleToc();

    internal void ToggleToc()
    {
        _settings.TocVisible = !_settings.TocVisible;
        BroadcastSettings();
    }

    private void OnToggleFullWidth(object sender, RoutedEventArgs e)
    {
        _settings.FullWidth = !_settings.FullWidth;
        BroadcastSettings();
    }

    private void OnToggleWrap(object sender, RoutedEventArgs e)
    {
        _settings.WordWrap = !_settings.WordWrap;
        BroadcastSettings();
    }

    internal void ChangeZoom(int dir)
    {
        _settings.PreviewZoom = dir == 0 ? 1.0 : Zoom.Step(_settings.PreviewZoom, dir);
        BroadcastSettings();
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => ChangeZoom(1);

    private void OnZoomOut(object sender, RoutedEventArgs e) => ChangeZoom(-1);

    private void OnZoomReset(object sender, RoutedEventArgs e) => ChangeZoom(0);

    private void OnSettings(object sender, RoutedEventArgs e) => ShowSettings();

    internal void ShowSettings()
    {
        var dlg = new SettingsWindow(_settings) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            ApplyTheme();
            BroadcastSettings();
        }
    }

    private void OnShortcutsHelp(object sender, RoutedEventArgs e)
    {
        ChoiceDialog.Info(this, "Горячие клавиши",
            "Ctrl+O — открыть, Ctrl+N — новый документ, Ctrl+S — сохранить, Ctrl+Shift+S — сохранить как, Ctrl+W — закрыть вкладку, " +
            "Ctrl+Tab / Ctrl+Shift+Tab — следующая / предыдущая вкладка.\n" +
            "Ctrl+1 — просмотр, Ctrl+2 — правка и просмотр, Ctrl+3 — правка, Ctrl+Shift+O — оглавление.\n" +
            "Ctrl+F — найти, Ctrl+H — заменить, Ctrl+B — жирный, Ctrl+I — курсив, Ctrl+K — ссылка, Ctrl+Z / Ctrl+Y — отменить / повторить.\n" +
            "Ctrl+колёсико, Ctrl+Плюс / Ctrl+Минус — масштаб, Ctrl+0 — обычный масштаб, Ctrl+P — печать.");
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        string version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0";
        ChoiceDialog.Info(this, $"MD Viewer {version}",
            "Просмотр и редактирование документов Markdown в стиле GitHub: формулы KaTeX, схемы Mermaid, подсветка кода, экспорт в PDF и HTML. " +
            "Работает без интернета.\nНастройки: " + _store.Directory);
    }

    // ---------------- Клавиатура ----------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if ((mods & ModifierKeys.Control) == 0)
        {
            return;
        }

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        string? name = key switch
        {
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            >= Key.NumPad0 and <= Key.NumPad9 => ((int)(key - Key.NumPad0)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Key.OemPlus or Key.Add => "Plus",
            Key.OemMinus or Key.Subtract => "Minus",
            Key.OemComma => "Comma",
            Key.Tab => "Tab",
            Key.F4 => "F4",
            Key.PageDown => "PageDown",
            Key.PageUp => "PageUp",
            _ => null,
        };
        if (name is null)
        {
            return;
        }

        string combo = "Ctrl+" + ((mods & ModifierKeys.Shift) != 0 && name != "Plus" ? "Shift+" : string.Empty) + name;
        if (HandleShortcut(combo, fromPage: false))
        {
            e.Handled = true;
        }
    }

    /// <summary>Горячие клавиши приложения (из окна и со страницы документа).</summary>
    internal bool HandleShortcut(string key, bool fromPage)
    {
        switch (key)
        {
            case "Ctrl+O": ShowOpenDialog(); return true;
            case "Ctrl+N": _ = NewDocumentAsync(); return true;
            case "Ctrl+S": _ = SaveActiveAsync(false); return true;
            case "Ctrl+Shift+S": _ = SaveActiveAsync(true); return true;
            case "Ctrl+W":
            case "Ctrl+F4":
                if (ActiveTab is { } t)
                {
                    _ = CloseTabAsync(t);
                }

                return true;
            case "Ctrl+Tab":
            case "Ctrl+PageDown": SelectNextTab(1); return true;
            case "Ctrl+Shift+Tab":
            case "Ctrl+PageUp": SelectNextTab(-1); return true;
            case "Ctrl+1": SetMode(ViewMode.View); return true;
            case "Ctrl+2": SetMode(ViewMode.Split); return true;
            case "Ctrl+3": SetMode(ViewMode.Edit); return true;
            case "Ctrl+Shift+O": ToggleToc(); return true;
            case "Ctrl+Plus": ChangeZoom(1); return true;
            case "Ctrl+Minus": ChangeZoom(-1); return true;
            case "Ctrl+0": ChangeZoom(0); return true;
            case "Ctrl+P": _ = PrintAsync(); return true;
            case "Ctrl+Comma": ShowSettings(); return true;
        }

        if (!fromPage && ActiveTab is { } tab)
        {
            string? cmd = key switch
            {
                "Ctrl+F" => "find",
                "Ctrl+H" => "replace",
                "Ctrl+B" => "bold",
                "Ctrl+I" => "italic",
                "Ctrl+K" => "link",
                _ => null,
            };
            if (cmd is not null)
            {
                tab.Post(new { type = "command", name = cmd });
                return true;
            }
        }

        return false;
    }

    private void OnPageCommand(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string cmd } && ActiveTab is { } tab)
        {
            tab.Post(new { type = "command", name = cmd });
            tab.Web.Focus();
        }
    }

    // ---------------- Размер интерфейса (проверка масштаба 150/200 %) ----------------

    internal void SetUiScale(double scale)
    {
        _uiScale = scale;
        ApplyUiScale();
    }

    private void ApplyUiScale()
    {
        App.UiScale = _uiScale;
        RootPanel.LayoutTransform = _uiScale == 1.0 ? Transform.Identity : new ScaleTransform(_uiScale, _uiScale);
        foreach (var t in _tabs)
        {
            if (t.Core is not null)
            {
                t.Web.ZoomFactor = _uiScale;
            }
        }
    }

    // ---------------- Окно ----------------

    private void RestorePlacement()
    {
        var p = _settings.Window;
        if (p is null || p.Width < 300 || p.Height < 200)
        {
            return;
        }

        var area = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var r = new Rect(p.Left, p.Top, p.Width, p.Height);
        if (!area.IntersectsWith(r))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = p.Left;
        Top = p.Top;
        Width = p.Width;
        Height = p.Height;
        if (p.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SavePlacement()
    {
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings.Window = new WindowPlacement { Left = r.Left, Top = r.Top, Width = r.Width, Height = r.Height, Maximized = WindowState == WindowState.Maximized };
    }

    internal void OpenFromOtherInstance(string[] paths)
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetForegroundWindow(hwnd);
        if (paths.Length > 0)
        {
            _ = OpenFilesAsync(paths, activate: true, quiet: false);
        }
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();
}
