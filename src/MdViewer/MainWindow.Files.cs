using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MdViewer.Core.Documents;
using MdViewer.Core.Settings;
using MdViewer.Dialogs;
using MdViewer.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace MdViewer;

public partial class MainWindow
{
    private const string TabDragFormat = "MdViewerTab";
    private string? _lastDialogDir;

    // ---------------- Открытие ----------------

    private void OnOpen(object sender, RoutedEventArgs e) => ShowOpenDialog();

    internal void ShowOpenDialog()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Открыть документ Markdown",
            Filter = "Документы Markdown (*.md; *.markdown)|*.md;*.markdown;*.mdown;*.mkd;*.mkdn;*.mdwn|Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };
        string? dir = _lastDialogDir ?? (ActiveTab?.FilePath is { } p ? Path.GetDirectoryName(p) : null);
        if (dir is not null && Directory.Exists(dir))
        {
            dlg.InitialDirectory = dir;
        }

        if (dlg.ShowDialog(this) == true)
        {
            _lastDialogDir = Path.GetDirectoryName(dlg.FileNames[0]);
            _ = OpenFilesAsync(dlg.FileNames, activate: true, quiet: false);
        }
    }

    private void OnNew(object sender, RoutedEventArgs e) => _ = NewDocumentAsync();

    internal async Task<DocumentTab> NewDocumentAsync()
    {
        var tab = await CreateTabAsync(t => t.SetUntitled());
        TabStrip.SelectedItem = tab;
        SetMode(ViewMode.Split);
        return tab;
    }

    /// <summary>Открывает файлы вкладками. Уже открытый файл просто становится активным.</summary>
    internal async Task OpenFilesAsync(IEnumerable<string> paths, bool activate, bool quiet, Encoding? encoding = null)
    {
        DocumentTab? last = null;
        foreach (string raw in paths)
        {
            string path;
            try
            {
                path = Path.GetFullPath(raw);
            }
            catch (Exception)
            {
                continue;
            }

            var existing = FindTab(path);
            if (existing is not null)
            {
                last = existing;
                continue;
            }

            var tab = await OpenFileAsync(path, quiet, encoding);
            if (tab is not null)
            {
                last = tab;
            }
        }

        if (last is not null && (activate || ActiveTab is null))
        {
            TabStrip.SelectedItem = last;
        }

        UpdateWelcome();
        SaveSettings();
    }

    private async Task<DocumentTab?> OpenFileAsync(string path, bool quiet, Encoding? encoding)
    {
        if (!File.Exists(path))
        {
            if (!quiet)
            {
                ChoiceDialog.Info(this, $"Файл не найден или удалён: «{Path.GetFileName(path)}».", path);
            }

            RecentFilesList.Remove(_settings.RecentFiles, path);
            return null;
        }

        long size = DocumentFile.GetLength(path);
        if (size > TextFileCodec.LargeFileThreshold && !quiet)
        {
            if (!ChoiceDialog.Confirm(this, $"Файл «{Path.GetFileName(path)}» очень большой ({size / (1024 * 1024)} МБ). Открытие может занять время. Открыть?", "Открыть"))
            {
                return null;
            }
        }
        else if (size > TextFileCodec.LargeFileThreshold && quiet)
        {
            return null;
        }

        LoadedDocument doc;
        try
        {
            doc = await Task.Run(() => DocumentFile.Load(path, encoding));
        }
        catch (BinaryFileException)
        {
            if (!quiet)
            {
                ChoiceDialog.Info(this, $"Файл «{Path.GetFileName(path)}» не похож на текстовый документ и не может быть открыт.", path);
            }

            return null;
        }
        catch (UnauthorizedAccessException)
        {
            if (!quiet)
            {
                ChoiceDialog.Info(this, $"Нет доступа к файлу «{Path.GetFileName(path)}».", "Проверьте права на файл или откройте его от имени другого пользователя.");
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException || ex is FileNotFoundException)
        {
            if (!quiet)
            {
                ChoiceDialog.Info(this, $"Не удалось открыть файл «{Path.GetFileName(path)}».", ex.Message);
            }

            return null;
        }

        // Пустая вкладка «Без имени» без правок заменяется открытым файлом.
        DocumentTab? replace = _tabs.Count == 1 && _tabs[0].IsEmptyUntitled ? _tabs[0] : null;

        var tab = await CreateTabAsync(t => t.SetLoaded(doc));
        if (replace is not null)
        {
            RemoveTab(replace);
        }

        RecentFilesList.Add(_settings.RecentFiles, doc.Path);
        Log.Info($"Открыт файл {doc.Path} ({doc.EncodingName}, {doc.Length} байт)");
        return tab;
    }

    private void OnRecentMenuOpened(object sender, RoutedEventArgs e)
    {
        RecentMenu.Items.Clear();
        if (_settings.RecentFiles.Count == 0)
        {
            RecentMenu.Items.Add(new MenuItem { Header = "(пусто)", IsEnabled = false });
            return;
        }

        int n = 1;
        foreach (string path in _settings.RecentFiles.ToList())
        {
            var item = new MenuItem
            {
                Header = new TextBlock { Text = $"{n++}. {Path.GetFileName(path)}", MaxWidth = 420, TextTrimming = TextTrimming.CharacterEllipsis },
                ToolTip = path,
                Tag = path,
            };
            item.Click += (_, _) => _ = OpenFilesAsync([path], activate: true, quiet: false);
            RecentMenu.Items.Add(item);
        }

        RecentMenu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "Очистить список" };
        clear.Click += (_, _) =>
        {
            _settings.RecentFiles.Clear();
            SaveSettings();
            UpdateWelcome();
        };
        RecentMenu.Items.Add(clear);
    }

    private void OnWelcomeRecent(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path })
        {
            _ = OpenFilesAsync([path], activate: true, quiet: false);
        }
    }

    private void OnEncodingMenuOpened(object sender, RoutedEventArgs e)
    {
        EncodingMenu.Items.Clear();
        var tab = ActiveTab;
        if (tab?.FilePath is null)
        {
            EncodingMenu.Items.Add(new MenuItem { Header = "(документ не сохранён)", IsEnabled = false });
            return;
        }

        EncodingMenu.Items.Add(new MenuItem { Header = $"Сейчас: {tab.EncodingName}", IsEnabled = false });
        EncodingMenu.Items.Add(new Separator());
        foreach (var (name, get) in TextFileCodec.ManualEncodings)
        {
            var item = new MenuItem { Header = "Открыть заново в " + name, IsChecked = tab.EncodingName.StartsWith(name, StringComparison.Ordinal) };
            item.Click += (_, _) => ReopenWithEncoding(tab, get());
            EncodingMenu.Items.Add(item);
        }
    }

    private void ReopenWithEncoding(DocumentTab tab, Encoding encoding)
    {
        if (tab.FilePath is null)
        {
            return;
        }

        if (tab.IsDirty && !ChoiceDialog.Confirm(this, "Несохранённые изменения будут потеряны. Открыть файл заново в другой кодировке?", "Открыть заново"))
        {
            return;
        }

        try
        {
            var doc = DocumentFile.Load(tab.FilePath, encoding);
            tab.ReplaceText(doc);
            UpdateStatus();
        }
        catch (Exception ex)
        {
            ChoiceDialog.Info(this, "Не удалось открыть файл заново.", ex.Message);
        }
    }

    // ---------------- Сохранение ----------------

    private void OnSave(object sender, RoutedEventArgs e) => _ = SaveActiveAsync(false);

    private void OnSaveAs(object sender, RoutedEventArgs e) => _ = SaveActiveAsync(true);

    internal Task<bool> SaveActiveAsync(bool saveAs) => ActiveTab is { } t ? SaveTabAsync(t, saveAs) : Task.FromResult(false);

    /// <summary>Сохраняет вкладку. Возвращает false, если пользователь отменил или сохранить не удалось.</summary>
    internal async Task<bool> SaveTabAsync(DocumentTab tab, bool saveAs, string? targetPath = null)
    {
        string text = await tab.GetTextAsync();
        string? path = targetPath ?? tab.FilePath;
        if (targetPath is null && (saveAs || path is null))
        {
            path = AskSavePath(tab, ".md", "Документ Markdown (*.md)|*.md|Документ Markdown (*.markdown)|*.markdown|Все файлы (*.*)|*.*");
            if (path is null)
            {
                return false;
            }
        }

        try
        {
            if (File.Exists(path) && new FileInfo(path!).IsReadOnly)
            {
                throw new UnauthorizedAccessException("Файл только для чтения");
            }

            DocumentFile.Save(path!, text, tab.LineEnding);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            Log.Error("Ошибка сохранения " + path, ex);
            string reason = DescribeWriteError(ex);
            if (targetPath is not null)
            {
                return false;
            }

            if (ChoiceDialog.Confirm(this, $"Не удалось сохранить файл «{Path.GetFileName(path)}». {reason}", "Сохранить как…", "Отмена", path))
            {
                return await SaveTabAsync(tab, saveAs: true);
            }

            return false;
        }

        tab.MarkSaved(path!, text);
        RecentFilesList.Add(_settings.RecentFiles, path!);
        UpdateTitle();
        UpdateStatus();
        SaveSettings();
        Log.Info("Сохранён файл " + path);
        return true;
    }

    internal static string DescribeWriteError(Exception ex)
    {
        const int ErrorDiskFull = unchecked((int)0x80070070);
        const int ErrorHandleDiskFull = unchecked((int)0x80070027);
        const int ErrorSharing = unchecked((int)0x80070020);
        const int ErrorLock = unchecked((int)0x80070021);
        return ex switch
        {
            UnauthorizedAccessException => "Нет прав на запись или файл только для чтения.",
            IOException io when io.HResult == ErrorDiskFull || io.HResult == ErrorHandleDiskFull => "Недостаточно места на диске.",
            IOException io when io.HResult == ErrorSharing || io.HResult == ErrorLock => "Файл занят другой программой.",
            DirectoryNotFoundException => "Папка не найдена.",
            _ => ex.Message,
        };
    }

    private string? AskSavePath(DocumentTab tab, string ext, string filter)
    {
        string baseName = tab.FilePath is null ? tab.Title : Path.GetFileNameWithoutExtension(tab.FilePath);
        var dlg = new SaveFileDialog
        {
            Title = ext == ".md" ? "Сохранить документ" : "Экспорт",
            Filter = filter,
            FileName = baseName + ext,
            DefaultExt = ext,
            AddExtension = true,
            OverwritePrompt = true,
        };
        string? dir = tab.FilePath is not null ? Path.GetDirectoryName(tab.FilePath) : _lastDialogDir;
        if (dir is not null && Directory.Exists(dir))
        {
            dlg.InitialDirectory = dir;
        }

        if (dlg.ShowDialog(this) != true)
        {
            return null;
        }

        _lastDialogDir = Path.GetDirectoryName(dlg.FileName);
        return dlg.FileName;
    }

    // ---------------- Закрытие ----------------

    private void OnCloseTab(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is { } t)
        {
            _ = CloseTabAsync(t);
        }
    }

    private void OnTabCloseButton(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DocumentTab tab })
        {
            e.Handled = true;
            _ = CloseTabAsync(tab);
        }
    }

    internal async Task<bool> CloseTabAsync(DocumentTab tab)
    {
        if (!await ConfirmSaveAsync(tab))
        {
            return false;
        }

        RemoveTab(tab);
        SaveSettings();
        return true;
    }

    /// <summary>Спрашивает о несохранённых изменениях. true — можно закрывать.</summary>
    private async Task<bool> ConfirmSaveAsync(DocumentTab tab)
    {
        await tab.GetTextAsync();
        tab.IsDirty = !string.Equals(tab.CurrentText, tab.SavedText, StringComparison.Ordinal);
        if (!tab.IsDirty)
        {
            return true;
        }

        TabStrip.SelectedItem = tab;
        var choice = SaveChangesDialog.Ask(this, tab.Title);
        return choice switch
        {
            SaveChoice.Save => await SaveTabAsync(tab, false),
            SaveChoice.DontSave => true,
            _ => false,
        };
    }

    private void RemoveTab(DocumentTab tab)
    {
        int index = _tabs.IndexOf(tab);
        bool wasActive = tab == ActiveTab;
        _tabs.Remove(tab);
        ContentHost.Children.Remove(tab.Web);
        tab.MessageReceived -= OnTabMessage;
        tab.ExternalChange -= OnExternalChange;
        tab.PropertyChanged -= OnTabPropertyChanged;
        tab.Dispose();
        if (wasActive && _tabs.Count > 0)
        {
            TabStrip.SelectedIndex = Math.Min(index, _tabs.Count - 1);
        }

        UpdateWelcome();
        UpdateMenus();
        UpdateStatus();
        UpdateTitle();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed)
        {
            _closing = true;
            return;
        }

        e.Cancel = true;
        if (_closing)
        {
            return;
        }

        _closing = true;
        try
        {
            foreach (var tab in _tabs.ToList())
            {
                if (!await ConfirmSaveAsync(tab))
                {
                    _closing = false;
                    return;
                }
            }

            SavePlacement();
            SaveSettings();
            _closeConfirmed = true;
            _automationServer?.Stop();
            await Dispatcher.InvokeAsync(Close);
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка при закрытии", ex);
            _closing = false;
        }
    }

    // ---------------- Внешние изменения файла ----------------

    private bool _askingExternal;

    private void OnExternalChange(DocumentTab tab)
    {
        if (tab.FilePath is null || _askingExternal || _closing)
        {
            return;
        }

        string path = tab.FilePath;
        bool exists = File.Exists(path);
        string? disk = exists ? DocumentFile.TryReadText(path, tab.Encoding) : null;
        var action = ExternalChangePolicy.Decide(exists, disk, tab.LastKnownDiskText, tab.IsDirty);
        Log.Info($"Внешнее изменение {path}: {action}");
        switch (action)
        {
            case ExternalChangeAction.Reload:
                try
                {
                    tab.ReplaceText(DocumentFile.Load(path, tab.EncodingName.StartsWith("UTF-8", StringComparison.Ordinal) ? null : tab.Encoding));
                    UpdateStatus();
                }
                catch (Exception ex)
                {
                    Log.Error("Не удалось перечитать файл", ex);
                }

                break;
            case ExternalChangeAction.Ask:
                _askingExternal = true;
                try
                {
                    TabStrip.SelectedItem = tab;
                    int r = ChoiceDialog.Show(this, $"Файл «{tab.Title}» изменён другой программой, а во вкладке есть несохранённые правки.",
                    [
                        new DialogButton("Загрузить с диска", "ReloadFromDiskButton"),
                        new DialogButton("Оставить мою версию", "KeepMineButton", IsDefault: true, IsCancel: true),
                    ]);
                    if (r == 0)
                    {
                        tab.ReplaceText(DocumentFile.Load(path, tab.EncodingName.StartsWith("UTF-8", StringComparison.Ordinal) ? null : tab.Encoding));
                    }
                    else
                    {
                        tab.LastKnownDiskText = disk ?? tab.LastKnownDiskText;
                    }

                    UpdateStatus();
                }
                catch (Exception ex)
                {
                    Log.Error("Ошибка при обработке внешнего изменения", ex);
                }
                finally
                {
                    _askingExternal = false;
                }

                break;
            case ExternalChangeAction.Deleted:
                if (!tab.DeletedNoticeShown)
                {
                    tab.DeletedNoticeShown = true;
                    tab.IsDirty = true;
                    StatusPath.Text = $"Файл удалён или перемещён: {path}";
                }

                break;
        }
    }

    // ---------------- Сообщения со страницы ----------------

    private void OnTabMessage(DocumentTab tab, JsonElement msg, CoreWebView2ObjectCollectionView? extra)
    {
        string type = msg.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        switch (type)
        {
            case "shortcut":
                HandleShortcut(msg.GetProperty("key").GetString() ?? string.Empty, fromPage: true);
                break;
            case "modeChanged":
                tab.Mode = ParseMode(msg.GetProperty("mode").GetString());
                if (tab == ActiveTab)
                {
                    UpdateMenus();
                }

                break;
            case "openExternal":
                string url = msg.GetProperty("url").GetString() ?? string.Empty;
                if (LinkTargets.IsSafeExternal(url))
                {
                    App.OpenUrl(url);
                }

                break;
            case "openLocal":
                OpenLocalLink(tab, msg.GetProperty("path").GetString() ?? string.Empty, msg.TryGetProperty("hash", out var h) ? h.GetString() : null);
                break;
            case "copy":
                CopyToClipboard(msg.GetProperty("text").GetString() ?? string.Empty);
                break;
            case "pasteImage":
                PasteImage(tab, msg.GetProperty("data").GetString() ?? string.Empty);
                break;
            case "pickImage":
                PickImage(tab);
                break;
            case "dropFiles":
                var files = new List<string>();
                if (extra is not null)
                {
                    foreach (object o in extra)
                    {
                        if (o is CoreWebView2File f && !string.IsNullOrEmpty(f.Path))
                        {
                            files.Add(f.Path);
                        }
                    }
                }

                OnFilesDropped(files);
                break;
            case "layout":
                if (msg.TryGetProperty("split", out var sp) && sp.ValueKind == JsonValueKind.Number)
                {
                    _settings.SplitRatio = Math.Clamp(sp.GetDouble(), 0.15, 0.85);
                }

                if (msg.TryGetProperty("tocWidth", out var tw) && tw.ValueKind == JsonValueKind.Number)
                {
                    _settings.TocWidth = Math.Clamp(tw.GetDouble(), 120, 600);
                }

                foreach (var other in _tabs.Where(x => x != tab))
                {
                    other.Post(new { type = "settings", settings = PageSettings() });
                }

                SaveSettings();
                break;
            case "textChanged":
                if (tab == ActiveTab)
                {
                    UpdateTitle();
                }

                break;
        }
    }

    private void OpenLocalLink(DocumentTab tab, string relPath, string? hash)
    {
        if (string.IsNullOrWhiteSpace(relPath))
        {
            return;
        }

        string full = LinkTargets.Resolve(tab.DocumentDirectory, relPath);
        switch (LinkTargets.Classify(full))
        {
            case LocalLinkKind.Markdown:
                _ = OpenLinkedAsync(full, hash);
                break;
            case LocalLinkKind.OtherFile:
                App.OpenUrl(full);
                break;
            default:
                ChoiceDialog.Info(this, $"Файл по ссылке не найден: «{relPath}».", full);
                break;
        }
    }

    private async Task OpenLinkedAsync(string full, string? hash)
    {
        await OpenFilesAsync([full], activate: true, quiet: false);
        if (!string.IsNullOrEmpty(hash) && FindTab(full) is { } t)
        {
            await Task.Delay(300);
            t.Post(new { type = "anchor", id = hash });
        }
    }

    private static void CopyToClipboard(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(50);
            }
        }
    }

    private bool EnsureSavedForImages(DocumentTab tab)
    {
        if (!tab.IsUntitled)
        {
            return true;
        }

        if (!ChoiceDialog.Confirm(this, "Чтобы вставить картинку, сначала сохраните документ: картинка будет сохранена в папку images рядом с ним.", "Сохранить…"))
        {
            return false;
        }

        // Синхронно ждать нельзя — сохранение спросит путь в диалоге.
        bool saved = false;
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = SaveTabAsync(tab, true).ContinueWith(r =>
        {
            saved = r.Status == TaskStatus.RanToCompletion && r.Result;
            frame.Continue = false;
        }, TaskScheduler.FromCurrentSynchronizationContext());
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        return saved;
    }

    private void PasteImage(DocumentTab tab, string base64)
    {
        if (string.IsNullOrEmpty(base64) || !EnsureSavedForImages(tab))
        {
            return;
        }

        try
        {
            byte[] data = Convert.FromBase64String(base64);
            var target = PastedImages.Allocate(tab.FilePath!, DateTime.Now);
            Directory.CreateDirectory(Path.GetDirectoryName(target.FullPath)!);
            using (var input = new MemoryStream(data))
            {
                var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(decoder.Frames[0]));
                using var fs = File.Create(target.FullPath);
                encoder.Save(fs);
            }

            tab.Post(new { type = "insert", text = target.MarkdownLink });
            Log.Info("Картинка из буфера сохранена: " + target.FullPath);
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось вставить картинку", ex);
            ChoiceDialog.Info(this, "Не удалось сохранить картинку из буфера обмена.", DescribeWriteError(ex));
        }
    }

    private void PickImage(DocumentTab tab)
    {
        if (!EnsureSavedForImages(tab))
        {
            return;
        }

        var dlg = new OpenFileDialog
        {
            Title = "Вставить картинку",
            Filter = "Картинки (*.png; *.jpg; *.gif; *.svg; *.webp)|*.png;*.jpg;*.jpeg;*.gif;*.svg;*.webp;*.bmp|Все файлы (*.*)|*.*",
            InitialDirectory = tab.DocumentDirectory,
        };
        if (dlg.ShowDialog(this) == true)
        {
            string rel = PastedImages.RelativePath(tab.FilePath!, dlg.FileName);
            tab.Post(new { type = "insert", text = PastedImages.MarkdownImage(rel, Path.GetFileNameWithoutExtension(dlg.FileName)) });
        }
    }

    // ---------------- Перетаскивание ----------------

    internal void OnFilesDropped(IReadOnlyList<string> files)
    {
        var list = files.Where(File.Exists).ToList();
        if (list.Count == 0)
        {
            return;
        }

        Activate();
        _ = OpenFilesAsync(list, activate: true, quiet: false);
    }

    private void OnWindowDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            DropOverlay.Visibility = Visibility.Visible;
        }
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            e.Handled = true;
            OnFilesDropped(files);
        }
    }

    private ListBoxItem? ItemFromEvent(object source) =>
        source is DependencyObject d ? ItemsControl.ContainerFromElement(TabStrip, d) as ListBoxItem : null;

    private void OnTabStripMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(TabStrip);
        _dragTab = ItemFromEvent(e.OriginalSource)?.DataContext as DocumentTab;
    }

    private void OnTabStripMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragTab is null)
        {
            return;
        }

        var p = e.GetPosition(TabStrip);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var tab = _dragTab;
        _dragTab = null;
        DragDrop.DoDragDrop(TabStrip, new DataObject(TabDragFormat, tab), DragDropEffects.Move);
    }

    private void OnTabStripMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && ItemFromEvent(e.OriginalSource)?.DataContext is DocumentTab tab)
        {
            e.Handled = true;
            _ = CloseTabAsync(tab);
        }

        _dragTab = null;
    }

    private void OnTabStripDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(TabDragFormat))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void OnTabStripDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(TabDragFormat) is not DocumentTab tab)
        {
            return;
        }

        e.Handled = true;
        int from = _tabs.IndexOf(tab);
        int to = ItemFromEvent(e.OriginalSource)?.DataContext is DocumentTab target ? _tabs.IndexOf(target) : _tabs.Count - 1;
        MoveTab(from, to);
    }

    internal void MoveTab(int from, int to)
    {
        if (from < 0 || to < 0 || from >= _tabs.Count || to >= _tabs.Count || from == to)
        {
            return;
        }

        var active = ActiveTab;
        _tabs.Move(from, to);
        TabStrip.SelectedItem = active;
        SaveSettings();
    }
}
