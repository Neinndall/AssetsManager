namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>One selectable map state: a primary transformation or a secondary-domain state.</summary>
    public sealed class MapVisibilityStateOption
    {
        internal MapVisibilityStateOption(string label, int flags, bool isCustom = false)
        {
            Label = label;
            Flags = flags;
            IsCustom = isCustom;
        }

        public string Label { get; }
        public int Flags { get; }
        public bool IsCustom { get; }
    }
}
