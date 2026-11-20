using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.Tables;
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
    private static readonly FontFamily CodeFont = new("Consolas");

    public static void Render(Panel target, string markdown)
    {
        target.Children.Clear();
        RenderBlocks(target, Markdown.Parse(markdown, Pipeline));
    }

    private static void RenderBlocks(Panel target, ContainerBlock blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    var title = Text(heading.Inline);
                    title.FontSize = Math.Max(14, 23 - heading.Level * 2);
                    title.FontWeight = FontWeights.SemiBold;
                    target.Children.Add(title);
                    break;
                case ParagraphBlock paragraph:
                    target.Children.Add(Text(paragraph.Inline));
                    break;
                case CodeBlock code:
                    var output = new TextBox
                    {
                        Text = code.Lines.ToString(), IsReadOnly = true, FontFamily = CodeFont,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, TextWrapping = TextWrapping.Wrap,
                        Background = CodeBackground
                    };
                    target.Children.Add(output);
                    break;
                case QuoteBlock quote:
                    var quoted = new StackPanel();
                    RenderBlocks(quoted, quote);
                    target.Children.Add(new Border { Child = quoted, BorderBrush = BorderColor, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 0, 0, 0) });
                    break;
                case ListBlock list:
                    RenderList(target, list);
                    break;
                case MarkdownTable table:
                    target.Children.Add(RenderTable(table));
                    break;
                case ThematicBreakBlock:
                    target.Children.Add(new Border { Height = 1, Background = BorderColor, Margin = new Thickness(0, 7, 0, 7) });
                    break;
                case ContainerBlock nested:
                    RenderBlocks(target, nested);
                    break;
            }
        }
    }

    private static void RenderList(Panel target, ListBlock list)
    {
        var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var row = new DockPanel { Margin = new Thickness(12, 0, 0, 0) };
            var marker = new TextBlock { Text = list.IsOrdered ? $"{number++}." : "•", Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(marker, Dock.Left);
            row.Children.Add(marker);
            var body = new StackPanel();
            RenderBlocks(body, item);
            row.Children.Add(body);
            target.Children.Add(row);
        }
    }

    private static Grid RenderTable(MarkdownTable table)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 8) };
        foreach (var row in table.OfType<MarkdownTableRow>())
        {
            var index = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var column = 0;
            foreach (var cell in row.OfType<MarkdownTableCell>())
            {
                if (column >= grid.ColumnDefinitions.Count) grid.ColumnDefinitions.Add(new ColumnDefinition());
                var content = new StackPanel();
                if (row.IsHeader) TextElement.SetFontWeight(content, FontWeights.SemiBold);
                RenderBlocks(content, cell);
                var border = new Border { Child = content, BorderBrush = BorderColor, BorderThickness = new Thickness(0.5), Padding = new Thickness(6, 2, 6, 2) };
                Grid.SetRow(border, index);
                Grid.SetColumn(border, column++);
                grid.Children.Add(border);
            }
        }
        return grid;
    }

    private static TextBlock Text(ContainerInline? content)
    {
        var text = new TextBlock { Margin = new Thickness(0, 1, 0, 6), TextWrapping = TextWrapping.Wrap };
        if (content?.FirstChild is LiteralInline literal && literal.NextSibling is null)
            text.Text = literal.Content.ToString();
        else if (content is not null)
            RenderInlines(text.Inlines, content);
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
