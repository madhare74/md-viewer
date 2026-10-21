using MdViewer.Core.Settings;

namespace MdViewer.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void Settings_RoundTrip()
    {
        using var tmp = new TempDir();
        var store = new SettingsStore(tmp.Path);
        var s = new AppSettings
        {
            Theme = ThemeSetting.Dark,
            EditorFontFamily = "Cascadia Mono",
            EditorFontSize = 16,
            PreviewZoom = 1.3,
            FullWidth = true,
            WordWrap = false,
            DefaultMode = ViewMode.Split,
            TocVisible = false,
            TocWidth = 300,
            SplitRatio = 0.4,
            ActiveTab = 1,
            Window = new WindowPlacement { Left = 10, Top = 20, Width = 1000, Height = 700, Maximized = true },
        };
        s.RecentFiles.Add(@"C:\docs\a.md");
        s.OpenTabs.Add(@"C:\docs\b.md");
        store.Save(s);

        var loaded = new SettingsStore(tmp.Path).Load();
        Assert.Equal(ThemeSetting.Dark, loaded.Theme);
        Assert.Equal("Cascadia Mono", loaded.EditorFontFamily);
        Assert.Equal(16, loaded.EditorFontSize);
        Assert.Equal(1.3, loaded.PreviewZoom);
        Assert.True(loaded.FullWidth);
        Assert.False(loaded.WordWrap);
        Assert.Equal(ViewMode.Split, loaded.DefaultMode);
        Assert.False(loaded.TocVisible);
        Assert.Equal(300, loaded.TocWidth);
        Assert.Equal(0.4, loaded.SplitRatio);
        Assert.Equal(new[] { @"C:\docs\a.md" }, loaded.RecentFiles);
        Assert.Equal(new[] { @"C:\docs\b.md" }, loaded.OpenTabs);
        Assert.True(loaded.Window!.Maximized);
        Assert.Contains("\"Dark\"", File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void MissingFile_GivesDefaults()
    {
        using var tmp = new TempDir();
        var s = new SettingsStore(tmp.Path).Load();
        Assert.Equal(ThemeSetting.System, s.Theme);
        Assert.Equal(ViewMode.View, s.DefaultMode);
        Assert.Equal(1.0, s.PreviewZoom);
        Assert.True(s.WordWrap);
    }

    [Fact]
    public void CorruptFile_GivesDefaults_AndKeepsBackup()
    {
        using var tmp = new TempDir();
        var store = new SettingsStore(tmp.Path);
        File.WriteAllText(store.FilePath, "{ это не json");
        var s = store.Load();
        Assert.Equal(ThemeSetting.System, s.Theme);
        Assert.True(File.Exists(store.FilePath + ".bad"));
    }

    [Fact]
    public void OutOfRangeValues_AreClamped()
    {
        using var tmp = new TempDir();
        var store = new SettingsStore(tmp.Path);
        File.WriteAllText(store.FilePath, "{\"PreviewZoom\": 50, \"EditorFontSize\": 1, \"SplitRatio\": 2, \"EditorFontFamily\": \"\"}");
        var s = store.Load();
        Assert.Equal(AppSettings.MaxZoom, s.PreviewZoom);
        Assert.Equal(8, s.EditorFontSize);
        Assert.Equal(0.85, s.SplitRatio);
        Assert.Equal("Consolas", s.EditorFontFamily);
    }

    [Fact]
    public void RecentFiles_KeepsTenMostRecent_WithoutDuplicates()
    {
        var list = new List<string>();
        for (int i = 0; i < 12; i++)
        {
            RecentFilesList.Add(list, Path.Combine(Path.GetTempPath(), $"f{i}.md"));
        }

        Assert.Equal(10, list.Count);
        Assert.EndsWith("f11.md", list[0]);
        RecentFilesList.Add(list, Path.Combine(Path.GetTempPath(), "f5.md"));
        Assert.Equal(10, list.Count);
        Assert.EndsWith("f5.md", list[0]);
        Assert.Single(list, p => p.EndsWith("f5.md", StringComparison.Ordinal));
        RecentFilesList.Remove(list, list[0]);
        Assert.Equal(9, list.Count);
    }

    [Fact]
    public void SessionTabs_RestoreOnlyExistingSavedFiles()
    {
        var captured = SessionTabs.Capture(new string?[] { @"C:\a.md", null, @"C:\b.md", @"c:\A.md" });
        Assert.Equal(new[] { @"C:\a.md", @"C:\b.md" }, captured);
        var restored = SessionTabs.Restore(captured, p => p.EndsWith("b.md", StringComparison.Ordinal));
        Assert.Equal(new[] { @"C:\b.md" }, restored);
    }

    [Theory]
    [InlineData(1.0, 1, 1.1)]
    [InlineData(1.0, -1, 0.9)]
    [InlineData(3.0, 1, 3.0)]
    [InlineData(0.5, -1, 0.5)]
    public void Zoom_Steps(double current, int dir, double expected)
    {
        Assert.Equal(expected, Zoom.Step(current, dir), 3);
    }
}
