using System;
using System.Collections.Concurrent;
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
using AssetsManager.Services.News;

namespace AssetsManager.Utils
{
    /// <summary>
    /// Modern native WPF block renderer for Riot article HTML content.
    /// Converts rich article markup into responsive, hardware-accelerated WPF visual elements
    /// (HUD cards, stats lists with pixel-perfect hanging indent bullets, change tag pills,
    /// headings with champion/item badges, images, tables, and formatted text) without FlowDocument overhead.
    /// </summary>
    public static class ArticleNativeRenderer
    {
        private static readonly Regex TokenRegex = new("<[^>]+>|[^<]+", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex AttrRegex = new("([a-zA-Z_:][-a-zA-Z0-9_:.]*)\\s*=\\s*(\"[^\"]*\"|'[^']*'|[^\\s>]+)|([a-zA-Z_:][-a-zA-Z0-9_:.]*)", RegexOptions.Compiled);
        private static readonly Regex IconSizeRegex = new(@"-(\d+)x(\d+)\.", RegexOptions.Compiled);
        private static readonly Regex BackgroundColorRegex = new(@"background(?:-color)?\s*:\s*(#[0-9a-fA-F]{3,8}|rgba?\([^)]+\))", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
            { "br", "img", "hr", "input", "meta", "link", "source", "wbr" };

        private static readonly HashSet<string> SkipTags = new(StringComparer.OrdinalIgnoreCase)
            { "script", "style", "iframe", "video", "svg", "button", "form", "nav", "footer", "template" };

        private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
            { "p", "h1", "h2", "h3", "h4", "h5", "h6", "li", "div", "header", "blockquote", "figure", "figcaption", "pre", "table", "tr", "td", "th", "ul", "ol", "hr" };

        private static readonly ConcurrentDictionary<string, byte[]> ImageByteCache = new(StringComparer.OrdinalIgnoreCase);

        private const string BaseUrl = "https://www.leagueoflegends.com";
        private const string NewsBaseUrl = "https://www.leagueoflegends.com/en-us/news/";

        private static readonly SolidColorBrush TextPrimary = FromHex("#ECEFF4");
        private static readonly SolidColorBrush TextSecondary = FromHex("#B0B8C8");
        private static readonly SolidColorBrush TextMuted = FromHex("#78829A");
        private static readonly SolidColorBrush Accent = FromHex("#5C85FF");
        private static readonly SolidColorBrush QuoteBackground = FromHex("#141824");
        private static readonly SolidColorBrush CodeBackground = FromHex("#161B28");
        private static readonly SolidColorBrush BorderColor = FromHex("#2A3144");
        private static readonly SolidColorBrush TableHeaderBackground = FromHex("#1A2032");
        private static readonly SolidColorBrush TableRowAlternateBackground = FromHex("#131722");
        private static readonly SolidColorBrush White = Brushes.White;

        private sealed class Node
        {
            public string Tag;
            public Dictionary<string, string> Attrs;
            public List<Node> Children = new();
            public string Text;

            public bool IsText => Tag == null;
            public bool IsSkip { get; set; }
        }

        public static void RenderToPanel(string html, Panel targetPanel, HttpClient httpClient)
        {
            if (targetPanel == null) return;
            targetPanel.Children.Clear();

            if (string.IsNullOrWhiteSpace(html)) return;

            var nodes = ParseTree(html);
            bool isFirst = true;
            foreach (var node in nodes)
            {
                var element = EmitNode(node, httpClient);
                if (element != null)
                {
                    if (isFirst && element is FrameworkElement fe)
                    {
                        // Strip top margin from the very first element to keep top spacing tight and aligned
                        fe.Margin = new Thickness(fe.Margin.Left, 0, fe.Margin.Right, fe.Margin.Bottom);
                        isFirst = false;
                    }
                    targetPanel.Children.Add(element);
                }
            }
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
                    currentContainer.Add(new Node { Text = WebUtility.HtmlDecode(token) });
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
                if (string.IsNullOrEmpty(name) || name == "!DOCTYPE") continue;

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
                var node = new Node { Tag = name, Attrs = attrs };
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

        private static UIElement EmitNode(Node node, HttpClient httpClient)
        {
            if (node.IsSkip) return null;

            if (node.IsText)
            {
                string text = node.Text?.Trim();
                if (string.IsNullOrEmpty(text)) return null;
                return CreateParagraphBlock(new[] { new Run(text) });
            }

            string tag = node.Tag.ToLowerInvariant();
            switch (tag)
            {
                case "p":
                {
                    if (ContainsImage(node))
                    {
                        return EmitContainerPanel(node, httpClient);
                    }
                    var inlines = new List<Inline>();
                    FillInlines(inlines, node, httpClient);
                    return inlines.Count > 0 ? CreateParagraphBlock(inlines) : null;
                }
                case "div":
                case "section":
                case "article":
                case "main":
                case "body":
                case "header":
                    return EmitContainerPanel(node, httpClient);

                case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                    return CreateHeadingElement(node, tag, httpClient);

                case "ul":
                case "ol":
                    return CreateListBlock(node, tag == "ol", httpClient);

                case "blockquote":
                    return CreateBlockquoteCard(node, httpClient);

                case "figure":
                    return CreateFigureBlock(node, httpClient);

                case "img":
                    return CreateImageBlock(node, httpClient);

                case "hr":
                    return CreateSeparator();

                case "pre":
                case "code":
                    return CreateCodeBlock(node);

                case "table":
                    return CreateTableElement(node, httpClient);

                default:
                {
                    if (ContainsBlockChildren(node))
                    {
                        return EmitContainerPanel(node, httpClient);
                    }
                    var inlines = new List<Inline>();
                    FillInlines(inlines, node, httpClient);
                    return inlines.Count > 0 ? CreateParagraphBlock(inlines) : null;
                }
            }
        }

        private static UIElement EmitContainerPanel(Node node, HttpClient httpClient)
        {
            var panel = new StackPanel();

            for (int i = 0; i < node.Children.Count; i++)
            {
                var child = node.Children[i];
                if (child.IsSkip) continue;

                // Handle icon + heading pairing (e.g. champion/item icon right before <h3>)
                string childTag = child.Tag?.ToLowerInvariant();
                if (string.Equals(childTag, "p", StringComparison.OrdinalIgnoreCase) &&
                    !HasTextContent(child) && TryGetIconNode(child, out var iconNode))
                {
                    int nextIndex = i + 1;
                    while (nextIndex < node.Children.Count && node.Children[nextIndex].IsSkip) nextIndex++;
                    if (nextIndex < node.Children.Count && IsHeadingTag(node.Children[nextIndex].Tag))
                    {
                        var headingElement = CreateHeadingWithIcon(node.Children[nextIndex], iconNode, httpClient);
                        if (headingElement != null) panel.Children.Add(headingElement);
                        i = nextIndex;
                        continue;
                    }
                }

                var element = EmitNode(child, httpClient);
                if (element != null)
                {
                    panel.Children.Add(element);
                }
            }

            return panel.Children.Count > 0 ? panel : null;
        }

        private static UIElement CreateHeadingWithIcon(Node headingNode, Node iconNode, HttpClient httpClient)
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 16, 0, 6)
            };

            var icon = CreateAbilityIcon(iconNode, httpClient);
            if (icon != null)
            {
                stack.Children.Add(icon);
            }

            var textBlock = new TextBlock
            {
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = Accent,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };

            var inlines = new List<Inline>();
            FillInlines(inlines, headingNode, httpClient);
            foreach (var inline in inlines)
            {
                textBlock.Inlines.Add(inline);
            }

            stack.Children.Add(textBlock);
            return stack;
        }

