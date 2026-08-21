using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Utils;

namespace AssetsManager.Tests.Diagnostics.News
{
    /// <summary>
    /// CLI diagnostic for ArticleHtmlToFlowDocument rendering of Riot patch notes.
    /// Dumps the generated FlowDocument block structure (with icon sizes), verifies
    /// layout invariants (3px blockquote accent bar, section headings inside
    /// &lt;header&gt;, icons inline next to titles) and renders real pages to bitmaps
    /// to check for an expanded accent bar.
    /// Usage: dotnet run --project AssetsManager.Tests/AssetsManager.Tests.csproj -- patch-notes-render
    /// </summary>
    internal static class PatchNotesRenderDiagnostic
    {
        private const string FixtureRelativePath = @"Benchmark\Diagnostics\News\patch_26_15_body.html";

        public static void Run()
        {
            string html = LoadPatchHtml();
            Console.WriteLine($"[PatchNotesRender] Fixture: {FixtureRelativePath} ({html.Length:N0} chars)");

            var checks = new List<(string name, bool ok, string detail)>();

            string blockDump = null;
            string accentDump = null;
            string headingDump = null;
            string pageDump = null;
            var pagesBlueish = new List<(int page, int percent)>();

            RunInSta(() =>
            {
                var doc = ArticleHtmlToFlowDocument.Parse(html, new HttpClient());

                blockDump = DumpBlockOrder(doc);
                accentDump = DumpAccentInvariants(doc);
                headingDump = DumpHeadingParagraphs(doc);

                var paginator = ((IDocumentPaginatorSource)doc).DocumentPaginator;
                paginator.PageSize = new Size(900, 1400);
                paginator.ComputePageCount();
                pageDump = RenderPagesToBitmap(paginator, out pagesBlueish);
            });

            Console.WriteLine("\n================ BLOCK ORDER DUMP ================");
            Console.WriteLine(blockDump);

            Console.WriteLine("\n================ ACCENT BAR INVARIANTS ================");
            Console.WriteLine(accentDump);

            Console.WriteLine("\n================ HEADING PARAGRAPHS ================");
            Console.WriteLine(headingDump);

            Console.WriteLine("\n================ PAGE RENDER (BLUEISH) ================");
            Console.WriteLine(pageDump);

            int accentCells = ParseCount(accentDump, "cells painted with Accent background:");
            int quoteSections = ParseCount(accentDump, "blockquote sections with 3px accent border:");
            checks.Add(("No accent cells in tables", accentCells == 0, $"cells={accentCells}"));
            checks.Add(("Blockquotes render as accent sections", quoteSections >= 20, $"sections={quoteSections}"));
            checks.Add(("Section headings preserved (Patch Highlights)", headingDump.Contains("Patch Highlights", StringComparison.OrdinalIgnoreCase), ""));
            checks.Add(("Section headings preserved (Champions)", headingDump.Contains("Champions", StringComparison.OrdinalIgnoreCase), ""));
            checks.Add(("Section headings preserved (Bugfixes)", headingDump.Contains("Bugfixes", StringComparison.OrdinalIgnoreCase), ""));
            checks.Add(("Champion icon inline (40x40)", blockDump.Contains("[IMG 40x40]"), ""));
            checks.Add(("Ability icon inline (32x32)", blockDump.Contains("[IMG 32x32]"), ""));
            checks.Add(("No full-width bordered icons in headings", !headingDump.Contains("[Border: Image]"), ""));
            checks.Add(("Pages stay under 5% blueish", pagesBlueish.All(p => p.percent < 5), string.Join(", ", pagesBlueish.Select(p => $"p{p.page}={p.percent}%"))));

            Console.WriteLine("\n================ CHECKS ================");
            bool allOk = true;
            foreach (var (name, ok, detail) in checks)
            {
                Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name} {detail}");
                allOk &= ok;
            }
            Console.WriteLine(allOk ? "\nRESULT: ALL CHECKS PASSED" : "\nRESULT: SOME CHECKS FAILED");
        }

