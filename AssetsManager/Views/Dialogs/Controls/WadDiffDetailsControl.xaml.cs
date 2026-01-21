using AssetsManager.Views.Models.Wad;
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
                this.DataContext = diff;

                if (diff.Type == ChunkDiffType.Modified)
                {
                    long sizeDiff = (long)(diff.NewUncompressedSize ?? 0) - (long)(diff.OldUncompressedSize ?? 0);
                    if (sizeDiff != 0)
                    {
                        // Note: If we really wanted to be clean, this formatting would be in the Model property,
                        // but since it's a specific UI-only addition for Modified type, we can keep it here or ignore.
                        // Let's keep it simple for now as the main visibility mess is gone.
                    }
                }
            }
            else
            {
                this.DataContext = null;
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
