using MdViewer.Core.Export;

namespace MdViewer.Core.Tests;

public class ExportTests
{
    [Fact]
    public void Html_IsSelfContained()
    {
        string body = "<h1>Т</h1><p><img src=\"images/pic.png\" alt=\"a\"></p><p><img src=\"https://doc.mdviewer.example/images/pic.png\"></p><script>alert(1)</script>";
        string html = HtmlExporter.Build(new HtmlExportOptions
        {
            Title = "Тест <1>",
            BodyHtml = body,
            Css = ".markdown-body{color:#000}",
            DocumentDirectory = Samples.Dir,
        });
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("<meta charset=\"utf-8\">", html);
        Assert.Contains("<title>Тест &lt;1&gt;</title>", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("images/pic.png", html);
        Assert.Equal(2, CountOf(html, "src=\"data:image/png;base64,"));
        Assert.DoesNotContain("<link", html);
        Assert.Contains(".markdown-body{color:#000}", html);
    }

    [Fact]
    public void RemoteImages_AreDownloaded_OrKept()
    {
        string body = "<img src=\"https://example.com/a.png\"><img src=\"https://example.com/b.png\">";
        string html = HtmlExporter.EmbedImages(body, null, url => url.EndsWith("a.png", StringComparison.Ordinal) ? (new byte[] { 1, 2, 3 }, "image/png") : null);
        Assert.Contains("data:image/png;base64,AQID", html);
        Assert.Contains("https://example.com/b.png", html);
    }

    [Fact]
    public void MissingLocalImage_IsKept()
    {
        string html = HtmlExporter.EmbedImages("<img src=\"нет.png\">", Samples.Dir, null);
        Assert.Contains("нет.png", html);
    }

    [Fact]
    public void Fonts_AreEmbedded_AndMissingOnesDropped()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "fonts"));
        File.WriteAllBytes(Path.Combine(tmp.Path, "fonts", "A.woff2"), new byte[] { 1, 2, 3 });
        string css = "@font-face{font-family:A;src:url(fonts/A.woff2) format(\"woff2\"),url(fonts/A.woff) format(\"woff\"),url(fonts/A.ttf) format(\"truetype\")}.x{color:red}";
        string res = HtmlExporter.EmbedFonts(css, tmp.Path);
        Assert.Contains("src:url(data:font/woff2;base64,AQID) format(\"woff2\")}", res);
        Assert.DoesNotContain("A.woff)", res);
        Assert.DoesNotContain("A.ttf", res);
        Assert.Contains(".x{color:red}", res);
    }

    private static int CountOf(string s, string sub)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0)
        {
            n++;
            i += sub.Length;
        }

        return n;
    }
}
