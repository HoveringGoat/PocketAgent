using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownTable = Markdig.Extensions.Tables.Table;
using MarkdownTableRow = Markdig.Extensions.Tables.TableRow;
using MarkdownTableCell = Markdig.Extensions.Tables.TableCell;

namespace PocketAgent.Desktop;

internal static class TranscriptMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().DisableHtml().Build();
    private static readonly Brush CodeBackground = MakeBrush("#25382C");
    private static readonly Brush BorderColor = MakeBrush("#47604B");
    private static readonly Brush TranscriptColor = MakeBrush("#C1DAA6");
    private static readonly FontFamily CodeFont = new("Consolas");

    public static void Render(Panel target, string markdown)
    {
        var document = new FlowDocument { PagePadding = new Thickness(0), Foreground = TranscriptColor };
        RenderBlocks(document.Blocks, Markdown.Parse(markdown, Pipeline));
        if (target.Children.Count > 0 && target.Children[0] is RichTextBox current)
        {
            var previous = current.Document.ContentStart;
            var start = previous.GetOffsetToPosition(current.Selection.Start);
            var end = previous.GetOffsetToPosition(current.Selection.End);
            current.Document = document;
            if (start != end)
                current.Selection.Select(document.ContentStart.GetPositionAtOffset(start) ?? document.ContentEnd,
                    document.ContentStart.GetPositionAtOffset(end) ?? document.ContentEnd);
            return;
        }
        var viewer = new RichTextBox(document)
        {
            IsReadOnly = true,
            IsDocumentEnabled = true,
            Background = Brushes.Transparent,
            Foreground = TranscriptColor,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        PassWheelToTranscript(viewer);
        target.Children.Add(viewer);
    }

    public static TextBox ReadOnlyText(string text, Brush? foreground = null)
    {
        var viewer = new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            Foreground = foreground ?? TranscriptColor, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(0)
        };
        PassWheelToTranscript(viewer);
        return viewer;
    }

    private static void PassWheelToTranscript(Control viewer)
    {
        viewer.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            viewer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = Mouse.MouseWheelEvent });
        };
    }

    private static void RenderBlocks(BlockCollection target, ContainerBlock blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    var title = Text(heading.Inline);
                    title.FontSize = Math.Max(14, 23 - heading.Level * 2);
                    title.FontWeight = FontWeights.SemiBold;
                    target.Add(title);
                    break;
                case ParagraphBlock paragraph:
                    target.Add(Text(paragraph.Inline));
                    break;
                case CodeBlock code:
                    var output = new Paragraph(new Run(code.Lines.ToString()))
                    {
                        FontFamily = CodeFont, Background = CodeBackground, Padding = new Thickness(8),
                        Margin = new Thickness(0, 2, 0, 8)
                    };
                    target.Add(output);
                    break;
                case QuoteBlock quote:
                    var quoted = new Section { BorderBrush = BorderColor, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 0, 0, 0) };
                    RenderBlocks(quoted.Blocks, quote);
                    target.Add(quoted);
                    break;
                case ListBlock list:
                    RenderList(target, list);
                    break;
                case MarkdownTable table:
                    target.Add(RenderTable(table));
                    break;
                case ThematicBreakBlock:
                    target.Add(new Paragraph { FontSize = 1, BorderBrush = BorderColor, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 7, 0, 7) });
                    break;
                case ContainerBlock nested:
                    RenderBlocks(target, nested);
                    break;
            }
        }
    }

    private static void RenderList(BlockCollection target, ListBlock list)
    {
        var rendered = new List
        {
            MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            StartIndex = int.TryParse(list.OrderedStart, out var start) ? Math.Max(1, start) : 1,
            Margin = new Thickness(16, 0, 0, 6)
        };
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var entry = new ListItem();
            RenderBlocks(entry.Blocks, item);
            rendered.ListItems.Add(entry);
        }
        target.Add(rendered);
    }

    private static Table RenderTable(MarkdownTable table)
    {
        var grid = new Table { Margin = new Thickness(0, 2, 0, 8), CellSpacing = 0 };
        var rows = new TableRowGroup();
        grid.RowGroups.Add(rows);
        foreach (var row in table.OfType<MarkdownTableRow>())
        {
            var rendered = new TableRow();
            if (row.IsHeader) rendered.FontWeight = FontWeights.SemiBold;
            foreach (var cell in row.OfType<MarkdownTableCell>())
            {
                if (rendered.Cells.Count >= grid.Columns.Count) grid.Columns.Add(new TableColumn());
                var content = new TableCell { BorderBrush = BorderColor, BorderThickness = new Thickness(0.5), Padding = new Thickness(6, 2, 6, 2) };
                RenderBlocks(content.Blocks, cell);
                rendered.Cells.Add(content);
            }
            rows.Rows.Add(rendered);
        }
        return grid;
    }

    private static Paragraph Text(ContainerInline? content)
    {
        var text = new Paragraph { Margin = new Thickness(0, 1, 0, 6) };
        if (content is not null) RenderInlines(text.Inlines, content);
        return text;
    }

    private static void RenderInlines(InlineCollection target, ContainerInline content)
    {
        foreach (var inline in content)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;
                case HtmlEntityInline entity:
                    target.Add(new Run(entity.Transcoded.ToString()));
                    break;
                case LineBreakInline line:
                    if (line.IsHard) target.Add(new LineBreak());
                    else target.Add(new Run(" "));
                    break;
                case CodeInline code:
                    target.Add(new Run(code.Content) { FontFamily = CodeFont, Background = CodeBackground });
                    break;
                case EmphasisInline emphasis:
                    Span style = emphasis.DelimiterCount == 2 ? new Bold() : new Italic();
                    RenderInlines(style.Inlines, emphasis);
                    target.Add(style);
                    break;
                case LinkInline link when !link.IsImage && SafeWebLink(link.Url) is { } uri:
                    var hyperlink = WebLink(uri);
                    RenderInlines(hyperlink.Inlines, link);
                    target.Add(hyperlink);
                    break;
                case AutolinkInline auto when SafeWebLink(auto.Url) is { } uri:
                    var autolink = WebLink(uri);
                    autolink.Inlines.Add(new Run(auto.Url));
                    target.Add(autolink);
                    break;
                case AutolinkInline auto:
                    target.Add(new Run(auto.Url));
                    break;
                case LinkInline { IsImage: true }:
                    // The transcript image row supplies the caption and preview.
                    break;
                case ContainerInline nested:
                    RenderInlines(target, nested);
                    break;
            }
        }
    }

    private static Uri? SafeWebLink(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri : null;

    private static Hyperlink WebLink(Uri uri)
    {
        var link = new Hyperlink { NavigateUri = uri, Foreground = Brushes.LightGreen, ToolTip = uri.AbsoluteUri };
        link.RequestNavigate += (_, args) =>
        {
            args.Handled = true;
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { link.ToolTip = "Windows could not open this link."; }
        };
        return link;
    }

    private static Brush MakeBrush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}
