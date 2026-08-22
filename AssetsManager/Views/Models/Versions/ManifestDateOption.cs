using System;

namespace AssetsManager.Views.Models.Versions
{
    public sealed class ManifestDateOption
    {
        public DateTime? Date { get; }
        public string DisplayName { get; }

        public ManifestDateOption(DateTime? date, string displayName)
        {
            Date = date?.Date;
            DisplayName = displayName;
        }

        public override string ToString() => DisplayName;
    }
}
