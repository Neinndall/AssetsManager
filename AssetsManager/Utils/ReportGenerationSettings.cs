namespace AssetsManager.Utils
{
    public class ReportGenerationSettings
    {
        public bool Enabled { get; set; }
        public bool FilterNew { get; set; }
        public bool FilterModified { get; set; }
        public bool FilterRenamed { get; set; }
        public bool FilterRemoved { get; set; }
    }
}
