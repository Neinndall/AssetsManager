using System;

namespace AssetsManager.Views.Models
{
    public class JsonDiffHistoryEntry
    {
        public string FileName { get; set; }
        public string OldFilePath { get; set; }
        public string NewFilePath { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
