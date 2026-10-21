using System.Text;
using Markdig.Extensions.Mathematics;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MdViewer.Core.Markdown;

/// <summary>Пункт оглавления: уровень 1–6, текст без разметки, якорь, строка исходника (с 0).</summary>
public sealed record TocEntry(int Level, string Text, string Id, int Line);

public static class TocBuilder
{
    public static IReadOnlyList<TocEntry> Build(MarkdownDocument doc)
    {
        var list = new List<TocEntry>();
        foreach (var h in doc.Descendants<HeadingBlock>())
        {
            if (IsInside<QuoteBlock>(h) || IsInside<Markdig.Extensions.Footnotes.Footnote>(h))
            {
                continue;
            }

            string text = PlainText(h.Inline).Trim();
            string id = h.TryGetAttributes()?.Id ?? string.Empty;
            list.Add(new TocEntry(Math.Clamp(h.Level, 1, 6), text, id, h.Line));
        }

        return list;
    }

    public static string PlainText(ContainerInline? container)
    {
        if (container is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        Append(sb, container);
        return CollapseSpaces(sb.ToString());
    }

    private static void Append(StringBuilder sb, Inline inline)
    {
        switch (inline)
        {
            case LiteralInline lit:
                sb.Append(lit.Content.ToString());
                break;
            case CodeInline code:
                sb.Append(code.Content);
                break;
            case MathInline math:
                sb.Append(math.Content.ToString());
                break;
            case HtmlEntityInline ent:
                sb.Append(ent.Transcoded.ToString());
                break;
            case AutolinkInline auto:
                sb.Append(auto.Url);
                break;
            case LineBreakInline:
                sb.Append(' ');
                break;
            case HtmlInline:
                break;
            case ContainerInline c:
                foreach (var child in c)
                {
                    Append(sb, child);
                }

                break;
        }
    }

    private static bool IsInside<T>(Block b)
        where T : Block
    {
        for (var p = b.Parent; p is not null; p = p.Parent)
        {
            if (p is T)
            {
                return true;
            }
        }

        return false;
    }

    private static string CollapseSpaces(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!space)
                {
                    sb.Append(' ');
                }

                space = true;
            }
            else
            {
                sb.Append(c);
                space = false;
            }
        }

        return sb.ToString();
    }
}
