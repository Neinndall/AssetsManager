namespace AssetsManager.Views.Models.Monitor
{
    public enum HistoryEntryType
    {
        FileDiff,
        WadComparison
    }

    public class HistoryEntry
    {
        public string FileName { get; set; }
        public string OldFilePath { get; set; }
        public string NewFilePath { get; set; }
        public System.DateTime Timestamp { get; set; }
        public HistoryEntryType Type { get; set; } = HistoryEntryType.FileDiff;
        public string ReferenceId { get; set; }
    }
}