        private static UIElement CreateHeadingElement(Node node, string tag, HttpClient httpClient)
        {
            var textBlock = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.Bold
            };

            switch (tag)
            {
                case "h1":
                    textBlock.FontSize = 22;
                    textBlock.Foreground = White;
                    textBlock.Margin = new Thickness(0, 22, 0, 8);
                    break;
                case "h2":
                    textBlock.FontSize = 18;
                    textBlock.Foreground = White;
                    textBlock.Margin = new Thickness(0, 18, 0, 6);
                    break;
                case "h3":
                    textBlock.FontSize = 15.5;
                    textBlock.Foreground = Accent;
                    textBlock.Margin = new Thickness(0, 16, 0, 6);
                    break;
                case "h4":
                    textBlock.FontSize = 14;
                    textBlock.FontWeight = FontWeights.SemiBold;
                    textBlock.Foreground = TextPrimary;
                    textBlock.Margin = new Thickness(0, 12, 0, 4);
                    break;
                default:
                    textBlock.FontSize = 13;
                    textBlock.FontWeight = FontWeights.SemiBold;
                    textBlock.Foreground = TextSecondary;
                    textBlock.Margin = new Thickness(0, 10, 0, 4);
                    break;
            }

            var inlines = new List<Inline>();
            FillInlines(inlines, node, httpClient);
            foreach (var inline in inlines)
            {
                textBlock.Inlines.Add(inline);
            }

