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

        private string FormatSize(ulong? sizeInBytes)
        {
            if (sizeInBytes == null) return "N/A";
            double sizeInKB = (double)sizeInBytes / 1024.0;
            return $"{sizeInKB:F2} KB";
        }
    }
}
