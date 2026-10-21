using MdViewer.Core.Markdown;

namespace MdViewer.Core.Tests;

public class TocTests
{
    private readonly MarkdownRenderer _r = new();

    [Fact]
    public void Levels_AndLines_AreCollected()
    {
        var toc = _r.Render(Samples.Read("headings.md")).Toc;
        Assert.Equal(new[] { 1, 2, 2, 3, 4, 5, 6, 1 }, toc.Select(t => t.Level).ToArray());
        Assert.Equal("Введение", toc[0].Text);
        Assert.Equal(0, toc[0].Line);
        Assert.Equal("Setext заголовок", toc[^1].Text);
    }

    [Fact]
    public void DuplicateHeadings_GetUniqueIds()
    {
        var toc = _r.Render(Samples.Read("headings.md")).Toc;
        Assert.Equal("Установка", toc[1].Text);
        Assert.Equal("Установка", toc[2].Text);
        Assert.NotEqual(toc[1].Id, toc[2].Id);
        Assert.Equal("установка", toc[1].Id);
        Assert.Equal("установка-1", toc[2].Id);
    }

    [Fact]
    public void FormattedHeading_HasPlainText()
    {
        var toc = _r.Render(Samples.Read("headings.md")).Toc;
        Assert.Equal("Шаг первый и код", toc[3].Text);
    }

    [Fact]
    public void HeadingsInQuotes_AreSkipped()
    {
        var toc = _r.Render(Samples.Read("headings.md")).Toc;
        Assert.DoesNotContain(toc, t => t.Text == "Заголовок в цитате");
    }

    [Fact]
    public void HeadingIds_MatchHtml()
    {
        var res = _r.Render("# Привет, мир!\n\n## Второй **раздел**\n");
        foreach (var t in res.Toc)
        {
            Assert.Contains($"id=\"{t.Id}\"", res.Html);
        }

        Assert.Equal("привет-мир", res.Toc[0].Id);
    }

    [Fact]
    public void EmptyDocument_HasEmptyToc()
    {
        Assert.Empty(_r.Render("просто текст").Toc);
    }
}
