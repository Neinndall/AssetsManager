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
            { ".txt", MaterialIconKind.FileDocumentOutline }, // Outline
            { ".log", MaterialIconKind.FileDocumentOutline }, // Outline
            { ".info", MaterialIconKind.FileDocumentOutline }, // Outline
            { ".stringtable", MaterialIconKind.Translate },
            { ".png", MaterialIconKind.ImageOutline }, // Outline
            { ".jpg", MaterialIconKind.ImageOutline }, // Outline
            { ".jpeg", MaterialIconKind.ImageOutline }, // Outline
            { ".bmp", MaterialIconKind.ImageOutline }, // Outline
            { ".svg", MaterialIconKind.Svg },
            { ".dds", MaterialIconKind.Texture }, // Texture (Unique)
            { ".tex", MaterialIconKind.Texture }, // Texture (Unique)
            { ".webm", MaterialIconKind.MoviePlayOutline }, // Outline
            { ".ogg", MaterialIconKind.MusicNoteOutline }, // Outline
            { ".bin", MaterialIconKind.FileCodeOutline }, // Outline
            { ".troybin", MaterialIconKind.StarFourPoints },
            { ".preload", MaterialIconKind.FormatListBulleted },
            { ".skl", MaterialIconKind.HumanMale }, 
            { ".skn", MaterialIconKind.HumanMale },
            { ".wpk", MaterialIconKind.FolderMusicOutline }, // Outline
            { ".bnk", MaterialIconKind.FolderMusicOutline }, // Outline
            { ".wasm", MaterialIconKind.CodeBraces },
            { ".bundle", MaterialIconKind.PackageVariant },
            { ".assetbundle", MaterialIconKind.PackageVariant },
        };

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value switch
            {
                WadGroupViewModel => MaterialIconKind.ArchiveOutline,
                DiffTypeGroupViewModel diffType => GetDiffTypeIcon(diffType.Type),
                SerializableChunkDiff chunk => GetIcon(Path.GetExtension(chunk.Path), chunk.Path),
                FileSystemNodeModel node => GetNodeIcon(node),
                string path => GetIcon(Path.GetExtension(path), path),
                _ => MaterialIconKind.FileQuestionOutline, // Outline
            };
        }

        private static MaterialIconKind GetNodeIcon(FileSystemNodeModel node)
        {
            if (node == null || string.IsNullOrEmpty(node.FullPath))
            {
                return MaterialIconKind.FileQuestionOutline; // Outline
            }

            if (node.Type == NodeType.VirtualDirectory)
            {
                string name = node.Name;
                if (name.StartsWith("[+]")) return MaterialIconKind.FilePlusOutline; // Outline
                if (name.StartsWith("[~]")) return MaterialIconKind.FileEditOutline; // Outline
                if (name.StartsWith("[»]")) return MaterialIconKind.FileMoveOutline; // Outline
                if (name.StartsWith("[-]")) return MaterialIconKind.FileRemoveOutline; // Outline
                if (name.StartsWith("[=]")) return MaterialIconKind.Link;
            }

            switch (node.Type)
            {
                case NodeType.RealDirectory:
                case NodeType.VirtualDirectory:
                    if (node.FullPath.EndsWith(".wad", StringComparison.OrdinalIgnoreCase) || node.FullPath.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase))
                    {
                        return MaterialIconKind.ArchiveOutline;
                    }
                    return MaterialIconKind.FolderOutline; // Outline
                case NodeType.WadFile:
                    return MaterialIconKind.ArchiveOutline;
                case NodeType.AudioEvent:
                    return MaterialIconKind.PlaylistMusicOutline; // Outline
                case NodeType.WemFile:
                    return MaterialIconKind.MusicNoteOutline; // Outline
                default:
                    var icon = GetIcon(node.Extension, node.FullPath);
                    // Correction: If GetIcon returns Folder for a file node (because it has no extension), 
                    // we must override it because we know 'node.Type' is a File type here.
                    if (string.IsNullOrEmpty(node.Extension) && icon == MaterialIconKind.FolderOutline)
                    {
                        return MaterialIconKind.FileQuestionOutline;
                    }
                    return icon;
            }
        }

        private static MaterialIconKind GetDiffTypeIcon(ChunkDiffType type)
        {
            return type switch
            {
                ChunkDiffType.New => MaterialIconKind.FilePlusOutline, // Outline
                ChunkDiffType.Removed => MaterialIconKind.FileRemoveOutline, // Outline
                ChunkDiffType.Modified => MaterialIconKind.FileEditOutline, // Outline
                ChunkDiffType.Renamed => MaterialIconKind.FileMoveOutline, // Outline
                ChunkDiffType.Dependency => MaterialIconKind.Link,
                _ => MaterialIconKind.FileQuestionOutline, // Outline
            };
        }

        private static MaterialIconKind GetIcon(string extension, string fullPath)
        {
            // 0. Handle null path for dummy nodes
            if (string.IsNullOrEmpty(fullPath))
            {
                return MaterialIconKind.FileQuestionOutline; // Outline
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

            // 3. Heuristic: If no extension, assume it's a Directory
            if (string.IsNullOrEmpty(extension))
            {
                return MaterialIconKind.FolderOutline;
            }

            // 4. Default icon for unknown files
            return MaterialIconKind.FileOutline; // Outline
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}

