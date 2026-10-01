namespace AssetsManager.Views.Models.Viewer
{
    public sealed class VfxCharacterFormOption
    {
        internal VfxCharacterFormOption(VfxCharacterFormDefinition definition, string label)
        {
            Definition = definition;
            Label = label;
        }

        public string Label { get; }
        internal VfxCharacterFormDefinition Definition { get; }
    }
}
