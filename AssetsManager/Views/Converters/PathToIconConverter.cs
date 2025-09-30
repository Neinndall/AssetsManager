using AssetsManager.Views.Models;
using Material.Icons;
using Material.Icons.WPF;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;

namespace AssetsManager.Views.Converters
{
    public class PathToIconConverter : IValueConverter
    {
        private static readonly Dictionary<string, MaterialIconKind> KnownExtensions = new Dictionary<string, MaterialIconKind>(StringComparer.OrdinalIgnoreCase)
        {
            // User-provided list
            { ".json", MaterialIconKind.CodeJson },
            { ".js", MaterialIconKind.LanguageJavascript },
            { ".css", MaterialIconKind.LanguageCss3 },
            { ".html", MaterialIconKind.LanguageHtml5 },
            { ".xml", MaterialIconKind.FileXmlBox },
            { ".lua", MaterialIconKind.LanguageLua },
            { ".txt", MaterialIconKind.FileDocumentOutline },
            { ".log", MaterialIconKind.FileDocumentOutline },
            { ".png", MaterialIconKind.ImageOutline },
            { ".jpg", MaterialIconKind.ImageOutline },
            { ".jpeg", MaterialIconKind.ImageOutline },
            { ".bmp", MaterialIconKind.ImageOutline },
            { ".svg", MaterialIconKind.Svg },
            { ".dds", MaterialIconKind.FileImageOutline },
            { ".tex", MaterialIconKind.FileImageOutline },
            { ".webm", MaterialIconKind.MoviePlayOutline },
            { ".ogg", MaterialIconKind.MusicNote },
            { ".bin", MaterialIconKind.FileCodeOutline },
            { ".skl", MaterialIconKind.Person },
            { ".skn", MaterialIconKind.Person },
        };

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not FileSystemNodeModel node)
            {
                return MaterialIconKind.FileOutline;
            }

            if (node.Type == NodeType.RealDirectory || node.Type == NodeType.VirtualDirectory)
            {
                return MaterialIconKind.FolderOutline;
            }

            if (node.Type == NodeType.WadFile)
            {
                return MaterialIconKind.PackageVariant;
            }

            return GetIcon(node.Extension, node.FullPath);
        }

        private static MaterialIconKind GetIcon(string extension, string fullPath)
        {
            // 1. Check the curated list for a direct match
            if (!string.IsNullOrEmpty(extension) && KnownExtensions.TryGetValue(extension, out var knownIcon))
            {
                return knownIcon;
            }

            // 2. Fallback for files without an extension in their name (e.g. some JS/CSS files in WADs)
            var lowerPath = fullPath.ToLowerInvariant();
            if (lowerPath.Contains("javascript") || lowerPath.Contains("/js/"))
            {
                return MaterialIconKind.LanguageJavascript;
            }
            if (lowerPath.Contains("/css/"))
            {
                return MaterialIconKind.LanguageCss3;
            }

            // 3. Default icon if no match is found
            return MaterialIconKind.FileOutline;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
