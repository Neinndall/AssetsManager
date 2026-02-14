using AssetsManager.Views.Models.Wad;

namespace AssetsManager.Views.Models.Dialogs.Controls
{
    public class WadDiffDetailsModel
    {
        public SerializableChunkDiff SelectedDiff { get; set; }
        public bool HasSelection => SelectedDiff != null;
    }
}