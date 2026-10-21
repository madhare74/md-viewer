using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Xunit;
using Xunit.Abstractions;

namespace MdViewer.UiTests;

[Collection("ui")]
public class UiTests
{
    private readonly ITestOutputHelper _out;

    public UiTests(ITestOutputHelper output)
    {
        _out = output;
    }

    private static int TabCount(AppSession s) => s.State().GetProperty("tabs").GetArrayLength();

    [Fact]
    public void OpenFromCommandLine_ShowsFormattedDocument()
    {
        string dir = AppSession.CopySamples();
        using var s = AppSession.Start(nameof(OpenFromCommandLine_ShowsFormattedDocument), [Path.Combine(dir, "all-features.md")]);
        Assert.Equal(1, TabCount(s));
        s.WaitUntil(() => s.TabNames().Contains("all-features.md"), 10, "вкладка в списке вкладок (UIA)");
        Assert.Contains("all-features.md", s.State().GetProperty("title").GetString());

        var st = s.PageState();
        _out.WriteLine(st.GetRawText());
        Assert.Equal("view", st.GetProperty("mode").GetString());
        Assert.True(st.GetProperty("highlighted").GetInt32() >= 4, "подсветка кода");
        Assert.True(st.GetProperty("mathOk").GetInt32() >= 3, "формулы KaTeX");
        Assert.Equal(0, st.GetProperty("mathErrors").GetInt32());
        Assert.Equal(2, st.GetProperty("mermaidOk").GetInt32());
        Assert.Equal(0, st.GetProperty("mermaidErrors").GetInt32());
        Assert.True(st.GetProperty("tocItems").GetInt32() >= 8);

        string text = s.PreviewText();
        Assert.Contains("Образец документа MD Viewer", text);
        Assert.Contains("Сделано", text);
        Assert.Equal("true", s.Eval("document.querySelector('#preview img').complete && document.querySelector('#preview img').naturalWidth > 0"));
        Assert.Equal("true", s.Eval("!!document.querySelector('#preview table td[style*=right]')"));
        s.Screenshot("01-open-view.png");
    }

    [Fact]
    public void DroppedFiles_OpenAsTabs()
    {
        string dir = AppSession.CopySamples();
        using var s = AppSession.Start(nameof(DroppedFiles_OpenAsTabs));
        Assert.True(s.State().GetProperty("welcomeVisible").GetBoolean());
        s.Screenshot("02-welcome.png");
        s.Call(new { cmd = "drop", paths = new[] { Path.Combine(dir, "links.md"), Path.Combine(dir, "headings.md") } });
        s.WaitUntil(() => TabCount(s) == 2, 30, "две вкладки после перетаскивания");
        Assert.False(s.State().GetProperty("welcomeVisible").GetBoolean());
        Assert.Equal(new[] { "links.md", "headings.md" }, s.TabNames());
    }

    [Fact]
    public void SecondLaunch_OpensTabInSameWindow()
    {
        string dir = AppSession.CopySamples();
        using var s = AppSession.Start(nameof(SecondLaunch_OpensTabInSameWindow), [Path.Combine(dir, "links.md")]);
        var psi = new ProcessStartInfo(AppSession.ExePath) { UseShellExecute = false };
        psi.ArgumentList.Add(Path.Combine(dir, "headings.md"));
        psi.Environment["MDVIEWER_DATA_DIR"] = s.DataDir;
        psi.Environment["MDVIEWER_INSTANCE_ID"] = s.InstanceId;
        using var second = Process.Start(psi)!;
        Assert.True(second.WaitForExit(20000), "второй экземпляр должен завершиться");
        s.WaitUntil(() => TabCount(s) == 2, 30, "вкладка из второго запуска");
        Assert.Contains("headings.md", s.TabNames());
    }

