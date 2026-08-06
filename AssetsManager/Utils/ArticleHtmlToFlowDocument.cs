using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AssetsManager.Utils
{
    /// <summary>
    /// Converts the raw HTML article body (from Riot's CMS) into a native WPF FlowDocument.
    /// Supports paragraphs, headings, lists, blockquotes, figures, images, links and basic
    /// inline formatting. Images are downloaded asynchronously through the shared HttpClient.
    /// </summary>
    public static class ArticleHtmlToFlowDocument
    {
        private static readonly Regex TokenRegex = new("<[^>]+>|[^<]+", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex AttrRegex = new("([a-zA-Z_:][-a-zA-Z0-9_:.]*)\\s*=\\s*(\"[^\"]*\"|'[^']*'|[^\\s>]+)|([a-zA-Z_:][-a-zA-Z0-9_:.]*)", RegexOptions.Compiled);

        private static readonly Regex IconSizeRegex = new(@"-(\d+)x(\d+)\.", RegexOptions.Compiled);

        private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
            { "br", "img", "hr", "input", "meta", "link", "source", "wbr" };

        private static readonly HashSet<string> SkipTags = new(StringComparer.OrdinalIgnoreCase)
            { "script", "style", "iframe", "video", "svg", "button", "form", "nav", "footer", "template" };

        private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
            { "p", "h1", "h2", "h3", "h4", "h5", "h6", "li", "div", "header", "blockquote", "figure", "figcaption", "pre", "table", "tr", "td", "th", "ul", "ol", "hr" };

        private const string BaseUrl = "https://www.leagueoflegends.com";
        private const string NewsBaseUrl = "https://www.leagueoflegends.com/en-us/news/";

        private static readonly SolidColorBrush TextPrimary = FromHex("#E7E9F0");
        private static readonly SolidColorBrush TextSecondary = FromHex("#C6CAD6");
        private static readonly SolidColorBrush Accent = FromHex("#5C85FF");
        private static readonly SolidColorBrush QuoteBackground = FromHex("#151A28");
        private static readonly SolidColorBrush CodeBackground = FromHex("#151A28");
        private static readonly SolidColorBrush BorderColor = FromHex("#2A2F3E");
        private static readonly SolidColorBrush TableHeaderBackground = FromHex("#1B2138");
        private static readonly SolidColorBrush TableRowAlternateBackground = FromHex("#161A2A");
        private static readonly SolidColorBrush White = Brushes.White;

        private sealed class Node
        {
            public string Tag;
            public Dictionary<string, string> Attrs;
            public List<Node> Children;
            public string Text;

            public bool IsText => Tag == null;
            public bool IsSkip { get; set; }
        }

        public static FlowDocument Parse(string html, HttpClient httpClient)
        {
            var doc = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 15,
                Foreground = TextPrimary,
                LineHeight = 26,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextAlignment = TextAlignment.Left
            };

            if (string.IsNullOrWhiteSpace(html)) return doc;

            var nodes = ParseTree(html);
            var blocks = new List<Block>();
            foreach (var node in nodes)
            {
                EmitBlock(blocks, node, httpClient);
            }

            foreach (var block in blocks)
            {
                doc.Blocks.Add(block);
            }

            return doc;
        }

        private static List<Node> ParseTree(string html)
        {
            var root = new List<Node>();
            var openChain = new List<Node>();
            var currentContainer = root;

            foreach (Match match in TokenRegex.Matches(html))
            {
                string token = match.Value;
                if (token[0] != '<')
                {
                    if (currentContainer != null)
                    {
                        currentContainer.Add(new Node { Text = WebUtility.HtmlDecode(token) });
                    }
                    continue;
                }

                bool closing = token.Length > 1 && token[1] == '/';
                int nameStart = closing ? 2 : 1;
                int i = nameStart;
                while (i < token.Length && (char.IsLetterOrDigit(token[i]) || token[i] == '-' || token[i] == '_' || token[i] == ':'))
                {
                    i++;
                }
                if (i >= token.Length) continue;

                string name = token.Substring(nameStart, i - nameStart);
                if (string.IsNullOrEmpty(name)) continue;
                if (name == "!DOCTYPE") continue;

                if (closing)
                {
                    if (openChain.Count > 0 && string.Equals(openChain[openChain.Count - 1].Tag, name, StringComparison.OrdinalIgnoreCase))
                    {
                        openChain.RemoveAt(openChain.Count - 1);
                    }
                    else
                    {
                        int index = -1;
                        for (int j = openChain.Count - 1; j >= 0; j--)
                        {
                            if (string.Equals(openChain[j].Tag, name, StringComparison.OrdinalIgnoreCase))
                            {
                                index = j;
                                break;
                            }
                        }
                        if (index >= 0)
                        {
                            openChain.RemoveRange(index, openChain.Count - index);
                        }
                    }
                    currentContainer = openChain.Count > 0 ? openChain[openChain.Count - 1].Children : root;
                    continue;
                }

                var attrs = ParseAttributes(token.Substring(i));
                var node = new Node { Tag = name, Attrs = attrs, Children = new List<Node>() };
                if (SkipTags.Contains(name)) node.IsSkip = true;
                currentContainer.Add(node);

                if (VoidTags.Contains(name)) continue;

                openChain.Add(node);
                currentContainer = node.Children;
            }

            return root;
        }

        private static Dictionary<string, string> ParseAttributes(string attributeSource)
        {
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in AttrRegex.Matches(attributeSource))
            {
                string key = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[3].Value;
                if (string.IsNullOrEmpty(key)) continue;
                string value = match.Groups[2].Success ? match.Groups[2].Value.Trim('"', '\'') : string.Empty;
                attrs[key] = WebUtility.HtmlDecode(value);
            }
            return attrs;
        }

        private static void EmitBlock(List<Block> blocks, Node node, HttpClient httpClient)
        {
            if (node.IsSkip) return;

            if (node.IsText)
            {
                string text = node.Text.Trim();
                if (string.IsNullOrEmpty(text)) return;
                var paragraph = new Paragraph();
                paragraph.Inlines.Add(new Run(text));
                blocks.Add(paragraph);
                return;
            }

            string tag = node.Tag.ToLowerInvariant();
            switch (tag)
            {
                case "p":
                {
                    // Paragraphs containing <img> must not keep the image inline: WPF renders
                    // InlineUIContainer on the text line, overlapping the following content.
                    // Split the paragraph into text blocks and image blocks instead.
                    if (ContainsImage(node))
                    {
                        EmitParagraphWithImages(blocks, node, httpClient);
                        break;
                    }
                    var paragraph = CreateParagraph();
                    FillInlines(paragraph.Inlines, node, httpClient);
                    if (HasContent(paragraph.Inlines)) blocks.Add(paragraph);
                    break;
                }
                case "div":
                case "section":
                case "article":
                case "main":
                case "body":
                    EmitContainerBlocks(blocks, node, httpClient);
                    break;
                case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                {
                    var paragraph = CreateParagraph();
                    ApplyHeadingStyle(paragraph, tag);
                    FillInlines(paragraph.Inlines, node, httpClient);
                    if (HasContent(paragraph.Inlines)) blocks.Add(paragraph);
                    break;
                }
                case "ul":
                case "ol":
                {
                    var list = new List
                    {
                        MarkerStyle = tag == "ul" ? TextMarkerStyle.Disc : TextMarkerStyle.Decimal,
                        Margin = new Thickness(20, 0, 0, 10)
                    };
                    foreach (var child in node.Children)
                    {
                        if (child.IsText)
                        {
                            string text = child.Text.Trim();
                            if (string.IsNullOrEmpty(text)) continue;
                            var plainItem = new ListItem();
                            var plainParagraph = CreateParagraph();
                            plainParagraph.Inlines.Add(new Run(text));
                            plainItem.Blocks.Add(plainParagraph);
                            list.ListItems.Add(plainItem);
                            continue;
                        }

                        string childTag = child.Tag?.ToLowerInvariant();
                        if (childTag == "li")
                        {
                            var listItem = new ListItem();
                            var itemParagraph = CreateParagraph();
                            FillInlines(itemParagraph.Inlines, child, httpClient);
                            listItem.Blocks.Add(itemParagraph);
                            list.ListItems.Add(listItem);
                        }
                        else if (childTag == "ul" || childTag == "ol")
                        {
                            var nestedBlocks = new List<Block>();
                            EmitBlock(nestedBlocks, child, httpClient);
                            foreach (var nested in nestedBlocks)
                            {
                                if (nested is List nestedList && list.ListItems.Count > 0)
                                {
                                    var lastItem = (System.Collections.IList)list.ListItems;
                                    (lastItem[list.ListItems.Count - 1] as ListItem)?.Blocks.Add(nestedList);
                                }
                            }
                        }
                    }
                    blocks.Add(list);
                    break;
                }
                case "blockquote":
                {
                    // NOTE: a two-column Table with a fixed Pixel bar column and a Star column
                    // does NOT honour the Pixel width in FlowDocument rendering — WPF splits the
                    // width 50/50 and the accent bar expands to half the page. A Section with a
                    // left border reproduces the quote look with a real 3px accent bar.
                    var quoteSection = new Section
                    {
                        Background = QuoteBackground,
                        BorderBrush = Accent,
                        BorderThickness = new Thickness(3, 0, 0, 0),
                        Padding = new Thickness(16, 12, 16, 12),
                        Margin = new Thickness(0, 12, 0, 14)
                    };

                    foreach (var child in node.Children)
                    {
                        if (child.IsSkip) continue;
                        if (child.IsText)
                        {
                            string text = child.Text.Trim();
                            if (string.IsNullOrEmpty(text)) continue;
                            var textParagraph = CreateParagraph();
                            textParagraph.Inlines.Add(new Run(text));
                            quoteSection.Blocks.Add(textParagraph);
                            continue;
                        }
                        if (string.Equals(child.Tag, "p", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(child.Tag, "div", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(child.Tag, "br", StringComparison.OrdinalIgnoreCase))
                        {
                            var innerParagraph = CreateParagraph();
                            FillInlines(innerParagraph.Inlines, child, httpClient);
                            if (HasContent(innerParagraph.Inlines)) quoteSection.Blocks.Add(innerParagraph);
                        }
                    }

                    blocks.Add(quoteSection);
                    break;
                }
                case "figure":
                {
                    var section = new Section();
                    foreach (var child in node.Children)
                    {
                        if (child.IsSkip) continue;
                        if (child.IsText)
                        {
                            string text = child.Text.Trim();
                            if (string.IsNullOrEmpty(text)) continue;
                            var textParagraph = CreateParagraph();
                            textParagraph.Inlines.Add(new Run(text));
                            section.Blocks.Add(textParagraph);
                            continue;
                        }

                        string childTag = child.Tag?.ToLowerInvariant();
                        if (childTag == "img")
                        {
                            EmitImageBlock(section.Blocks, child, httpClient);
                        }
                        else if (childTag == "figcaption")
                        {
                            var caption = CreateParagraph();
                            caption.FontSize = 13;
                            caption.Foreground = TextSecondary;
                            caption.Margin = new Thickness(0, 6, 0, 16);
                            FillInlines(caption.Inlines, child, httpClient);
                            section.Blocks.Add(caption);
                        }
                        else
                        {
                            var nestedBlocks = new List<Block>();
                            EmitBlock(nestedBlocks, child, httpClient);
                            foreach (var nested in nestedBlocks) section.Blocks.Add(nested);
                        }
                    }
                    blocks.Add(section);
                    break;
                }
                case "img":
                    EmitImageBlock(blocks, node, httpClient);
                    break;
                case "hr":
                    blocks.Add(new BlockUIContainer(CreateSeparator()));
                    break;
                case "pre":
                case "code":
                {
                    var paragraph = CreateParagraph();
                    paragraph.FontFamily = new FontFamily("Consolas");
                    paragraph.Background = CodeBackground;
                    paragraph.Padding = new Thickness(12, 8, 12, 8);
                    paragraph.Margin = new Thickness(0, 12, 0, 12);
                    FillInlines(paragraph.Inlines, node, httpClient);
                    if (HasContent(paragraph.Inlines)) blocks.Add(paragraph);
                    break;
                }
                case "table":
                    EmitTableBlock(blocks, node, httpClient);
                    break;
                case "tr":
                case "td":
                case "th":
                case "thead":
                case "tbody":
                case "tfoot":
                case "caption":
                    // Orphaned table part (no parent <table>): flatten as text.
                    {
                        var parts = new List<string>();
                        CollectCellTexts(node, parts);
                        if (parts.Count == 0) break;
                        var paragraph = CreateParagraph();
                        paragraph.Inlines.Add(new Run(string.Join(" — ", parts)));
                        blocks.Add(paragraph);
                        break;
                    }
                default:
                {
                    if (ContainsBlockChildren(node))
                    {
                        EmitContainerBlocks(blocks, node, httpClient);
                    }
                    else
                    {
                        var paragraph = CreateParagraph();
                        FillInlines(paragraph.Inlines, node, httpClient);
                        if (HasContent(paragraph.Inlines)) blocks.Add(paragraph);
                    }
                    break;
                }
            }
        }

        private static bool ContainsBlockChildren(Node node)
        {
            foreach (var child in node.Children)
            {
                if (child.IsSkip || child.IsText) continue;
                if (BlockTags.Contains(child.Tag)) return true;
                if (ContainsBlockChildren(child)) return true;
            }
            return false;
        }

        private static void EmitContainerBlocks(List<Block> blocks, Node node, HttpClient httpClient)
        {
            Paragraph pending = null;

            void Flush()
            {
                if (pending != null && HasContent(pending.Inlines))
                {
                    blocks.Add(pending);
                    pending = null;
                }
            }

            for (int i = 0; i < node.Children.Count; i++)
            {
                var child = node.Children[i];
                if (child.IsSkip) continue;

                if (child.IsText)
                {
                    string text = child.Text.Trim();
                    if (string.IsNullOrEmpty(text)) continue;
                    pending ??= CreateParagraph();
                    pending.Inlines.Add(new Run(text));
                    continue;
                }

                string childTag = child.Tag.ToLowerInvariant();

                // Champion/item icons are emitted as <p><a><img></a></p> right before the <h3>
                // title. Merge them so the icon sits inline next to the title,
                // matching the live site layout.
                if (string.Equals(childTag, "p", StringComparison.OrdinalIgnoreCase) &&
                    !HasTextContent(child) && TryGetIconNode(child, out var iconNode))
                {
                    int nextIndex = i + 1;
                    while (nextIndex < node.Children.Count && node.Children[nextIndex].IsSkip) nextIndex++;
                    if (nextIndex < node.Children.Count && IsHeadingTag(node.Children[nextIndex].Tag))
                    {
                        Flush();

                        var heading = CreateParagraph();
                        ApplyHeadingStyle(heading, node.Children[nextIndex].Tag.ToLowerInvariant());
                        var icon = CreateAbilityIcon(iconNode, httpClient);
                        if (icon != null)
                        {
                            heading.Inlines.Add(new InlineUIContainer(icon)
                            {
                                BaselineAlignment = BaselineAlignment.Center
                            });
                            heading.Inlines.Add(new Run(" "));
                        }
                        FillInlines(heading.Inlines, node.Children[nextIndex], httpClient);
                        if (HasContent(heading.Inlines)) blocks.Add(heading);
                        i = nextIndex;
                        continue;
                    }
                }

                if (BlockTags.Contains(childTag))
                {
                    Flush();
                    EmitBlock(blocks, child, httpClient);
                }
                else if (string.Equals(childTag, "img", StringComparison.OrdinalIgnoreCase))
                {
                    Flush();
                    EmitImageBlock(blocks, child, httpClient);
                }
                else
                {
                    pending ??= CreateParagraph();
                    FillInlines(pending.Inlines, child, httpClient);
                }
            }

            Flush();
        }

        private static bool HasTextContent(Node node)
        {
            if (node.IsSkip) return false;
            if (node.IsText) return !string.IsNullOrWhiteSpace(node.Text);
            foreach (var child in node.Children)
            {
                if (HasTextContent(child)) return true;
            }
            return false;
        }

        private static bool IsHeadingTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return false;
            string t = tag.ToLowerInvariant();
            return t == "h1" || t == "h2" || t == "h3" || t == "h4" || t == "h5" || t == "h6";
        }

        private static bool TryGetIconNode(Node node, out Node iconNode)
        {
            iconNode = null;
            if (node.IsText || node.IsSkip) return false;
            if (string.Equals(node.Tag, "img", StringComparison.OrdinalIgnoreCase))
            {
                if (IsAbilityIconUrl(GetSrc(node)))
                {
                    iconNode = node;
                    return true;
                }
                return false;
            }
            foreach (var child in node.Children)
            {
                if (TryGetIconNode(child, out var nested))
                {
                    iconNode = nested;
                    return true;
                }
            }
            return false;
        }

        private static void ApplyHeadingStyle(Paragraph paragraph, string tag)
        {
            switch (tag)
            {
                case "h1":
                    paragraph.FontSize = 25;
                    paragraph.Margin = new Thickness(0, 24, 0, 12);
                    break;
                case "h2":
                    paragraph.FontSize = 20;
                    paragraph.Margin = new Thickness(0, 20, 0, 10);
                    break;
                case "h3":
                    paragraph.FontSize = 17;
                    paragraph.Margin = new Thickness(0, 18, 0, 8);
                    break;
                case "h4":
                    paragraph.FontSize = 15;
                    paragraph.Margin = new Thickness(0, 16, 0, 8);
                    break;
                default:
                    paragraph.FontSize = 14.5;
                    paragraph.Margin = new Thickness(0, 14, 0, 6);
                    break;
            }
            paragraph.FontWeight = tag == "h4" || tag == "h5" || tag == "h6" ? FontWeights.SemiBold : FontWeights.Bold;
            paragraph.Foreground = White;
        }

        private static bool ContainsImage(Node node)
        {
            if (node.IsText || node.IsSkip) return false;
            if (string.Equals(node.Tag, "img", StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var child in node.Children)
            {
                if (ContainsImage(child)) return true;
            }
            return false;
        }

        private static void EmitParagraphWithImages(List<Block> blocks, Node node, HttpClient httpClient)
        {
            var pendingInlines = new List<Inline>();

            void Flush()
            {
                if (pendingInlines.Count == 0) return;
                var paragraph = CreateParagraph();
                foreach (var inline in pendingInlines) paragraph.Inlines.Add(inline);
                if (HasContent(paragraph.Inlines)) blocks.Add(paragraph);
                pendingInlines.Clear();
            }

            foreach (var child in node.Children)
            {
                if (child.IsSkip) continue;

                if (child.IsText)
                {
                    string text = child.Text;
                    if (!string.IsNullOrEmpty(text)) pendingInlines.Add(new Run(text));
                    continue;
                }

                if (string.Equals(child.Tag, "img", StringComparison.OrdinalIgnoreCase))
                {
                    Flush();
                    EmitImageBlock(blocks, child, httpClient);
                    continue;
                }

                if (ContainsImage(child))
                {
                    Flush();
                    EmitParagraphWithImages(blocks, child, httpClient);
                    continue;
                }

                var span = new Span();
                FillInlines(span.Inlines, child, httpClient);
                pendingInlines.Add(span);
            }

            Flush();
        }

        private static void CollectCellTexts(Node cell, List<string> parts)
        {
            if (cell.IsSkip) return;
            if (cell.IsText)
            {
                string text = cell.Text.Trim();
                if (!string.IsNullOrEmpty(text)) parts.Add(text);
                return;
            }
            foreach (var child in cell.Children) CollectCellTexts(child, parts);
        }

        private static void EmitTableBlock(List<Block> blocks, Node node, HttpClient httpClient)
        {
            var rows = new List<Node>();
            CollectTableRows(node, rows);
            if (rows.Count == 0) return;

            // Column count: max cells across non-section rows.
            int columnCount = 1;
            foreach (var row in rows)
            {
                int cellCount = 0;
                foreach (var cell in row.Children)
                {
                    if (cell.IsSkip || cell.IsText) continue;
                    if (string.Equals(cell.Tag, "td", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(cell.Tag, "th", StringComparison.OrdinalIgnoreCase))
                    {
                        cellCount++;
                    }
                }
                if (cellCount > 1) columnCount = Math.Max(columnCount, cellCount);
            }

            var table = new Table
            {
                CellSpacing = 0,
                Margin = new Thickness(0, 12, 0, 16)
            };
            for (int i = 0; i < columnCount; i++)
            {
                table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
            }

            var group = new TableRowGroup();
            int rowIndex = 0;
            foreach (var row in rows)
            {
                var cells = new List<Node>();
                foreach (var cell in row.Children)
                {
                    if (cell.IsSkip || cell.IsText) continue;
                    if (string.Equals(cell.Tag, "td", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(cell.Tag, "th", StringComparison.OrdinalIgnoreCase))
                    {
                        cells.Add(cell);
                    }
                }
                if (cells.Count == 0) continue;

                // Section header row: a single th spanning the whole table.
                if (cells.Count == 1 && string.Equals(cells[0].Tag, "th", StringComparison.OrdinalIgnoreCase) && !ContainsNestedTable(cells[0]))
                {
                    var sectionParagraph = CreateParagraph();
                    sectionParagraph.FontSize = 15.5;
                    sectionParagraph.FontWeight = FontWeights.Bold;
                    sectionParagraph.Foreground = White;
                    sectionParagraph.Margin = new Thickness(0, 18, 0, 8);
                    FillInlines(sectionParagraph.Inlines, cells[0], httpClient);
                    if (HasContent(sectionParagraph.Inlines)) blocks.Add(sectionParagraph);
                    continue;
                }

                var tableRow = new TableRow();
                bool isHeaderRow = rowIndex == 0 && cells.TrueForAll(c => string.Equals(c.Tag, "th", StringComparison.OrdinalIgnoreCase));

                for (int c = 0; c < cells.Count; c++)
                {
                    var cell = cells[c];
                    bool isTh = string.Equals(cell.Tag, "th", StringComparison.OrdinalIgnoreCase);

                    var tableCell = new TableCell
                    {
                        BorderBrush = BorderColor,
                        BorderThickness = new Thickness(0.5),
                        Padding = new Thickness(10, 8, 10, 8),
                        Background = isTh || isHeaderRow ? TableHeaderBackground : (rowIndex % 2 == 0 ? Brushes.Transparent : TableRowAlternateBackground)
                    };

                    if (cell.Attrs != null && cell.Attrs.TryGetValue("colspan", out var rawSpan) &&
                        int.TryParse(rawSpan, out int colSpan) && colSpan > 1)
                    {
                        tableCell.ColumnSpan = Math.Min(colSpan, columnCount);
                    }

                    AddCellBlocks(tableCell.Blocks, cell, isTh, httpClient);
                    tableRow.Cells.Add(tableCell);
                }
                group.Rows.Add(tableRow);
                rowIndex++;
            }

            if (group.Rows.Count == 0) return;
            table.RowGroups.Add(group);
            blocks.Add(table);
        }

        private static void CollectTableRows(Node node, List<Node> rows)
        {
            if (node.IsSkip || node.IsText) return;
            if (string.Equals(node.Tag, "tr", StringComparison.OrdinalIgnoreCase))
            {
                rows.Add(node);
                return;
            }
            foreach (var child in node.Children) CollectTableRows(child, rows);
        }

        private static bool ContainsNestedTable(Node node)
        {
            if (node.IsText || node.IsSkip) return false;
            if (string.Equals(node.Tag, "table", StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var child in node.Children)
            {
                if (ContainsNestedTable(child)) return true;
            }
            return false;
        }

        private static void AddCellBlocks(BlockCollection blocks, Node cell, bool isHeader, HttpClient httpClient)
        {
            bool hasBlockChildren = false;
            foreach (var child in cell.Children)
            {
                if (child.IsSkip || child.IsText) continue;
                if (BlockTags.Contains(child.Tag) && !string.Equals(child.Tag, "span", StringComparison.OrdinalIgnoreCase))
                {
                    hasBlockChildren = true;
                    break;
                }
            }

            if (!hasBlockChildren)
            {
                var paragraph = CreateParagraph();
                paragraph.Margin = new Thickness(0);
                if (isHeader)
                {
                    paragraph.FontWeight = FontWeights.Bold;
                    paragraph.Foreground = TextPrimary;
                }
                FillInlines(paragraph.Inlines, cell, httpClient);
                if (HasContent(paragraph.Inlines)) blocks.Add(paragraph);
                return;
            }

            var nestedBlocks = new List<Block>();
            EmitContainerBlocks(nestedBlocks, cell, httpClient);
            foreach (var nested in nestedBlocks)
            {
                if (nested is Table) continue; // WPF forbids nested tables; drop them.
                blocks.Add(nested);
            }
        }

        private static void FillInlines(InlineCollection inlines, Node node, HttpClient httpClient)
        {
            foreach (var child in node.Children)
            {
                if (child.IsSkip) continue;
                if (child.IsText)
                {
                    string text = child.Text;
                    if (string.IsNullOrEmpty(text)) continue;
                    var run = new Run(text);
                    inlines.Add(run);
                    continue;
                }

                string tag = child.Tag?.ToLowerInvariant();
                switch (tag)
                {
                    case "br":
                        inlines.Add(new LineBreak());
                        break;
                    case "strong":
                    case "b":
                    case "em":
                    case "i":
                    case "u":
                    case "s":
                    {
                        var span = new Span();
                        span.FontWeight = (tag == "strong" || tag == "b") ? FontWeights.Bold : span.FontWeight;
                        span.FontStyle = (tag == "em" || tag == "i") ? FontStyles.Italic : span.FontStyle;
                        span.TextDecorations = (tag == "u") ? TextDecorations.Underline : span.TextDecorations;
                        span.TextDecorations = (tag == "s") ? TextDecorations.Strikethrough : span.TextDecorations;
                        FillInlines(span.Inlines, child, httpClient);
                        inlines.Add(span);
                        break;
                    }
                    case "a":
                    {
                        string href = child.Attrs != null && child.Attrs.TryGetValue("href", out var rawHref) ? rawHref : null;
                        var hyperlink = new Hyperlink
                        {
                            Foreground = Accent,
                            TextDecorations = null,
                            Cursor = System.Windows.Input.Cursors.Hand
                        };
                        if (!string.IsNullOrEmpty(href))
                        {
                            hyperlink.Tag = ResolveUrl(href);
                            hyperlink.RequestNavigate += Hyperlink_RequestNavigate;
                        }
                        FillInlines(hyperlink.Inlines, child, httpClient);
                        inlines.Add(hyperlink);
                        break;
                    }
                    case "span":
                    case "div":
                    case "font":
                    case "code":
                    {
                        var span = new Span();
                        if (tag == "code")
                        {
                            span.FontFamily = new FontFamily("Consolas");
                            span.Foreground = TextSecondary;
                        }
                        FillInlines(span.Inlines, child, httpClient);
                        inlines.Add(span);
                        break;
                    }
                    case "img":
                    {
                        if (IsAbilityIconUrl(GetSrc(child)))
                        {
                            // Icons (champion/ability, <= 512px) go inline right before the
                            // title text, vertically centered on the text line.
                            var icon = CreateAbilityIcon(child, httpClient);
                            if (icon != null)
                            {
                                inlines.Add(new InlineUIContainer(icon)
                                {
                                    BaselineAlignment = BaselineAlignment.Center
                                });
                            }
                        }
                        else
                        {
                            // Content images are block-level: keep them off the text line
                            // so they don't overlap surrounding inlines.
                            inlines.Add(new LineBreak());
                            var element = CreateImageBlock(child, httpClient);
                            if (element != null) inlines.Add(new InlineUIContainer(element));
                            inlines.Add(new LineBreak());
                        }
                        break;
                    }
                    case "p":
                    case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                    case "ul": case "ol": case "blockquote": case "figure": case "hr":
                    {
                        // Nested block inside a paragraph: flatten as plain inline text.
                        CollectPlainText(child, inlines);
                        break;
                    }
                    default:
                        FillInlines(inlines, child, httpClient);
                        break;
                }
            }
        }

        private static void CollectPlainText(Node node, InlineCollection inlines)
        {
            if (node.IsSkip) return;
            if (node.IsText)
            {
                string text = node.Text;
                if (!string.IsNullOrEmpty(text)) inlines.Add(new Run(text));
                return;
            }
            if (string.Equals(node.Tag, "br", StringComparison.OrdinalIgnoreCase))
            {
                inlines.Add(new LineBreak());
                return;
            }
            if (node.Tag != null && string.Equals(node.Tag, "img", StringComparison.OrdinalIgnoreCase)) return;
            foreach (var child in node.Children) CollectPlainText(child, inlines);
        }

        private static string GetSrc(Node node)
        {
            return node.Attrs != null && node.Attrs.TryGetValue("src", out var rawSrc) ? rawSrc : null;
        }

        private static void EmitImageBlock(ICollection<Block> blocks, Node node, HttpClient httpClient)
        {
            var element = CreateImageElement(node, httpClient);
            if (element == null) return;
            blocks.Add(new BlockUIContainer(element));
        }

        private static FrameworkElement CreateImageElement(Node node, HttpClient httpClient)
        {
            // Icons (champion/item/ability, <= 512px) render as small inline-friendly images;
            // content images render as centered bordered blocks.
            return IsAbilityIconUrl(GetSrc(node))
                ? CreateAbilityIcon(node, httpClient)
                : CreateImageBlock(node, httpClient);
        }

        private static bool IsAbilityIconUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            if (url.IndexOf("ddragon.leagueoflegends.com", StringComparison.OrdinalIgnoreCase) >= 0 &&
                url.IndexOf("/img/", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (url.IndexOf("cmsassets.rgpub.io", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var m = IconSizeRegex.Match(url);
                if (m.Success)
                {
                    int w = int.Parse(m.Groups[1].Value);
                    int h = int.Parse(m.Groups[2].Value);
                    return Math.Max(w, h) <= 512;
                }
            }
            return false;
        }

        private static bool IsChampionIconUrl(string url)
        {
            return url.IndexOf("/img/champion/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static (double, double) GetIconSize(string url)
        {
            if (IsChampionIconUrl(url)) return (40, 40);
            if (url.IndexOf("/img/item/", StringComparison.OrdinalIgnoreCase) >= 0) return (48, 48);
            if (url.IndexOf("cmsassets.rgpub.io", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var m = IconSizeRegex.Match(url);
                if (m.Success && int.Parse(m.Groups[1].Value) == 512) return (48, 48);
            }
            return (32, 32);
        }

        private static FrameworkElement CreateAbilityIcon(Node node, HttpClient httpClient)
        {
            string resolved = ResolveUrl(GetSrc(node));
            if (resolved == null) return null;

            (double w, double h) = GetIconSize(resolved);
            var image = new Image
            {
                Width = w,
                Height = h,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                SnapsToDevicePixels = true
            };
            LoadImageAsync(image, resolved, httpClient);
            return image;
        }

        private static FrameworkElement CreateImageBlock(Node node, HttpClient httpClient)
        {
            string resolved = ResolveUrl(GetSrc(node));
            if (resolved == null) return null;

            var image = new Image
            {
                Stretch = Stretch.Uniform,
                MaxHeight = 420,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true
            };

            var border = new Border
            {
                Background = CodeBackground,
                CornerRadius = new CornerRadius(10),
                BorderBrush = BorderColor,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 16, 0, 16),
                ClipToBounds = true,
                Padding = new Thickness(4),
                HorizontalAlignment = HorizontalAlignment.Center,
                MaxWidth = 780
            };

            border.Child = image;

            LoadImageAsync(image, resolved, httpClient);
            return border;
        }

        private static void LoadImageAsync(Image image, string url, HttpClient httpClient)
        {
            if (httpClient == null) return;

            TaskScheduler continuationScheduler;
            try
            {
                continuationScheduler = TaskScheduler.FromCurrentSynchronizationContext();
            }
            catch (InvalidOperationException)
            {
                continuationScheduler = TaskScheduler.Default;
            }

            Task.Run(async () =>
            {
                try
                {
                    return await httpClient.GetByteArrayAsync(url).ConfigureAwait(false);
                }
                catch
                {
                    return null;
                }
            }).ContinueWith(previous =>
            {
                byte[] bytes = previous.Result;
                if (bytes == null || bytes.Length == 0) return;
                try
                {
                    var bitmap = new BitmapImage();
                    using (var stream = new MemoryStream(bytes))
                    {
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = stream;
                        bitmap.EndInit();
                        bitmap.Freeze();
                    }
                    image.Source = bitmap;
                }
                catch
                {
                    // Ignore broken images.
                }
            }, continuationScheduler);
        }

        private static string ResolveUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (url.StartsWith("//", StringComparison.Ordinal)) return "https:" + url;
            if (url.StartsWith("/", StringComparison.Ordinal)) return BaseUrl + url;
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url;
            return NewsBaseUrl + url;
        }

        private static void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            e.Handled = true;
            var hyperlink = sender as Hyperlink;
            string url = hyperlink?.Tag as string ?? e.Uri?.ToString();
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                // Ignore: link is not opened.
            }
        }

        private static Paragraph CreateParagraph()
        {
            return new Paragraph
            {
                Margin = new Thickness(0, 0, 0, 14),
                TextAlignment = TextAlignment.Left
            };
        }

        private static bool HasContent(InlineCollection inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case Run run when !string.IsNullOrWhiteSpace(run.Text):
                        return true;
                    case LineBreak:
                        continue;
                    case InlineUIContainer:
                        return true;
                    case Span span when HasContent(span.Inlines):
                        return true;
                    case Hyperlink hyperlink when HasContent(hyperlink.Inlines):
                        return true;
                }
            }
            return false;
        }

        private static Border CreateSeparator()
        {
            return new Border
            {
                Height = 1,
                Background = BorderColor,
                Margin = new Thickness(0, 16, 0, 16)
            };
        }

        private static SolidColorBrush FromHex(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }
}
