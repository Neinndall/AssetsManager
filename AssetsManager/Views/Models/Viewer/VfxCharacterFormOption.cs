using System;
using System.Linq;
using LeagueToolkit.Hashing;

namespace AssetsManager.Views.Models.Viewer
{
    public sealed class VfxCharacterFormOption
    {
        internal VfxCharacterFormOption(VfxCharacterFormDefinition definition, SceneModel model)
        {
            Definition = definition;
            Label = definition.Name;
            if (string.IsNullOrWhiteSpace(Label) || Label.StartsWith("Form ", StringComparison.Ordinal))
            {
                // A body label is only presentation; authored hashes still control visibility.
                string bodyName = model.Parts.FirstOrDefault(part =>
                    part.Name.StartsWith("Body_", StringComparison.OrdinalIgnoreCase) &&
                    definition.ShowSubmeshHashes.Contains(Fnv1a.HashLower(part.Name)))?.Name;
                if (bodyName != null)
                    Label = bodyName["Body_".Length..].Replace('_', ' ');
            }
            if (string.IsNullOrWhiteSpace(Label))
                Label = $"Form {definition.GearIndex + 1}";
        }

        public string Label { get; }
        internal VfxCharacterFormDefinition Definition { get; }
    }
}