        private static string DumpBlockOrder(FlowDocument doc)
        {
            var sb = new StringBuilder();
            int index = 0;
            foreach (var block in doc.Blocks)
            {
                string kind = DescribeBlock(block, out string background, out string snippet);
                sb.AppendLine($"{index,3} | {kind,-22} | bg={background,-10} | {snippet}");
                index++;
            }
            return sb.ToString();
        }

        private static string DumpAccentInvariants(FlowDocument doc)
        {
            var sb = new StringBuilder();
            int accentCells = 0;
            foreach (var block in doc.Blocks)
            {
                if (block is not Table table) continue;
                foreach (var rowGroup in table.RowGroups)
                {
                    foreach (var row in rowGroup.Rows)
                    {
                        foreach (var cell in row.Cells)
                        {
                            if (cell.Background is SolidColorBrush scb &&
                                scb.Color == Color.FromRgb(0x5C, 0x85, 0xFF))
                            {
                                accentCells++;
                            }
                        }
                    }
                }
            }
            sb.AppendLine($"cells painted with Accent background: {accentCells}");

            int quoteSections = 0;
            foreach (var block in doc.Blocks)
            {
                if (block is Section section &&
                    section.BorderBrush is SolidColorBrush bbr &&
                    bbr.Color == Color.FromRgb(0x5C, 0x85, 0xFF) &&
                    section.BorderThickness.Left == 3)
                {
                    quoteSections++;
                }
            }
            sb.AppendLine($"blockquote sections with 3px accent border: {quoteSections}");
            return sb.ToString();
        }

        private static string DumpHeadingParagraphs(FlowDocument doc)
        {
            var sb = new StringBuilder();
            foreach (var block in doc.Blocks)
            {
                if (block is not Paragraph p) continue;
                string text = FirstText(p.Inlines, 60).Trim();
                if (text.Length < 2 || text.Length > 40) continue;
                sb.AppendLine($"[{p.FontSize,4}] {p.FontWeight} {text}");
            }
            return sb.ToString();
        }

