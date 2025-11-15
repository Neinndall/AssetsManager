using AssetsManager.Views.Models;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace AssetsManager.Views.Dialogs.Controls
{
    public partial class WadDiffDetailsControl : UserControl
    {
        public WadDiffDetailsControl()
        {
            InitializeComponent();
        }

        public void DisplayDetails(object item)
        {
            if (item is SerializableChunkDiff diff)
            {
                noSelectionPanel.Visibility = Visibility.Collapsed;
                detailsContentPanel.Visibility = Visibility.Visible;

                // Reset panel visibility
                renamedOldNamePanel.Visibility = Visibility.Collapsed;
                renamedNewNamePanel.Visibility = Visibility.Collapsed;
                genericFileNamePanel.Visibility = Visibility.Collapsed;
                pathPanel.Visibility = Visibility.Visible; // Default to visible
                oldSizePanel.Visibility = Visibility.Visible;
                newSizePanel.Visibility = Visibility.Visible;

                if (diff.Type == ChunkDiffType.Renamed)
                {
                    renamedOldNamePanel.Visibility = Visibility.Visible;
                    renamedNewNamePanel.Visibility = Visibility.Visible;
                    pathPanel.Visibility = Visibility.Collapsed; // Hide generic path for renames

                    renamedOldNameTextBlock.Text = !string.IsNullOrEmpty(diff.OldPath) ? diff.OldPath : "N/A";
                    renamedNewNameTextBlock.Text = !string.IsNullOrEmpty(diff.NewPath) ? diff.NewPath : "N/A";
                }
                else
                {
                    genericFileNamePanel.Visibility = Visibility.Visible;
                    string currentPath = diff.NewPath ?? diff.OldPath;
                    genericFileNameTextBlock.Text = !string.IsNullOrEmpty(currentPath) ? Path.GetFileName(currentPath) : "N/A";
                    var directoryPath = Path.GetDirectoryName(currentPath);
                    pathTextBlock.Text = !string.IsNullOrEmpty(directoryPath) ? directoryPath : "N/A";
                }

                changeTypeTextBlock.Text = diff.Type.ToString();
                sourceWadTextBlock.Text = diff.SourceWadFile;

                oldSizeTextBlock.Text = FormatSize(diff.OldUncompressedSize);
                newSizeTextBlock.Text = FormatSize(diff.NewUncompressedSize);

                if (diff.Type == ChunkDiffType.New)
                {
                    oldSizePanel.Visibility = Visibility.Collapsed;
                }
                else if (diff.Type == ChunkDiffType.Removed)
                {
                    newSizePanel.Visibility = Visibility.Collapsed;
                }
                else if (diff.Type == ChunkDiffType.Modified)
                {
                    long sizeDiff = (long)(diff.NewUncompressedSize ?? 0) - (long)(diff.OldUncompressedSize ?? 0);
                    if (sizeDiff != 0)
                    {
                        string diffSign = sizeDiff > 0 ? "+" : "";
                        newSizeTextBlock.Text += $" ({diffSign}{FormatSize((ulong)Math.Abs(sizeDiff))})";
                    }
                }
            }
            else
            {
                noSelectionPanel.Visibility = Visibility.Visible;
                detailsContentPanel.Visibility = Visibility.Collapsed;
            }
        }

        private string FormatSize(ulong? sizeInBytes)
        {
            if (sizeInBytes == null) return "N/A";
            double sizeInKB = (double)sizeInBytes / 1024.0;
            return $"{sizeInKB:F2} KB";
        }
    }
}
