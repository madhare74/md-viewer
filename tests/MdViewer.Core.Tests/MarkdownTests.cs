using MdViewer.Core.Markdown;

namespace MdViewer.Core.Tests;

public class MarkdownTests
{
    private readonly MarkdownRenderer _r = new();

    private string Html(string md) => _r.Render(md).Html;

    [Fact]
    public void AllFeaturesSample_RendersEveryElement()
    {
        var res = _r.Render(Samples.Read("all-features.md"));
        string h = res.Html;
        Assert.Contains("<h1 id=\"образец-документа-md-viewer\"", h);
        Assert.Contains("<strong>жирным</strong>", h);
        Assert.Contains("<em>курсивом</em>", h);
        Assert.Contains("<del>зачёркнутым</del>", h);
        Assert.Contains("<code>кодом в строке</code>", h);
        Assert.Contains("<a href=\"https://github.com\">https://github.com</a>", h);
        Assert.Contains("<blockquote", h);
        Assert.Contains("<hr", h);
        Assert.Contains("<ul", h);
        Assert.Contains("<ol", h);
        Assert.Contains("type=\"checkbox\"", h);
        Assert.Contains("checked=\"checked\"", h);
        Assert.Contains("<table", h);
        Assert.Contains("text-align: center", h);
        Assert.Contains("text-align: right", h);
        Assert.Contains("class=\"language-python\"", h);
        Assert.Contains("class=\"language-csharp\"", h);
        Assert.Contains("class=\"math\"", h);
        Assert.Contains("class=\"language-mermaid\"", h);
        Assert.Contains("<img src=\"images/pic.png\"", h);
        Assert.Contains("class=\"footnotes\"", h);
        Assert.Contains("<details>", h);
        Assert.Contains("😄", h);
        Assert.Contains("🚀", h);
        Assert.True(res.HasMath);
        Assert.True(res.HasMermaid);
    }

    [Fact]
    public void Blocks_HaveSourceLineNumbers()
    {
        string h = Html("# A\n\ntext\n\n- one\n- two\n");
        Assert.Contains("<h1 id=\"a\" data-line=\"0\">", h);
        Assert.Contains("data-line=\"2\"", h);
        Assert.Contains("data-line=\"4\"", h);
        Assert.Contains("data-line=\"5\"", h);
    }

    [Fact]
    public void InlineAndBlockMath_AreMarkedForKatex()
    {
        string h = Html("Формула $a^2$.\n\n$$\nb\n$$\n");
        Assert.Contains("<span class=\"math\">", h);
        Assert.Contains("<div class=\"math\"", h);
    }

    [Fact]
    public void MermaidBlock_IsKeptAsTextForThePage()
    {
        string h = Html("```mermaid\ngraph TD\nA-->B\n```\n");
        Assert.Contains("class=\"language-mermaid\"", h);
        Assert.Contains("A--&gt;B", h);
    }

    [Fact]
    public void MermaidError_DoesNotBreakDocument()
    {
        var res = _r.Render(Samples.Read("mermaid-error.md"));
        Assert.Contains("class=\"language-mermaid\"", res.Html);
        Assert.Contains("Документ отображается дальше.", res.Html);
        Assert.Contains("id=\"конец\"", res.Html);
    }

    [Fact]
    public void Scripts_AreRemoved()
    {
        string h = Html(Samples.Read("scripts.md"));
        Assert.DoesNotContain("<script", h, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("взломано", h);
        Assert.DoesNotContain("onerror", h, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onload", h, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", h, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", h, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", h, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<img src=\"images/pic.png\"", h);
        Assert.Contains("style=\"color: red\"", h);
        Assert.Contains("Красный текст", h);
        Assert.Contains("Обычный текст после.", h);
    }

    [Theory]
    [InlineData("<b onmouseover=\"x()\">t</b>", "<b>t</b>")]
    [InlineData("<script src=x></script>after", "after")]
    [InlineData("<a href=\"JaVaScRiPt:alert(1)\">x</a>", "<a>x</a>")]
    [InlineData("<a href=\"https://example.com\" title='t'>x</a>", "<a href=\"https://example.com\" title=\"t\">x</a>")]
    [InlineData("<STYLE>body{display:none}</STYLE>ok", "ok")]
    [InlineData("<input type=\"text\" value=\"x\">", "")]
    [InlineData("<input type=\"checkbox\" checked>", "<input type=\"checkbox\" checked disabled>")]
    [InlineData("<img src=\"data:image/png;base64,AAAA\">", "<img src=\"data:image/png;base64,AAAA\">")]
    [InlineData("<img src=\"data:text/html,<b>\">", "<img>")]
    [InlineData("<!-- комментарий -->", "<!-- комментарий -->")]
    [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=x\">", "")]
    public void RawHtmlFilter_Cases(string input, string expected)
    {
        Assert.Equal(expected, RawHtmlFilter.Filter(input));
    }

    [Theory]
    [InlineData("javascript:alert(1)", false, "#")]
    [InlineData(" java\nscript:alert(1)", false, "#")]
    [InlineData("data:text/html;base64,AA", false, "#")]
    [InlineData("data:image/png;base64,AA", true, "data:image/png;base64,AA")]
    [InlineData("https://example.com", false, "https://example.com")]
    [InlineData("other.md#x", false, "other.md#x")]
    public void UrlFilter_Cases(string url, bool image, string expected)
    {
        Assert.Equal(expected, UrlFilter.SafeUrl(url, image));
    }

    [Fact]
    public void LargeDocument_RendersQuickly()
    {
        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (sb.Length < 10 * 1024 * 1024)
        {
            i++;
            sb.Append("## Раздел ").Append(i).Append("\n\nТекст **раздела** с [ссылкой](https://example.com) и `кодом`. Пример текста для проверки скорости разбора.\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n```js\nconsole.log(").Append(i).Append(");\n```\n\n");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = _r.Render(sb.ToString());
        sw.Stop();
        Assert.Equal(i, res.Toc.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"Разбор 10 МБ занял {sw.Elapsed}");
    }
}
