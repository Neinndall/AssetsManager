namespace AssetsManager.Views.Models.Versions
{
    public class TargetInstallationOption
    {
        public string DisplayName { get; set; }
        public string Path { get; set; }
        public bool IsMain { get; set; }
        public bool IsPbe { get; set; }
        public string Version { get; set; }

        public override string ToString() => DisplayName;
    }
}
