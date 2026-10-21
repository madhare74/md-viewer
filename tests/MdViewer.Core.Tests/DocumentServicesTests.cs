using MdViewer.Core.Documents;

namespace MdViewer.Core.Tests;

public class DocumentServicesTests
{
    [Fact]
    public void ExternalChange_NoEdits_Reloads()
    {
        Assert.Equal(ExternalChangeAction.Reload, ExternalChangePolicy.Decide(true, "новый", "старый", isDirty: false));
    }

    [Fact]
    public void ExternalChange_WithEdits_Asks()
    {
        Assert.Equal(ExternalChangeAction.Ask, ExternalChangePolicy.Decide(true, "новый", "старый", isDirty: true));
    }

    [Fact]
    public void ExternalChange_SameContent_IsIgnored()
    {
        // Например, наше собственное сохранение или изменение только переводов строк.
        Assert.Equal(ExternalChangeAction.Ignore, ExternalChangePolicy.Decide(true, "a\r\nb", "a\nb", isDirty: true));
    }

    [Fact]
    public void ExternalChange_Deleted()
    {
        Assert.Equal(ExternalChangeAction.Deleted, ExternalChangePolicy.Decide(false, null, "x", isDirty: false));
    }

    [Fact]
    public void ExternalChange_Unreadable_IsIgnoredForNow()
    {
        Assert.Equal(ExternalChangeAction.Ignore, ExternalChangePolicy.Decide(true, null, "x", isDirty: false));
    }

    [Fact]
    public void ExternalChange_RealFileScenario()
    {
        using var tmp = new TempDir();
        string path = tmp.File("doc.md");
        DocumentFile.Save(path, "# Версия 1\n", LineEnding.CrLf);
        var doc = DocumentFile.Load(path);
        File.WriteAllText(path, "# Версия 2\r\n");
        string? disk = DocumentFile.TryReadText(path);
        Assert.Equal(ExternalChangeAction.Reload, ExternalChangePolicy.Decide(File.Exists(path), disk, doc.Text, false));
        Assert.Equal(ExternalChangeAction.Ask, ExternalChangePolicy.Decide(File.Exists(path), disk, doc.Text, true));
    }

    [Fact]
    public void PastedImage_NameAndPath()
    {
        var now = new DateTime(2026, 10, 21, 14, 5, 9);
        string doc = Path.Combine(Path.GetTempPath(), "docs", "note.md");
        var t = PastedImages.Allocate(doc, now, _ => false);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "docs", "images", "image-20261021-140509.png"), t.FullPath);
        Assert.Equal("images/image-20261021-140509.png", t.RelativePath);
        Assert.Equal("![картинка](images/image-20261021-140509.png)", t.MarkdownLink);
    }

    [Fact]
    public void PastedImage_ExistingName_GetsSuffix()
    {
        var now = new DateTime(2026, 10, 21, 14, 5, 9);
        string doc = Path.Combine(Path.GetTempPath(), "docs", "note.md");
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(Path.GetTempPath(), "docs", "images", "image-20261021-140509.png"),
            Path.Combine(Path.GetTempPath(), "docs", "images", "image-20261021-140509-2.png"),
        };
        var t = PastedImages.Allocate(doc, now, taken.Contains);
        Assert.Equal("images/image-20261021-140509-3.png", t.RelativePath);
    }

    [Fact]
    public void ImageRelativePath_UsesForwardSlashes_AndBracketsForSpaces()
    {
        string doc = Path.Combine(Path.GetTempPath(), "d", "note.md");
        string img = Path.Combine(Path.GetTempPath(), "d", "pics", "my pic.png");
        string rel = PastedImages.RelativePath(doc, img);
        Assert.Equal("pics/my pic.png", rel);
        Assert.Equal("![x](<pics/my pic.png>)", PastedImages.MarkdownImage(rel, "x"));
    }

    [Fact]
    public void LocalLinks_AreResolvedAndClassified()
    {
        string dir = Samples.Dir;
        string full = LinkTargets.Resolve(dir, "docs/other.md#part");
        Assert.Equal(Path.Combine(dir, "docs", "other.md"), full);
        Assert.Equal(LocalLinkKind.Markdown, LinkTargets.Classify(full));
        Assert.Equal(LocalLinkKind.OtherFile, LinkTargets.Classify(Path.Combine(dir, "images", "pic.png")));
        Assert.Equal(LocalLinkKind.Missing, LinkTargets.Classify(Path.Combine(dir, "нет.md")));
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com", true)]
    [InlineData("mailto:a@b.c", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows/system32/calc.exe", false)]
    public void ExternalLinks_Safety(string url, bool safe)
    {
        Assert.Equal(safe, LinkTargets.IsSafeExternal(url));
    }

    [Fact]
    public void CommandLine_ParsesFiles()
    {
        string cwd = Path.GetTempPath();
        var files = CommandLine.ParseFiles(new[] { "--automation", "a.md", Path.Combine(cwd, "b.md"), "\"a.md\"" }, cwd);
        Assert.Equal(2, files.Count);
        Assert.Equal(Path.GetFullPath(Path.Combine(cwd, "a.md")), files[0]);
    }
}
