namespace AssetsManager.Views.Models.Viewer
{
    public sealed class VfxCharacterBackdropOption
    {
        internal VfxCharacterBackdropOption(string label, MapSceneSource source)
        {
            Label = label ?? string.Empty;
            Source = source;
        }

        public string Label { get; }
        internal MapSceneSource Source { get; }
        public override string ToString() => Label;
    }
}
