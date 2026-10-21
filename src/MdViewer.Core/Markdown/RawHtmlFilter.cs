using System.Text;
using System.Text.RegularExpressions;

namespace MdViewer.Core.Markdown;

/// <summary>Проверка адресов ссылок и картинок.</summary>
public static class UrlFilter
{
    /// <summary>Возвращает адрес, если он безопасен; иначе «#». javascript:, vbscript:, data: (кроме картинок) запрещены.</summary>
    public static string SafeUrl(string? url, bool isImage)
    {
        if (string.IsNullOrEmpty(url))
        {
            return url ?? string.Empty;
        }

        string compact = new string(url.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray()).ToLowerInvariant();
        if (compact.StartsWith("javascript:", StringComparison.Ordinal) || compact.StartsWith("vbscript:", StringComparison.Ordinal))
        {
            return "#";
        }

        if (compact.StartsWith("data:", StringComparison.Ordinal))
        {
            bool image = compact.StartsWith("data:image/", StringComparison.Ordinal) && !compact.StartsWith("data:image/svg", StringComparison.Ordinal);
            return isImage && image ? url : "#";
        }

        return url;
    }
}

/// <summary>
/// Фильтр HTML, встроенного в Markdown. HTML показывается, но всё, что может выполнить код,
/// удаляется: script, iframe, object, embed, style, обработчики on*, javascript:-адреса.
/// Это дополнительная защита: на странице просмотра скрипты документа ещё и запрещены политикой CSP.
/// </summary>
public static partial class RawHtmlFilter
{
    // Теги, удаляемые вместе с содержимым.
    private static readonly HashSet<string> DropWithContent = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "iframe", "object", "embed", "applet", "frame", "frameset", "noscript", "template", "noembed", "xmp", "title", "textarea", "select",
    };

    // Теги, удаляемые без содержимого.
    private static readonly HashSet<string> DropTag = new(StringComparer.OrdinalIgnoreCase)
    {
        "link", "meta", "base", "form", "button", "param", "source", "track", "portal", "html", "head", "body",
    };

    private static readonly HashSet<string> UrlAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "href", "src", "xlink:href", "action", "formaction", "data", "poster", "background", "srcset", "lowsrc", "dynsrc", "cite", "longdesc", "usemap",
    };

    [GeneratedRegex(@"<!--.*?-->|<(/?)\s*([a-zA-Z][a-zA-Z0-9:-]*)((?:[^>""']|""[^""]*""|'[^']*')*)>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"([^\s""'>/=]+)(?:\s*=\s*(""[^""]*""|'[^']*'|[^\s""'=<>`]+))?", RegexOptions.Singleline)]
    private static partial Regex AttrRegex();

    public static string Filter(string html)
    {
        if (string.IsNullOrEmpty(html) || html.IndexOf('<') < 0)
        {
            return html ?? string.Empty;
        }

        var sb = new StringBuilder(html.Length);
        int pos = 0;
        string? skipUntil = null;
        foreach (Match m in TagRegex().Matches(html))
        {
            if (m.Index < pos)
            {
                continue;
            }

            if (skipUntil is null)
            {
                sb.Append(html, pos, m.Index - pos);
            }

            pos = m.Index + m.Length;

            if (m.Value.StartsWith("<!--", StringComparison.Ordinal))
            {
                if (skipUntil is null)
                {
                    sb.Append(m.Value);
                }

                continue;
            }

            bool closing = m.Groups[1].Value == "/";
            string name = m.Groups[2].Value;

            if (skipUntil is not null)
            {
                if (closing && string.Equals(name, skipUntil, StringComparison.OrdinalIgnoreCase))
                {
                    skipUntil = null;
                }

                continue;
            }

            if (DropWithContent.Contains(name))
            {
                bool selfClosing = m.Groups[3].Value.TrimEnd().EndsWith('/');
                if (!closing && !selfClosing)
                {
                    skipUntil = name;
                }

                continue;
            }

            if (DropTag.Contains(name))
            {
                continue;
            }

            if (string.Equals(name, "input", StringComparison.OrdinalIgnoreCase) && !closing)
            {
                // Разрешены только галочки (списки задач), всегда неактивные.
                if (!Regex.IsMatch(m.Groups[3].Value, @"type\s*=\s*[""']?checkbox", RegexOptions.IgnoreCase))
                {
                    continue;
                }
            }

            if (closing)
            {
                sb.Append("</").Append(name).Append('>');
                continue;
            }

            sb.Append('<').Append(name);
            string attrs = m.Groups[3].Value;
            bool selfClose = attrs.TrimEnd().EndsWith('/');
            foreach (Match a in AttrRegex().Matches(attrs))
            {
                string an = a.Groups[1].Value;
                if (an == "/" || an.Length == 0)
                {
                    continue;
                }

                if (an.StartsWith("on", StringComparison.OrdinalIgnoreCase) || string.Equals(an, "srcdoc", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(an, "formaction", StringComparison.OrdinalIgnoreCase) || string.Equals(an, "http-equiv", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string rawValue = a.Groups[2].Success ? a.Groups[2].Value : string.Empty;
                string value = rawValue.Length >= 2 && (rawValue[0] == '"' || rawValue[0] == '\'') ? rawValue[1..^1] : rawValue;

                if (UrlAttributes.Contains(an))
                {
                    string decoded = System.Net.WebUtility.HtmlDecode(value);
                    bool isImg = string.Equals(name, "img", StringComparison.OrdinalIgnoreCase) && string.Equals(an, "src", StringComparison.OrdinalIgnoreCase);
                    if (UrlFilter.SafeUrl(decoded, isImg) == "#" && decoded != "#")
                    {
                        continue;
                    }
                }

                if (string.Equals(an, "style", StringComparison.OrdinalIgnoreCase))
                {
                    string lower = System.Net.WebUtility.HtmlDecode(value).ToLowerInvariant();
                    if (lower.Contains("expression(") || lower.Contains("javascript:") || lower.Contains("url(") || lower.Contains("position:fixed") || lower.Contains("position: fixed"))
                    {
                        continue;
                    }
                }

                sb.Append(' ').Append(an);
                if (a.Groups[2].Success)
                {
                    sb.Append("=\"").Append(value.Replace("\"", "&quot;")).Append('"');
                }
            }

            if (string.Equals(name, "input", StringComparison.OrdinalIgnoreCase) && !attrs.Contains("disabled", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(" disabled");
            }

            sb.Append(selfClose ? " />" : ">");
        }

        if (skipUntil is null && pos < html.Length)
        {
            sb.Append(html, pos, html.Length - pos);
        }

        return sb.ToString();
    }
}
