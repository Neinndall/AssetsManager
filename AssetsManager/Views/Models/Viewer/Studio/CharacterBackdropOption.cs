namespace AssetsManager.Views.Models.Viewer
{
    public sealed class CharacterBackdropOption
    {
        internal CharacterBackdropOption(string label, MapSceneSource source)
        {
            Label = label ?? string.Empty;
            Source = source;
        }

        public string Label { get; }
        internal MapSceneSource Source { get; }
        public override string ToString() => Label;
    }
}
