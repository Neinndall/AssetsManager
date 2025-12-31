using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Material.Icons;
using System.Windows.Data;
using AssetsManager.Views.Dialogs;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Models.Wad;

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
            { ".info", MaterialIconKind.FileDocumentOutline },
            { ".stringtable", MaterialIconKind.Translate },
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
            { ".troybin", MaterialIconKind.StarFourPoints },
            { ".preload", MaterialIconKind.FormatListBulleted },
            { ".skl", MaterialIconKind.Person },
            { ".skn", MaterialIconKind.Person },
            { ".wpk", MaterialIconKind.FolderMusicOutline },
            { ".bnk", MaterialIconKind.FolderMusicOutline },
            { ".wasm", MaterialIconKind.CodeBraces },
            { ".bundle", MaterialIconKind.PackageVariant },
            { ".assetbundle", MaterialIconKind.PackageVariant },
        };

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value switch
            {
                WadGroupViewModel => MaterialIconKind.PackageVariant,
                DiffTypeGroupViewModel diffType => GetDiffTypeIcon(diffType.Type),
                SerializableChunkDiff chunk => GetIcon(Path.GetExtension(chunk.Path), chunk.Path),
                FileSystemNodeModel node => GetNodeIcon(node),
                _ => MaterialIconKind.FileQuestionOutline,
            };
        }

        private static MaterialIconKind GetNodeIcon(FileSystemNodeModel node)
        {
            if (node == null || string.IsNullOrEmpty(node.FullPath))
            {
                return MaterialIconKind.FileQuestionOutline; // Default icon for nodes without a path
            }

            if (node.Type == NodeType.VirtualDirectory)
            {
                string name = node.Name;
                if (name.StartsWith("[+]")) return MaterialIconKind.FilePlusOutline;
                if (name.StartsWith("[~]")) return MaterialIconKind.FileEditOutline;
                if (name.StartsWith("[»]")) return MaterialIconKind.FileMoveOutline;
                if (name.StartsWith("[-]")) return MaterialIconKind.FileRemoveOutline;
                if (name.StartsWith("[=]")) return MaterialIconKind.Link;
            }

            switch (node.Type)
            {
                case NodeType.RealDirectory:
                case NodeType.VirtualDirectory:
                    if (node.FullPath.EndsWith(".wad", StringComparison.OrdinalIgnoreCase) || node.FullPath.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase))
                    {
                        return MaterialIconKind.PackageVariant;
                    }
                    return MaterialIconKind.FolderOutline;
                case NodeType.WadFile:
                    return MaterialIconKind.PackageVariant;
                case NodeType.AudioEvent:
                    return MaterialIconKind.PlaylistMusic;
                case NodeType.WemFile:
                    return MaterialIconKind.MusicNote;
                default:
                    return GetIcon(node.Extension, node.FullPath);
            }
        }

        private static MaterialIconKind GetDiffTypeIcon(ChunkDiffType type)
        {
            return type switch
            {
                ChunkDiffType.New => MaterialIconKind.FilePlusOutline,
                ChunkDiffType.Removed => MaterialIconKind.FileRemoveOutline,
                ChunkDiffType.Modified => MaterialIconKind.FileEditOutline,
                ChunkDiffType.Renamed => MaterialIconKind.FileMoveOutline,
                ChunkDiffType.Dependency => MaterialIconKind.Link,
                _ => MaterialIconKind.FileQuestionOutline,
            };
        }

        private static MaterialIconKind GetIcon(string extension, string fullPath)
        {
            // 0. Handle null path for dummy nodes
            if (string.IsNullOrEmpty(fullPath))
            {
                return MaterialIconKind.FileQuestionOutline;
            }

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

