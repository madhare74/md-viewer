using System.Globalization;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Renderers.Html.Inlines;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MdViewer.Core.Markdown;

/// <summary>Результат преобразования Markdown в HTML.</summary>
public sealed record RenderResult(string Html, IReadOnlyList<TocEntry> Toc, bool HasMermaid, bool HasMath);

/// <summary>
/// Markdown → HTML в стиле GitHub (GFM): таблицы, списки задач, зачёркивание, сноски, автоссылки,
/// эмодзи по кодам, формулы (KaTeX рисует на странице), схемы Mermaid (рисуются на странице).
/// HTML внутри документа пропускается через фильтр: скрипты и обработчики событий удаляются.
/// У блоков проставляется data-line — номер строки исходника для синхронной прокрутки.
/// </summary>
public sealed class MarkdownRenderer
{
    private readonly MarkdownPipeline _pipeline;

    public MarkdownRenderer()
    {
        _pipeline = new MarkdownPipelineBuilder()
            .UseAlertBlocks()
            .UsePipeTables()
            .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
            .UseTaskLists()
            .UseAutoLinks()
            .UseFootnotes()
            .UseEmojiAndSmiley(false)
            .UseMathematics()
            .UseDiagrams()
            .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
            .Build();
    }

    public MarkdownPipeline Pipeline => _pipeline;

    public MarkdownDocument Parse(string markdown) => Markdig.Markdown.Parse(markdown ?? string.Empty, _pipeline);

    public RenderResult Render(string markdown)
    {
        var doc = Parse(markdown);
        AddLineAttributes(doc, 0);
        bool hasMath = false;
        bool hasMermaid = false;
        foreach (var node in doc.Descendants())
        {
            switch (node)
            {
                case LinkInline link:
                    link.Url = UrlFilter.SafeUrl(link.Url, link.IsImage);
                    break;
                case Markdig.Extensions.Mathematics.MathInline:
                case Markdig.Extensions.Mathematics.MathBlock:
                    hasMath = true;
                    break;
                case FencedCodeBlock fenced when string.Equals(fenced.Info, "mermaid", StringComparison.OrdinalIgnoreCase):
                    hasMermaid = true;
                    break;
            }
        }

        var toc = TocBuilder.Build(doc);
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var renderer = new HtmlRenderer(writer);
        _pipeline.Setup(renderer);
        renderer.ObjectRenderers.ReplaceOrAdd<HtmlBlockRenderer>(new SafeHtmlBlockRenderer());
        renderer.ObjectRenderers.ReplaceOrAdd<HtmlInlineRenderer>(new SafeHtmlInlineRenderer());
        renderer.Render(doc);
        writer.Flush();
        return new RenderResult(writer.ToString(), toc, hasMermaid, hasMath);
    }

    private static void AddLineAttributes(ContainerBlock container, int depth)
    {
        foreach (var block in container)
        {
            if (block is BlankLineBlock || block is LinkReferenceDefinitionGroup || block is HtmlBlock)
            {
                continue;
            }

            block.GetAttributes().AddPropertyIfNotExist("data-line", block.Line.ToString(CultureInfo.InvariantCulture));
            if (depth < 4 && block is ContainerBlock inner && (block is ListBlock || block is ListItemBlock || block is QuoteBlock))
            {
                AddLineAttributes(inner, depth + 1);
            }
        }
    }

    private sealed class SafeHtmlBlockRenderer : HtmlObjectRenderer<HtmlBlock>
    {
        protected override void Write(HtmlRenderer renderer, HtmlBlock obj)
        {
            if (!renderer.EnableHtmlForBlock)
            {
                renderer.WriteLeafRawLines(obj, true, true);
                return;
            }

            string raw = obj.Lines.ToString();
            renderer.Write(RawHtmlFilter.Filter(raw));
            renderer.WriteLine();
        }
    }

    private sealed class SafeHtmlInlineRenderer : HtmlObjectRenderer<HtmlInline>
    {
        protected override void Write(HtmlRenderer renderer, HtmlInline obj)
        {
            if (renderer.EnableHtmlForInline)
            {
                renderer.Write(RawHtmlFilter.Filter(obj.Tag));
            }
        }
    }
}
