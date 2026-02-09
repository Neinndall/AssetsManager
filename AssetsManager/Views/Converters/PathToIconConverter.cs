using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Material.Icons;
using System.Windows.Data;
using AssetsManager.Views.Models.Dialogs;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Models.Wad;

namespace AssetsManager.Views.Converters
{
    public class PathToIconConverter : IValueConverter
    {
        private static readonly Dictionary<string, MaterialIconKind> KnownExtensions = new Dictionary<string, MaterialIconKind>(StringComparer.OrdinalIgnoreCase)
        {
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
            { ".dds", MaterialIconKind.Texture },
            { ".tex", MaterialIconKind.Texture },
            { ".webm", MaterialIconKind.MoviePlayOutline },
            { ".ogg", MaterialIconKind.MusicNoteOutline },
            { ".bin", MaterialIconKind.FileCodeOutline },
            { ".troybin", MaterialIconKind.StarFourPoints },
            { ".preload", MaterialIconKind.FormatListBulleted },
            { ".skl", MaterialIconKind.HumanMale }, 
            { ".skn", MaterialIconKind.HumanMale },
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
                WadGroupViewModel => MaterialIconKind.ArchiveOutline,
                DiffTypeGroupViewModel diffType => GetDiffTypeIcon(diffType.Type),
                SerializableChunkDiff chunk => GetFileIcon(Path.GetExtension(chunk.Path), chunk.Path),
                FileSystemNodeModel node => GetNodeIcon(node),
                string path => GetIcon(Path.GetExtension(path), path),
                _ => MaterialIconKind.FileQuestionOutline,
            };
        }

        private static MaterialIconKind GetNodeIcon(FileSystemNodeModel node)
        {
            if (node == null || string.IsNullOrEmpty(node.FullPath))
            {
                return MaterialIconKind.FileQuestionOutline;
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
                        return MaterialIconKind.ArchiveOutline;
                    }
                    return MaterialIconKind.FolderOutline;
                case NodeType.WadFile:
                    return MaterialIconKind.ArchiveOutline;
                case NodeType.AudioEvent:
                    return MaterialIconKind.PlaylistMusicOutline;
                case NodeType.WemFile:
                    return MaterialIconKind.MusicNoteOutline;
                default:
                    return GetFileIcon(node.Extension, node.FullPath);
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

        private static MaterialIconKind GetFileIcon(string extension, string path)
        {
            var icon = GetIcon(extension, path);
            
            // If it's a file context but GetIcon thinks it's a folder (no extension),
            // we force it to be an unknown file icon.
            if (string.IsNullOrEmpty(extension) && icon == MaterialIconKind.FolderOutline)
            {
                return MaterialIconKind.FileQuestionOutline;
            }
            
            return icon;
        }

        private static MaterialIconKind GetIcon(string extension, string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                return MaterialIconKind.FileQuestionOutline;
            }

            if (!string.IsNullOrEmpty(extension) && KnownExtensions.TryGetValue(extension, out var knownIcon))
            {
                return knownIcon;
            }

            var lowerPath = fullPath.ToLowerInvariant();
            if (lowerPath.Contains("javascript") || lowerPath.Contains("/js/")) return MaterialIconKind.LanguageJavascript;
            if (lowerPath.Contains("/css/")) return MaterialIconKind.LanguageCss3;

            if (string.IsNullOrEmpty(extension))
            {
                return MaterialIconKind.FolderOutline;
            }

            return MaterialIconKind.FileOutline;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