        private static string RenderPagesToBitmap(DocumentPaginator paginator, out List<(int page, int percent)> blueish)
        {
            var sb = new StringBuilder();
            blueish = new List<(int, int)>();
            int pages = Math.Min(paginator.PageCount, 6);
            for (int p = 0; p < pages; p++)
            {
                var page = paginator.GetPage(p);
                var bmp = RenderPageToBitmap(page);
                var pixels = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
                bmp.CopyPixels(pixels, bmp.PixelWidth * 4, 0);

                var sbRow = new StringBuilder();
                int band = bmp.PixelHeight / 20;
                for (int rowBand = 0; rowBand < 20; rowBand++)
                {
                    int blueInBand = 0, bandTotal = 0;
                    for (int y = rowBand * band; y < (rowBand + 1) * band; y++)
                    {
                        for (int x = 0; x < bmp.PixelWidth; x++)
                        {
                            int i = (y * bmp.PixelWidth + x) * 4;
                            byte bb = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                            bandTotal++;
                            if (IsBlueish(bb, g, r)) blueInBand++;
                        }
                    }
                    sbRow.Append($" {blueInBand * 100 / Math.Max(1, bandTotal),2}%");
                }

                int blueishPx = 0, total = 0;
                var histogram = new Dictionary<uint, int>();
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                    total++;
                    if (IsBlueish(b, g, r)) blueishPx++;
                    uint key = (uint)((r >> 4) << 8 | (g >> 4) << 4 | (b >> 4));
                    histogram.TryGetValue(key, out int count);
                    histogram[key] = count + 1;
                }
                var top = histogram.OrderByDescending(kv => kv.Value).Take(5);
                string topColors = string.Join(", ", top.Select(kv => $"#{kv.Key >> 8:X2}{(kv.Key >> 4) & 0xF0:X2}{kv.Key & 0xF0:X2}(~{kv.Value * 100 / total}%)"));
                int percent = blueishPx * 100 / Math.Max(1, total);
                blueish.Add((p, percent));
                sb.AppendLine($"page#{p}: blueish={percent}% colors=[{topColors}] bands:[{sbRow}]");
            }
            return sb.ToString();
        }

        private static bool IsBlueish(byte b, byte g, byte r)
        {
            return b > 140 && b > r + 60 && b > g + 40;
        }

        private static RenderTargetBitmap RenderPageToBitmap(DocumentPage page)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, page.Size.Width, page.Size.Height));
                dc.DrawRectangle(new VisualBrush(page.Visual), null, new Rect(0, 0, page.Size.Width, page.Size.Height));
            }
            var bmp = new RenderTargetBitmap(
                (int)page.Size.Width, (int)page.Size.Height, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            return bmp;
        }

        private static int ParseCount(string dump, string label)
        {
            foreach (var line in dump.Split('\n'))
            {
                if (line.TrimStart().StartsWith(label, StringComparison.Ordinal))
                {
                    string value = line.Substring(line.LastIndexOf(':') + 1).Trim();
                    if (int.TryParse(value, out int result)) return result;
                }
            }
            return -1;
        }

        private static void RunInSta(Action action)
        {
            Exception failure = null;
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null) throw new InvalidOperationException("STA thread failed.", failure);
        }

        private static string LoadPatchHtml()
        {
            string repoRoot = FindRepoRoot();
            string path = Path.Combine(repoRoot, FixtureRelativePath);
            if (!File.Exists(path)) throw new FileNotFoundException($"Fixture not found: {path}");
            return File.ReadAllText(path);
        }

        private static string FindRepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir, "AssetsManager", "AssetsManager.csproj"))) return dir;
                dir = Path.GetDirectoryName(dir);
            }
            throw new DirectoryNotFoundException("Could not locate repository root.");
        }

        private static string DescribeBlock(Block block, out string background, out string snippet)
        {
            background = "none";
            snippet = string.Empty;

            switch (block)
            {
                case Paragraph p:
                    background = BrushToString(p.Background);
                    snippet = DescribeInlines(p.Inlines);
                    return "Paragraph";
                case List list:
                    snippet = $"items={list.ListItems.Count}";
                    return "List";
                case Table t:
                    snippet = $"cells={t.RowGroups.Sum(g => g.Rows.Count)}";
                    return "Table";
                case Section s:
                    snippet = $"blocks={s.Blocks.Count}";
                    return "Section";
                case BlockUIContainer ui when ui.Child != null:
                    snippet = DescribeElement(ui.Child);
                    return "BlockUIContainer";
                default:
                    return block.GetType().Name;
            }
        }

        private static string DescribeInlines(InlineCollection inlines)
        {
            var sb = new StringBuilder();
            CollectTextWithIcons(inlines, sb, 80);
            return sb.ToString();
        }

        private static void CollectTextWithIcons(InlineCollection inlines, StringBuilder sb, int max)
        {
            if (sb.Length >= max) return;
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case Run run:
                        sb.Append(run.Text);
                        break;
                    case Hyperlink link:
                        CollectTextWithIcons(link.Inlines, sb, max);
                        break;
                    case Span span:
                        CollectTextWithIcons(span.Inlines, sb, max);
                        break;
                    case InlineUIContainer ic when ic.Child is Image img:
                        sb.Append($"[IMG {img.Width}x{img.Height}]");
                        break;
                }
                if (sb.Length >= max) break;
            }
        }

        private static string DescribeElement(UIElement element)
        {
            if (element is Image img) return $"Image {img.Width}x{img.Height}";
            if (element is Border border) return "Border: " + (border.Child?.GetType().Name ?? "null");
            return element.GetType().Name;
        }

        private static string BrushToString(Brush brush)
        {
            if (brush == null) return "none";
            if (brush is SolidColorBrush scb) return scb.Color.ToString();
            return brush.GetType().Name;
        }

        private static void CollectText(InlineCollection inlines, StringBuilder sb, int max)
        {
            if (sb.Length >= max) return;
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case Run run:
                        sb.Append(run.Text);
                        break;
                    case Hyperlink link:
                        CollectText(link.Inlines, sb, max);
                        break;
                    case Span span:
                        CollectText(span.Inlines, sb, max);
                        break;
                }
                if (sb.Length >= max) break;
            }
        }

        private static string FirstText(InlineCollection inlines, int max)
        {
            var sb = new StringBuilder();
            CollectText(inlines, sb, max);
            return sb.ToString();
        }
    }
}
