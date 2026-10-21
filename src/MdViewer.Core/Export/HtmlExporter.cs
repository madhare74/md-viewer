using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MdViewer.Core.Export;

/// <summary>Параметры экспорта в самодостаточный HTML.</summary>
public sealed class HtmlExportOptions
{
    public string Title { get; init; } = "Документ";

    /// <summary>Готовая разметка просмотра (формулы KaTeX и схемы SVG уже отрисованы).</summary>
    public string BodyHtml { get; init; } = string.Empty;

    /// <summary>Стили (оформление GitHub, подсветка, KaTeX со встроенными шрифтами).</summary>
    public string Css { get; init; } = string.Empty;

    /// <summary>Папка документа — для картинок по относительным путям.</summary>
    public string? DocumentDirectory { get; init; }

    /// <summary>Загрузка картинок из интернета (null — не загружать, оставить адрес).</summary>
    public Func<string, (byte[] Data, string? Mime)?>? DownloadRemote { get; init; }
}

/// <summary>
/// Экспорт в один HTML-файл, который открывается в любом браузере без интернета:
/// стили встроены, картинки встроены как data:, скриптов нет.
/// </summary>
public static partial class HtmlExporter
{
    public const string DocHost = "https://doc.mdviewer.example/";
    public const string AppHost = "https://app.mdviewer.example/";

    /// <summary>Дополнительные стили страницы экспорта и печати.</summary>
    public const string PageCss = """
        html { background: #ffffff; }
        body { margin: 0; background: #ffffff; }
        .markdown-body { box-sizing: border-box; min-width: 200px; max-width: 980px; margin: 0 auto; padding: 45px; }
        @media (max-width: 767px) { .markdown-body { padding: 15px; } }
        .markdown-body .mermaid { text-align: center; margin: 0 0 16px; overflow-x: auto; }
        .markdown-body pre.mermaid { background: transparent; padding: 0; white-space: normal; font-family: inherit; }
        .markdown-body .mermaid svg { max-width: 100%; height: auto; }
        .markdown-body div.math { overflow-x: auto; margin: 0 0 16px; }
        .markdown-body pre code.hljs { padding: 0; background: transparent; }
        .mdv-error { display: block; white-space: pre-wrap; font: 13px Consolas, monospace; padding: 8px 12px; border-radius: 6px; color: #82071e; background: #ffebe9; border: 1px solid #ff818266; }
        @media print {
          .markdown-body { max-width: none; padding: 0; }
          .markdown-body pre, .markdown-body tr, .markdown-body blockquote, .markdown-body img, .markdown-body .mermaid, .markdown-body .katex-display { break-inside: avoid; }
          .markdown-body h1, .markdown-body h2, .markdown-body h3, .markdown-body h4 { break-after: avoid; }
        }
        """;

