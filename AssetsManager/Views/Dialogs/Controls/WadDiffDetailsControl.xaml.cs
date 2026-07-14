using AssetsManager.Views.Models.Wad;
using System.Windows.Controls;

namespace AssetsManager.Views.Dialogs.Controls
{
    public partial class WadDiffDetailsControl : UserControl
    {
        public SerializableChunkDiff ViewModel => DataContext as SerializableChunkDiff;

        public WadDiffDetailsControl()
        {
            InitializeComponent();
        }
    }
}