            return textBlock;
        }

        private static UIElement CreateParagraphBlock(IEnumerable<Inline> inlines)
        {
            var textBlock = new TextBlock
            {
                FontSize = 13.5,
                Foreground = TextSecondary,
                LineHeight = 22,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };

            foreach (var inline in inlines)
            {
                textBlock.Inlines.Add(inline);
            }

            return textBlock;
        }

        private static UIElement CreateBlockquoteCard(Node node, HttpClient httpClient)
        {
            var border = new Border
            {
                Background = QuoteBackground,
                BorderBrush = Accent,
                BorderThickness = new Thickness(3, 0, 0, 0),
                CornerRadius = new CornerRadius(0, 8, 8, 0),
                Padding = new Thickness(16, 10, 16, 10),
                Margin = new Thickness(0, 6, 0, 12)
            };

            var stack = new StackPanel();
            foreach (var child in node.Children)
            {
                var element = EmitNode(child, httpClient);
                if (element != null)
                {
                    stack.Children.Add(element);
                }
            }

            border.Child = stack;
            return border;
        }

        private static UIElement CreateListBlock(Node node, bool isOrdered, HttpClient httpClient)
        {
            var listStack = new StackPanel { Margin = new Thickness(0, 2, 0, 8) };
            int itemIndex = 1;

            foreach (var child in node.Children)
            {
                if (child.IsSkip) continue;
                string childTag = child.Tag?.ToLowerInvariant();
                if (childTag != "li") continue;

                var rowGrid = new Grid { Margin = new Thickness(0, 1, 0, 3) };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var bulletBlock = new TextBlock
                {
                    Text = isOrdered ? $"{itemIndex}." : "•",
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = Accent,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                    LineHeight = 20,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight
                };
                Grid.SetColumn(bulletBlock, 0);
                rowGrid.Children.Add(bulletBlock);

                var textBlock = new TextBlock
                {
                    FontSize = 13,
                    Foreground = TextSecondary,
                    LineHeight = 20,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    TextWrapping = TextWrapping.Wrap
                };

                var inlines = new List<Inline>();
                FillInlines(inlines, child, httpClient);
                foreach (var inline in inlines)
                {
                    textBlock.Inlines.Add(inline);
                }

                Grid.SetColumn(textBlock, 1);
                rowGrid.Children.Add(textBlock);

                listStack.Children.Add(rowGrid);
                itemIndex++;
            }

            return listStack;
        }