    [GeneratedRegex(@"(<img\b[^>]*?\bsrc\s*=\s*)(""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ImgSrcRegex();

    [GeneratedRegex(@"<script\b.*?</script\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex(@"\s(?:srcset|loading)\s*=\s*(""[^""]*""|'[^']*')", RegexOptions.IgnoreCase)]
    private static partial Regex SrcsetRegex();

    [GeneratedRegex(@"src\s*:\s*([^;}]*)", RegexOptions.IgnoreCase)]
    private static partial Regex FontSrcRegex();

    [GeneratedRegex(@"url\(\s*['""]?([^'"")]+)['""]?\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex CssUrlRegex();

    public static string Build(HtmlExportOptions o)
    {
        string body = ScriptRegex().Replace(o.BodyHtml ?? string.Empty, string.Empty);
        body = EmbedImages(body, o.DocumentDirectory, o.DownloadRemote);
        var sb = new StringBuilder(body.Length + o.Css.Length + 2048);
        sb.Append("<!DOCTYPE html>\n<html lang=\"ru\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<meta name=\"generator\" content=\"MD Viewer\">\n");
        sb.Append("<title>").Append(WebUtility.HtmlEncode(o.Title)).Append("</title>\n");
        sb.Append("<style>\n").Append(o.Css).Append('\n').Append(PageCss).Append("\n</style>\n");
        sb.Append("</head>\n<body>\n<article class=\"markdown-body\">\n");
        sb.Append(body);
        sb.Append("\n</article>\n</body>\n</html>\n");
        return sb.ToString();
    }

    /// <summary>Заменяет адреса картинок на встроенные data:-адреса.</summary>
    public static string EmbedImages(string html, string? documentDirectory, Func<string, (byte[] Data, string? Mime)?>? download)
    {
        html = SrcsetRegex().Replace(html, string.Empty);
        return ImgSrcRegex().Replace(html, m =>
        {
            string raw = m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
            string src = WebUtility.HtmlDecode(raw).Trim();
            string? dataUri = ToDataUri(src, documentDirectory, download);
            return dataUri is null ? m.Value : m.Groups[1].Value + "\"" + dataUri + "\"";
        });
    }

    internal static string? ToDataUri(string src, string? documentDirectory, Func<string, (byte[] Data, string? Mime)?>? download)
    {
        if (src.Length == 0 || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? localPath = null;
        if (src.StartsWith(DocHost, StringComparison.OrdinalIgnoreCase))
        {
            localPath = Uri.UnescapeDataString(StripQuery(src[DocHost.Length..]));
        }
        else if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (download is null)
            {
                return null;
            }

            try
            {
                var r = download(src);
                if (r is null || r.Value.Data.Length == 0)
                {
                    return null;
                }

                string mime = r.Value.Mime ?? MimeFromExtension(new Uri(src).AbsolutePath) ?? "application/octet-stream";
                return $"data:{mime};base64,{Convert.ToBase64String(r.Value.Data)}";
            }
            catch
            {
                return null;
            }
        }
        else if (src.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(src, UriKind.Absolute, out var fileUri))
        {
            localPath = fileUri.LocalPath;
        }
        else if (!src.Contains("://", StringComparison.Ordinal))
        {
            localPath = Uri.UnescapeDataString(StripQuery(src));
        }

        if (localPath is null)
        {
            return null;
        }

        try
        {
            string full = Path.IsPathRooted(localPath) ? localPath : Path.Combine(documentDirectory ?? Directory.GetCurrentDirectory(), localPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                return null;
            }

            string mime = MimeFromExtension(full) ?? "application/octet-stream";
            return $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(full))}";
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Встраивает шрифты в CSS (url(fonts/...) → data:). Ссылки на отсутствующие файлы убираются.</summary>
    public static string EmbedFonts(string css, string baseDirectory)
    {
        return FontSrcRegex().Replace(css, m =>
        {
            var parts = SplitTopLevel(m.Groups[1].Value);
            var kept = new List<string>();
            foreach (string part in parts)
            {
                var u = CssUrlRegex().Match(part);
                if (!u.Success)
                {
                    kept.Add(part.Trim());
                    continue;
                }

                string url = u.Groups[1].Value;
                if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    kept.Add(part.Trim());
                    continue;
                }

                string file = Path.Combine(baseDirectory, StripQuery(url).Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(file))
                {
                    continue;
                }

                string mime = MimeFromExtension(file) ?? "application/octet-stream";
                string data = $"url(data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(file))})";
                kept.Add(part.Trim().Replace(u.Value, data, StringComparison.Ordinal));
            }

            return kept.Count == 0 ? "src:local(\"Arial\")" : "src:" + string.Join(",", kept);
        });
    }

    public static string? MimeFromExtension(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" or ".jfif" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".ico" => "image/x-icon",
            ".avif" => "image/avif",
            ".woff2" => "font/woff2",
            ".woff" => "font/woff",
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",
            _ => null,
        };
    }

    private static string StripQuery(string s)
    {
        int i = s.IndexOfAny(['?', '#']);
        return i >= 0 ? s[..i] : s;
    }

    private static List<string> SplitTopLevel(string s)
    {
        var list = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                list.Add(s[start..i]);
                start = i + 1;
            }
        }

        list.Add(s[start..]);
        return list;
    }
}
