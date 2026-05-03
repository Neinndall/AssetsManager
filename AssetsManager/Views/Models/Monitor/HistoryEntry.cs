namespace AssetsManager.Views.Models.Monitor
{
    public enum HistoryEntryType
    {
        FileDiff,
        WadComparison
    }

    public class HistoryEntry
    {
        public string FileName { get; set; } // Legacy or full display
        public string Version { get; set; }   // Structured Version (e.g. 14.10.1234)
        public string DisplayName { get; set; } // Structured Name (e.g. League of Legends (PBE))
        public string OldFilePath { get; set; }
        public string NewFilePath { get; set; }
        public System.DateTime Timestamp { get; set; }
        public HistoryEntryType Type { get; set; } = HistoryEntryType.FileDiff;
        public string ReferenceId { get; set; }
    }
}