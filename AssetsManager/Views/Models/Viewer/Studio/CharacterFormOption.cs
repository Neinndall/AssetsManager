namespace AssetsManager.Views.Models.Viewer
{
    public sealed class CharacterFormOption
    {
        internal CharacterFormOption(CharacterFormDefinition definition, string label)
        {
            Definition = definition;
            Label = label;
        }

        public string Label { get; }
        internal CharacterFormDefinition Definition { get; }
    }
}
