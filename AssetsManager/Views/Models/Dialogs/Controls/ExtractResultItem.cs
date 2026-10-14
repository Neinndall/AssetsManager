using AssetsManager.Services.Explorer;
using AssetsManager.Services.Formatting;
using AssetsManager.Views.Models.Wad;

namespace AssetsManager.Views.Models.Dialogs.Controls
{
    /// <summary>
    /// Per-file outcome of a new-asset extraction. Produced by
    /// <see cref="AssetsManager.Services.Downloads.ExtractionService"/> so failures no
    /// longer abort the whole batch.
    /// </summary>
    public class ExtractResultItem
    {
        public SerializableChunkDiff Diff { get; set; }
        public bool Success { get; set; }
        public string OutputPath { get; set; }
        public WadExportMode Mode { get; set; }
        public string ErrorMessage { get; set; }
    }
}