    [Fact]
    public void CloseWindow_AsksToSave_CancelAndDontSave()
    {
        string dir = AppSession.CopySamples();
        string file = Path.Combine(dir, "links.md");
        string original = File.ReadAllText(file);
        using var s = AppSession.Start(nameof(CloseWindow_AsksToSave_CancelAndDontSave), [file, Path.Combine(dir, "headings.md")]);
        s.Call(new { cmd = "select", index = 0 });
        s.Call(new { cmd = "setText", text = "# Изменено\n" });
        s.WaitUntil(() => s.State().GetProperty("tabs")[0].GetProperty("dirty").GetBoolean(), 10, "признак несохранённых изменений");
        s.WaitUntil(() => s.TabNames().Contains("● links.md"), 10, "точка у имени вкладки");

        s.Window.Close();
        var dlg = s.WaitDialog();
        s.Call(new { cmd = "screenshotWindow", path = Path.Combine(AppSession.OutDir, "screenshots", "03-save-dialog.png"), title = "MD Viewer" });
        s.ClickInDialog(dlg, "CancelButton");
        Thread.Sleep(1000);
        Assert.False(s.HasExited, "после «Отмена» окно остаётся открытым");

        s.Window.Close();
        dlg = s.WaitDialog();
        s.ClickInDialog(dlg, "DontSaveButton");
        s.WaitUntil(() => s.HasExited, 20, "закрытие приложения");
        Assert.Equal(original, File.ReadAllText(file));
    }

    [Fact]
    public void CloseTab_SaveFromDialog_WritesFile()
    {
        string dir = AppSession.CopySamples();
        string file = Path.Combine(dir, "lf.md");
        using var s = AppSession.Start(nameof(CloseTab_SaveFromDialog_WritesFile), [file]);
        s.Call(new { cmd = "setText", text = "# LF\n\nновая строка\n" });
        s.Call(new { cmd = "closeTab" });
        var dlg = s.WaitDialog();
        s.ClickInDialog(dlg, "SaveButton");
        s.WaitUntil(() => TabCount(s) == 0, 15, "вкладка закрыта");
        Assert.Equal("# LF\n\nновая строка\n", File.ReadAllText(file));
    }