        private static UIElement CreateFigureBlock(Node node, HttpClient httpClient)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 10, 0, 14) };
            foreach (var child in node.Children)
            {
                if (child.IsSkip) continue;
                string childTag = child.Tag?.ToLowerInvariant();
                if (childTag == "img")
                {
                    var img = CreateImageBlock(child, httpClient);
                    if (img != null) stack.Children.Add(img);
                }
                else if (childTag == "figcaption")
                {
                    var inlines = new List<Inline>();
                    FillInlines(inlines, child, httpClient);
                    if (inlines.Count > 0)
                    {
                        var caption = new TextBlock
                        {
                            FontSize = 12,
                            Foreground = TextMuted,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Margin = new Thickness(0, 6, 0, 10),
                            TextWrapping = TextWrapping.Wrap
                        };
                        foreach (var inline in inlines) caption.Inlines.Add(inline);
                        stack.Children.Add(caption);
                    }
                }
                else
                {
                    var element = EmitNode(child, httpClient);
                    if (element != null) stack.Children.Add(element);
                }
            }
            return stack.Children.Count > 0 ? stack : null;
        }

        private static UIElement CreateImageBlock(Node node, HttpClient httpClient)
        {
            string src = GetSrc(node);
            if (IsAbilityIconUrl(src))
            {
                return CreateAbilityIcon(node, httpClient);
            }

            string resolved = ResolveUrl(src);
            if (resolved == null) return null;

            var image = new Image
            {
                Stretch = Stretch.Uniform,
                MaxHeight = 460,
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
                Margin = new Thickness(0, 12, 0, 12),
                ClipToBounds = true,
                Padding = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Center,
                MaxWidth = 840,
                Child = image
            };

            LoadImageAsync(image, resolved, httpClient, 900);
            return border;
        }

        private static UIElement CreateAbilityIcon(Node node, HttpClient httpClient)
        {
            string resolved = ResolveUrl(GetSrc(node));
            if (resolved == null) return null;

            (double w, double h) = GetIconSize(resolved);
            var image = new Image
            {
                Width = w,
                Height = h,
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true
            };

            var border = new Border
            {
                Width = w,
                Height = h,
                CornerRadius = new CornerRadius(6),
                BorderBrush = BorderColor,
                BorderThickness = new Thickness(1),
                Background = CodeBackground,
                ClipToBounds = true,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = image
            };

            LoadImageAsync(image, resolved, httpClient, (int)Math.Ceiling(w * 1.5));
            return border;
        }

        private static UIElement CreateCodeBlock(Node node)
        {
            var inlines = new List<Inline>();
            FillInlines(inlines, node, null);
            var textBlock = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12.5,
                Foreground = TextSecondary,
                TextWrapping = TextWrapping.Wrap
            };
            foreach (var inline in inlines) textBlock.Inlines.Add(inline);

            return new Border
            {
                Background = CodeBackground,
                CornerRadius = new CornerRadius(6),
                BorderBrush = BorderColor,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 6, 0, 10),
                Child = textBlock
            };
        }

        private static UIElement CreateTableElement(Node node, HttpClient httpClient)
        {
            var rows = new List<Node>();
            CollectTableRows(node, rows);
            if (rows.Count == 0) return null;

            int columnCount = 1;
            foreach (var row in rows)
            {
                int count = 0;
                foreach (var cell in row.Children)
                {
                    if (cell.IsSkip || cell.IsText) continue;
                    if (string.Equals(cell.Tag, "td", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(cell.Tag, "th", StringComparison.OrdinalIgnoreCase))
                    {
                        count++;
                    }
                }
                if (count > 1) columnCount = Math.Max(columnCount, count);
            }

            var grid = new Grid();
            for (int i = 0; i < columnCount; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

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

                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                bool isHeaderRow = rowIndex == 0 && cells.TrueForAll(c => string.Equals(c.Tag, "th", StringComparison.OrdinalIgnoreCase));

                for (int col = 0; col < cells.Count; col++)
                {
                    var cell = cells[col];
                    bool isTh = string.Equals(cell.Tag, "th", StringComparison.OrdinalIgnoreCase);

                    var cellBorder = new Border
                    {
                        BorderBrush = BorderColor,
                        BorderThickness = new Thickness(0.5),
                        Padding = new Thickness(10, 7, 10, 7),
                        Background = isTh || isHeaderRow ? TableHeaderBackground : (rowIndex % 2 == 0 ? Brushes.Transparent : TableRowAlternateBackground)
                    };

                    int colSpan = 1;
                    if (cell.Attrs != null && cell.Attrs.TryGetValue("colspan", out var rawSpan) &&
                        int.TryParse(rawSpan, out int span) && span > 1)
                    {
                        colSpan = Math.Min(span, columnCount - col);
                    }

                    Grid.SetRow(cellBorder, rowIndex);
                    Grid.SetColumn(cellBorder, col);
                    if (colSpan > 1) Grid.SetColumnSpan(cellBorder, colSpan);

                    var inlines = new List<Inline>();
                    FillInlines(inlines, cell, httpClient);
                    var cellText = new TextBlock
                    {
                        FontSize = 13,
                        Foreground = isTh || isHeaderRow ? TextPrimary : TextSecondary,
                        FontWeight = isTh || isHeaderRow ? FontWeights.Bold : FontWeights.Normal,
                        TextWrapping = TextWrapping.Wrap
                    };
                    foreach (var inline in inlines) cellText.Inlines.Add(inline);
                    cellBorder.Child = cellText;

                    grid.Children.Add(cellBorder);
                }
                rowIndex++;
            }

            return new Border
            {
                BorderBrush = BorderColor,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true,
                Margin = new Thickness(0, 10, 0, 14),
                Child = grid
            };
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

        private static UIElement CreateSeparator()
        {
            return new Border
            {
                Height = 1,
                Background = BorderColor,
                Opacity = 0.4,
                Margin = new Thickness(0, 14, 0, 14)
            };
        }

        private static void FillInlines(List<Inline> inlines, Node node, HttpClient httpClient)
        {
            foreach (var child in node.Children)
            {
                if (child.IsSkip) continue;
                if (child.IsText)
                {
                    string text = child.Text;
                    if (!string.IsNullOrEmpty(text)) inlines.Add(new Run(text));
                    continue;
                }

                string tag = child.Tag?.ToLowerInvariant();
                switch (tag)
                {
                    case "br":
                        inlines.Add(new LineBreak());
                        break;
                    case "span":
                    {
                        if (TryCreateTagBadge(child, out var badge))
                        {
                            inlines.Add(new InlineUIContainer(badge) { BaselineAlignment = BaselineAlignment.TextBottom });
                            break;
                        }
                        var span = new Span();
                        var nested = new List<Inline>();
                        FillInlines(nested, child, httpClient);
                        foreach (var item in nested) span.Inlines.Add(item);
                        inlines.Add(span);
                        break;
                    }
                    case "strong":
                    case "b":
                    {
                        if (TryCreateTagBadge(child, out var badge))
                        {
                            inlines.Add(new InlineUIContainer(badge) { BaselineAlignment = BaselineAlignment.TextBottom });
                            break;
                        }
                        var bold = new Bold();
                        var nested = new List<Inline>();
                        FillInlines(nested, child, httpClient);
                        foreach (var item in nested) bold.Inlines.Add(item);
                        inlines.Add(bold);
                        break;
                    }
                    case "em":
                    case "i":
                    {
                        var italic = new Italic();
                        var nested = new List<Inline>();
                        FillInlines(nested, child, httpClient);
                        foreach (var item in nested) italic.Inlines.Add(item);
                        inlines.Add(italic);
                        break;
                    }
                    case "u":
                    {
                        var underline = new Underline();
                        var nested = new List<Inline>();
                        FillInlines(nested, child, httpClient);
                        foreach (var item in nested) underline.Inlines.Add(item);
                        inlines.Add(underline);
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
                        var nested = new List<Inline>();
                        FillInlines(nested, child, httpClient);
                        foreach (var item in nested) hyperlink.Inlines.Add(item);
                        inlines.Add(hyperlink);
                        break;
                    }
                    case "code":
                    {
                        var span = new Span
                        {
                            FontFamily = new FontFamily("Consolas"),
                            Foreground = TextSecondary
                        };
                        var nested = new List<Inline>();
                        FillInlines(nested, child, httpClient);
                        foreach (var item in nested) span.Inlines.Add(item);
                        inlines.Add(span);
                        break;
                    }
                    case "img":
                    {
                        if (IsAbilityIconUrl(GetSrc(child)))
                        {
                            var icon = CreateAbilityIcon(child, httpClient);
                            if (icon != null)
                            {
                                inlines.Add(new InlineUIContainer(icon) { BaselineAlignment = BaselineAlignment.Center });
                            }
                        }
                        break;
                    }
                    default:
                        FillInlines(inlines, child, httpClient);
                        break;
                }
            }
        }

        private static bool TryCreateTagBadge(Node node, out FrameworkElement badge)
        {
            badge = null;
            string text = CollectAllText(node)?.Trim();
            if (string.IsNullOrWhiteSpace(text)) return false;

            string style = null;
            node.Attrs?.TryGetValue("style", out style);
            string hexBg = ExtractBackgroundColor(style);

            string clean = text.Trim('[', ']', ' ', '\u00A0');
            string upper = clean.ToUpperInvariant();

            bool isKnownTag = upper == "NEW" || upper == "REMOVED" || upper == "UPDATED" || upper == "ADJUSTED" ||
                             upper == "BUFF" || upper == "NERF" || upper == "BUGFIX" || upper == "QOL" ||
                             upper == "CHANGED" || upper == "HOTFIX";

            if (!isKnownTag && string.IsNullOrEmpty(hexBg)) return false;

            badge = CreateTagBadge(clean, hexBg);
            return true;
        }

        private static FrameworkElement CreateTagBadge(string text, string hexBg = null)
        {
            string upper = text.Trim().ToUpperInvariant();
            SolidColorBrush bgBrush;
            SolidColorBrush fgBrush;
            SolidColorBrush borderBrush;

            if (upper == "NEW" || upper == "BUFF")
            {
                bgBrush = FromHex("#142E1B");
                fgBrush = FromHex("#4EFA8B");
                borderBrush = FromHex("#226338");
            }
            else if (upper == "REMOVED" || upper == "NERF")
            {
                bgBrush = FromHex("#331618");
                fgBrush = FromHex("#FF6B6B");
                borderBrush = FromHex("#6E2328");
            }
            else if (upper == "UPDATED" || upper == "ADJUSTED" || upper == "CHANGED")
            {
                bgBrush = FromHex("#14253B");
                fgBrush = FromHex("#5CA8FF");
                borderBrush = FromHex("#1E4B7A");
            }
            else if (!string.IsNullOrEmpty(hexBg))
            {
                bgBrush = FromHex(hexBg);
                fgBrush = White;
                borderBrush = BorderColor;
            }
            else
            {
                bgBrush = FromHex("#1A2233");
                fgBrush = TextPrimary;
                borderBrush = BorderColor;
            }

            var border = new Border
            {
                Background = bgBrush,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Height = 15,
                Padding = new Thickness(5, 0, 5, 0),
                Margin = new Thickness(0, 0, 5, -2),
                VerticalAlignment = VerticalAlignment.Center
            };

            var tb = new TextBlock
            {
                Text = upper,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = fgBrush,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, -1, 0, 0)
            };

            border.Child = tb;
            return border;
        }

        private static string ExtractBackgroundColor(string style)
        {
            if (string.IsNullOrEmpty(style)) return null;
            var match = BackgroundColorRegex.Match(style);
            return match.Success ? match.Groups[1].Value : null;
        }

        private static string CollectAllText(Node node)
        {
            if (node.IsText) return node.Text;
            var sb = new System.Text.StringBuilder();
            foreach (var child in node.Children)
            {
                if (child.IsSkip) continue;
                sb.Append(CollectAllText(child));
            }
            return sb.ToString();
        }

        private static void LoadImageAsync(Image image, string url, HttpClient httpClient, int decodeWidth = 0)
        {
            if (string.IsNullOrEmpty(url) || httpClient == null) return;

            if (ImageByteCache.TryGetValue(url, out var cachedBytes))
            {
                ApplyBitmap(image, cachedBytes, decodeWidth);
                return;
            }

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
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.TryAddWithoutValidation("User-Agent", NewsService.BrowserUserAgent);
                    using var response = await httpClient.SendAsync(request).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) return null;
                    byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (bytes != null && bytes.Length > 0)
                    {
                        ImageByteCache[url] = bytes;
                    }
                    return bytes;
                }
                catch
                {
                    return null;
                }
            }).ContinueWith(previous =>
            {
                byte[] bytes = previous.Result;
                if (bytes != null && bytes.Length > 0)
                {
                    ApplyBitmap(image, bytes, decodeWidth);
                }
            }, continuationScheduler);
        }

        private static void ApplyBitmap(Image image, byte[] bytes, int decodeWidth)
        {
            try
            {
                var bitmap = new BitmapImage();
                using (var stream = new MemoryStream(bytes))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    if (decodeWidth > 0)
                    {
                        bitmap.DecodePixelWidth = decodeWidth;
                    }
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
                // Ignore failure opening external browser.
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
            if (url.IndexOf("/img/item/", StringComparison.OrdinalIgnoreCase) >= 0) return (44, 44);
            if (url.IndexOf("cmsassets.rgpub.io", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var m = IconSizeRegex.Match(url);
                if (m.Success && int.Parse(m.Groups[1].Value) == 512) return (44, 44);
            }
            return (32, 32);
        }

        private static string GetSrc(Node node)
        {
            return node?.Attrs != null && node.Attrs.TryGetValue("src", out var rawSrc) ? rawSrc : null;
        }

        private static string ResolveUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (url.StartsWith("//", StringComparison.Ordinal)) return "https:" + url;
            if (url.StartsWith("/", StringComparison.Ordinal)) return BaseUrl + url;
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url;
            return NewsBaseUrl + url;
        }

        private static SolidColorBrush FromHex(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }
}
