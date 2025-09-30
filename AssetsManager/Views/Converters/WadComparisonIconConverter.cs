using AssetsManager.Views.Models;
using AssetsManager.Views.Dialogs;
using Material.Icons;
using Material.Icons.WPF;
using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace AssetsManager.Views.Converters
{
    public class WadComparisonIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value switch
            {
                WadGroupViewModel => MaterialIconKind.PackageVariant,
                DiffTypeGroupViewModel diffType => GetDiffTypeIcon(diffType.Type),
                SerializableChunkDiff chunk => GetFileExtensionIcon(chunk.Path),
                FileSystemNodeModel node => GetNodeIcon(node),
                _ => MaterialIconKind.FileQuestionOutline,
            };
        }

        private static MaterialIconKind GetNodeIcon(FileSystemNodeModel node)
        {
            if (node.Status != DiffStatus.Unchanged && node.Type == NodeType.VirtualDirectory)
            {
                return GetDiffStatusIcon(node.Status);
            }

            switch (node.Type)
            {
                case NodeType.RealDirectory:
                case NodeType.VirtualDirectory:
                    return MaterialIconKind.FolderOutline;
                case NodeType.WadFile:
                    return MaterialIconKind.PackageVariant;
                default:
                    return node.Status != DiffStatus.Unchanged ? GetDiffStatusIcon(node.Status) : GetFileExtensionIcon(node.Name);
            }
        }

        private static MaterialIconKind GetDiffStatusIcon(DiffStatus status)
        {
            return status switch
            {
                DiffStatus.New => MaterialIconKind.FilePlusOutline,
                DiffStatus.Deleted => MaterialIconKind.FileRemoveOutline,
                DiffStatus.Modified => MaterialIconKind.FileEditOutline,
                DiffStatus.Renamed => MaterialIconKind.FileMoveOutline,
                _ => MaterialIconKind.FileQuestionOutline,
            };
        }

        private static MaterialIconKind GetDiffTypeIcon(ChunkDiffType type)
        {
            return type switch
            {
                ChunkDiffType.New => MaterialIconKind.FilePlusOutline,
                ChunkDiffType.Removed => MaterialIconKind.FileRemoveOutline,
                ChunkDiffType.Modified => MaterialIconKind.FileEditOutline,
                ChunkDiffType.Renamed => MaterialIconKind.FileMoveOutline,
                _ => MaterialIconKind.FileQuestionOutline,
            };
        }

        private static MaterialIconKind GetFileExtensionIcon(string path)
        {
            string extension = Path.GetExtension(path)?.ToLowerInvariant() ?? "";
            return extension switch
            {
                ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" => MaterialIconKind.ImageOutline,
                ".svg" => MaterialIconKind.Svg,
                ".dds" or ".tex" => MaterialIconKind.FileImageOutline,
                ".json" => MaterialIconKind.CodeJson,
                ".js" => MaterialIconKind.LanguageJavascript,
                ".css" => MaterialIconKind.LanguageCss3,
                ".html" => MaterialIconKind.LanguageHtml5,
                ".txt" or ".log" => MaterialIconKind.FileDocumentOutline,
                ".lua" => MaterialIconKind.LanguageLua,
                ".xml" => MaterialIconKind.FileXmlBox,
                ".bin" => MaterialIconKind.FileCodeOutline,
                ".skl" or ".skn" => MaterialIconKind.Person,
                ".webm" or ".ogg" => MaterialIconKind.MoviePlayOutline,
                _ => MaterialIconKind.FileOutline,
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