    [Fact]
    public void Modes_EditingUpdatesPreview_AndKeyboardWorks()
    {
        string dir = AppSession.CopySamples();
        string file = Path.Combine(dir, "lf.md");
        using var s = AppSession.Start(nameof(Modes_EditingUpdatesPreview_AndKeyboardWorks), [file]);
        s.Call(new { cmd = "mode", mode = "split" });
        Assert.Equal("split", s.PageState().GetProperty("mode").GetString());

        var sw = Stopwatch.StartNew();
        s.Call(new { cmd = "setText", text = "# Новый заголовок\n\nФормула $x^2$ и **жирный**.\n\n## Второй\n" });
        sw.Stop();
        _out.WriteLine($"Обновление просмотра: {sw.ElapsedMilliseconds} мс");
        Assert.Contains("Новый заголовок", s.PreviewText());
        var st = s.PageState();
        Assert.Equal(2, st.GetProperty("tocItems").GetInt32());
        Assert.Equal(1, st.GetProperty("mathOk").GetInt32());

        // Настоящий ввод с клавиатуры в редактор и Ctrl+S
        s.Eval("mdv.focusEditor(); mdv.scrollEditorToLine(999); true");
        s.Window.Focus();
        s.Eval("mdv.cursorToEnd(); true");
        Thread.Sleep(300);
        Keyboard.Type(VirtualKeyShort.RETURN);
        Keyboard.Type("Печать с клавиатуры");
        s.WaitUntil(() => s.PreviewText().Contains("Печать с клавиатуры"), 10, "просмотр обновился после ввода");
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_S);
        s.WaitUntil(() => File.ReadAllText(file).Contains("Печать с клавиатуры"), 10, "сохранение по Ctrl+S");
        Assert.False(s.State().GetProperty("tabs")[0].GetProperty("dirty").GetBoolean());

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_3);
        s.WaitUntil(() => s.PageState().GetProperty("mode").GetString() == "edit", 5, "режим «Правка» по Ctrl+3");
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_1);
        s.WaitUntil(() => s.PageState().GetProperty("mode").GetString() == "view", 5, "режим «Просмотр» по Ctrl+1");
        Assert.Equal("view", s.State().GetProperty("tabs")[0].GetProperty("mode").GetString());
    }

    [Fact]
    public void Toc_ClickScrollsToHeading()
    {
        string dir = AppSession.CopySamples();
        string file = Path.Combine(dir, "long.md");
        var sb = new StringBuilder("# Длинный документ\n\n");
        for (int i = 1; i <= 40; i++)
        {
            sb.Append("## Раздел ").Append(i).Append("\n\n");
            for (int j = 0; j < 8; j++)
            {
                sb.Append("Абзац текста раздела ").Append(i).Append(". Lorem ipsum dolor sit amet, consectetur adipiscing elit.\n\n");
            }
        }

        File.WriteAllText(file, sb.ToString());
        using var s = AppSession.Start(nameof(Toc_ClickScrollsToHeading), [file]);
        Assert.Equal(41, s.PageState().GetProperty("tocItems").GetInt32());
        s.Eval("document.querySelector('.toc-item[data-index=\"30\"]').click(); true");
        s.WaitUntil(() => s.PageState().GetProperty("previewScroll").GetDouble() > 1000, 5, "прокрутка к разделу");
        s.WaitUntil(() => s.PageState().GetProperty("tocCurrent").GetInt32() == 30, 5, "подсветка текущего раздела");
        Assert.Equal("true", s.Eval("(() => { const h = document.getElementById('раздел-30'); const r = h.getBoundingClientRect(); return r.top >= 0 && r.top < 80; })()"));
        s.Screenshot("04-toc-navigation.png");

        // В режиме правки оглавление прокручивает и редактор
        s.Call(new { cmd = "mode", mode = "split" });
        s.Eval("document.querySelector('.toc-item[data-index=\"10\"]').click(); true");
        Thread.Sleep(500);
        Assert.True(s.PageState().GetProperty("editorScroll").GetDouble() > 100);

        // Скрыть / показать оглавление
        s.Call(new { cmd = "shortcut", key = "Ctrl+Shift+O" });
        s.WaitUntil(() => s.Eval("getComputedStyle(document.getElementById('toc')).display") == "none", 5, "оглавление скрыто");
        s.Call(new { cmd = "shortcut", key = "Ctrl+Shift+O" });
        s.WaitUntil(() => s.Eval("getComputedStyle(document.getElementById('toc')).display") != "none", 5, "оглавление показано");
    }

    [Fact]
    public void Export_PdfAndHtml()
    {
        string dir = AppSession.CopySamples();
        using var s = AppSession.Start(nameof(Export_PdfAndHtml), [Path.Combine(dir, "all-features.md")], theme: "Dark");
        string outDir = Path.Combine(AppSession.OutDir, "export");
        Directory.CreateDirectory(outDir);
        string pdf = Path.Combine(outDir, "all-features.pdf");
        string html = Path.Combine(outDir, "all-features.html");

        s.Call(new { cmd = "export", kind = "pdf", path = pdf }, 120);
        var bytes = File.ReadAllBytes(pdf);
        Assert.True(bytes.Length > 20_000, "PDF не пустой");
        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
        string pdfText = Encoding.Latin1.GetString(bytes);
        var media = Regex.Match(pdfText, @"/MediaBox\s*\[[^\]]*\]");
        _out.WriteLine("PDF: " + bytes.Length + " байт, " + (media.Success ? media.Value : "MediaBox не найден в открытом виде") + ", ссылки: " + pdfText.Contains("/URI"));

        s.Call(new { cmd = "export", kind = "html", path = html }, 120);
        string h = File.ReadAllText(html);
        Assert.StartsWith("<!DOCTYPE html>", h);
        Assert.DoesNotContain("<script", h, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", h, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<svg", h);
        Assert.Contains("class=\"katex\"", h);
        Assert.Contains("data:image/png;base64,", h);
        Assert.Contains("data:font/woff2;base64,", h);
        Assert.DoesNotMatch(new Regex(@"\b(?:src|href)\s*=\s*""https?://(?:app|doc)\.mdviewer"), h);
        Assert.DoesNotMatch(new Regex(@"<(?:img|source|video|audio)\b[^>]*\bsrc\s*=\s*""https?://"), h);

        // После экспорта тёмная тема возвращается
        s.WaitUntil(() => s.PageState().GetProperty("theme").GetString() == "dark", 5, "тема после экспорта");
        s.Screenshot("05-dark-after-export.png");
    }

    [Fact]
    public void Scripts_InDocument_AreNotExecuted()
    {
        string dir = AppSession.CopySamples();
        using var s = AppSession.Start(nameof(Scripts_InDocument_AreNotExecuted), [Path.Combine(dir, "scripts.md")]);
        Assert.Equal("MD Viewer", s.Eval("document.title"));
        Assert.Equal("0", s.Eval("document.querySelectorAll('#preview script, #preview iframe').length"));
        Assert.Equal("0", s.Eval("document.querySelectorAll('#preview [onclick], #preview [onerror], #preview [onload]').length"));
        Assert.Contains("Обычный текст после.", s.PreviewText());
    }

    [Fact]
    public void Encoding_Cp1251_IsDetected()
    {
        string dir = AppSession.CopySamples();
        using var s = AppSession.Start(nameof(Encoding_Cp1251_IsDetected), [Path.Combine(dir, "cp1251.md")]);
        Assert.Equal("Windows-1251", s.State().GetProperty("tabs")[0].GetProperty("encoding").GetString());
        Assert.Contains("Текст на русском языке.", s.PreviewText());
    }

    [Fact]
    public void MermaidError_ShowsMessage_DocumentStillShown()
    {
        string dir = AppSession.CopySamples();
        using var s = AppSession.Start(nameof(MermaidError_ShowsMessage_DocumentStillShown), [Path.Combine(dir, "mermaid-error.md")]);
        var st = s.PageState();
        Assert.Equal(1, st.GetProperty("mermaidErrors").GetInt32());
        Assert.Equal(1, st.GetProperty("mathErrors").GetInt32());
        string text = s.PreviewText();
        Assert.Contains("Ошибка в схеме", text);
        Assert.Contains("Документ отображается дальше.", text);
        Assert.Contains("Конец", text);
        s.Screenshot("06-mermaid-error.png");
    }

    [Fact]
    public void ExternalChange_ReloadsOrAsks()
    {
        string dir = AppSession.CopySamples();
        string file = Path.Combine(dir, "lf.md");
        using var s = AppSession.Start(nameof(ExternalChange_ReloadsOrAsks), [file]);
        File.WriteAllText(file, "# Изменено снаружи\n");
        s.WaitUntil(() => s.PreviewText().Contains("Изменено снаружи"), 15, "автообновление без правок");
        Assert.False(s.State().GetProperty("tabs")[0].GetProperty("dirty").GetBoolean());

        s.Call(new { cmd = "setText", text = "# Мои правки\n" });
        File.WriteAllText(file, "# Вторая внешняя правка\n");
        var dlg = s.WaitDialog();
        s.ClickInDialog(dlg, "ReloadFromDiskButton");
        s.WaitUntil(() => s.PreviewText().Contains("Вторая внешняя правка"), 15, "загрузка с диска");
        Assert.Equal("# Вторая внешняя правка\n", s.Eval("mdv.getText()"));
    }

    [Fact]
    public void Links_OpenOtherMarkdownInNewTab()
    {
        string dir = AppSession.CopySamples();
        using var s = AppSession.Start(nameof(Links_OpenOtherMarkdownInNewTab), [Path.Combine(dir, "links.md")]);
        s.Eval("document.querySelector('#preview a[href=\"docs/other.md\"]').click(); true");
        s.WaitUntil(() => TabCount(s) == 2, 20, "вкладка по ссылке");
        Assert.Contains("other.md", s.TabNames());
    }

    [Fact]
    public void Tabs_RestoredOnNextLaunch_AndReorder()
    {
        string dir = AppSession.CopySamples();
        string data = Path.Combine(Path.GetTempPath(), "mdv-data-" + Guid.NewGuid().ToString("N")[..8]);
        using (var s = AppSession.Start(nameof(Tabs_RestoredOnNextLaunch_AndReorder) + "-1", [Path.Combine(dir, "links.md"), Path.Combine(dir, "headings.md")], dataDir: data))
        {
            s.Call(new { cmd = "moveTab", from = 1, to = 0 });
            Assert.Equal(new[] { "headings.md", "links.md" }, s.TabNames());
            s.Call(new { cmd = "new" });
            Assert.Equal(3, TabCount(s));
            s.Call(new { cmd = "closeWindow" });
            s.WaitUntil(() => s.HasExited, 20, "закрытие");
        }

        using var s2 = AppSession.Start(nameof(Tabs_RestoredOnNextLaunch_AndReorder) + "-2", dataDir: data, noRestore: false);
        s2.WaitUntil(() => TabCount(s2) == 2, 30, "восстановленные вкладки");
        Assert.Equal(new[] { "headings.md", "links.md" }, s2.TabNames());
        Assert.Contains("links.md", s2.State().GetProperty("recent").EnumerateArray().Select(x => Path.GetFileName(x.GetString()!)));
    }

    [Fact]
    public void PasteImage_SavesPngNextToDocument()
    {
        string dir = AppSession.CopySamples();
        string file = Path.Combine(dir, "lf.md");
        using var s = AppSession.Start(nameof(PasteImage_SavesPngNextToDocument), [file]);
        s.Call(new { cmd = "mode", mode = "split" });
        var t = new Thread(() =>
        {
            var bmp = System.Windows.Media.Imaging.BitmapSource.Create(4, 4, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, new byte[4 * 4 * 4], 16);
            System.Windows.Clipboard.SetImage(bmp);
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        s.Window.Focus();
        s.Eval("mdv.focusEditor(); true");
        Thread.Sleep(300);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
        s.WaitUntil(() => Directory.Exists(Path.Combine(dir, "images")) && Directory.GetFiles(Path.Combine(dir, "images"), "image-*.png").Length == 1, 15, "PNG в папке images");
        s.WaitUntil(() => s.Eval("mdv.getText()").Contains("](images/image-"), 10, "ссылка на картинку в тексте");
    }

    [Theory]
    [InlineData("light", 1.0)]
    [InlineData("light", 1.5)]
    [InlineData("light", 2.0)]
    [InlineData("dark", 1.0)]
    [InlineData("dark", 1.5)]
    [InlineData("dark", 2.0)]
    public void Screenshots_ThemesAndScales(string theme, double scale)
    {
        string dir = AppSession.CopySamples();
        string tag = $"{theme}-{(int)(scale * 100)}";
        using var s = AppSession.Start(nameof(Screenshots_ThemesAndScales) + "-" + tag, [Path.Combine(dir, "all-features.md"), Path.Combine(dir, "headings.md")], uiScale: scale, theme: theme == "dark" ? "Dark" : "Light");
        // Физический размер окна одинаковый — при масштабе 150/200 % на интерфейс остаётся меньше места, как на настоящем экране.
        s.Call(new { cmd = "resize", left = 0, top = 0, width = 1600, height = 1000 });
        s.Call(new { cmd = "select", index = 0 });
        s.WaitRendered();
        Assert.Equal(theme, s.PageState().GetProperty("theme").GetString());
        s.Screenshot($"10-{tag}-view.png");
        s.Call(new { cmd = "mode", mode = "split" });
        s.Eval("mdv.scrollEditorToLine(40); true");
        s.Screenshot($"11-{tag}-split.png");
        s.Call(new { cmd = "mode", mode = "edit" });
        s.Screenshot($"12-{tag}-edit.png");
        s.Call(new { cmd = "mode", mode = "view" });
        s.Eval("document.getElementById('preview-pane').scrollTop = 99999; true");
        s.Screenshot($"13-{tag}-view-bottom.png");

        s.Call(new { cmd = "showSettings" });
        s.WaitDialog("SettingsWindow");
        s.Call(new { cmd = "screenshotWindow", path = Path.Combine(AppSession.OutDir, "screenshots", $"14-{tag}-settings.png"), title = "Настройки" });
        s.ClickInDialog(s.WaitDialog("SettingsWindow"), "SettingsCancel");

        // Кнопки панели инструментов и подписи видимы целиком
        foreach (string id in new[] { "ModeView", "ModeSplit", "ModeEdit", "TocToggle", "SettingsButton" })
        {
            var el = s.Window.FindFirstDescendant(cf => cf.ByAutomationId(id));
            Assert.NotNull(el);
            Assert.False(el!.IsOffscreen, id + " за пределами окна");
            var r = el.BoundingRectangle;
            var w = s.Window.BoundingRectangle;
            Assert.True(r.Right <= w.Right + 1 && r.Left >= w.Left - 1, id + " обрезана по ширине");
        }
    }
}

[CollectionDefinition("ui", DisableParallelization = true)]
public class UiCollection
{
}
