using System;

namespace AssetsManager.Views.Models.Monitor
{
    public class JsonDiffHistoryEntry
    {
        public string FileName { get; set; }
        public string OldFilePath { get; set; }
        public string NewFilePath { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
