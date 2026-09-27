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
            if (definition.IsBase)
            {
                Label = "Base";
                return;
            }

            Label = definition.Name;
            if (string.IsNullOrWhiteSpace(Label) || Label.StartsWith("Form ", StringComparison.Ordinal))
            {
                string swapName = ResolveNameFromTextureSwaps(definition.GearIndex, model);
                if (!string.IsNullOrWhiteSpace(swapName))
                {
                    Label = swapName;
                }
                else
                {
                    // A body label is only presentation; authored hashes still control visibility.
                    string bodyName = model?.Parts?.FirstOrDefault(part =>
                        part.Name.StartsWith("Body_", StringComparison.OrdinalIgnoreCase) &&
                        definition.ShowSubmeshHashes.Contains(Fnv1a.HashLower(part.Name)))?.Name;
                    if (bodyName != null)
                        Label = bodyName["Body_".Length..].Replace('_', ' ');
                }
            }
            if (string.IsNullOrWhiteSpace(Label))
                Label = $"Form {definition.GearIndex + 1}";
        }

        private static string ResolveNameFromTextureSwaps(int gearIndex, SceneModel model)
        {
            if (model?.Parts == null) return null;
            foreach (var part in model.Parts)
            {
                var material = part.MaterialDefinition;
                if (material?.TextureSwaps == null) continue;
                foreach (var swap in material.TextureSwaps)
                {
                    string path = swap.Resolve(gearIndex);
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    string token = ExtractFormTokenFromTexturePath(path);
                    if (!string.IsNullOrWhiteSpace(token))
                        return token;
                }
            }
            return null;
        }

        internal static string ExtractFormTokenFromTexturePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string filename = System.IO.Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
            string clean = filename;
            if (clean.EndsWith("_tx_cm", StringComparison.OrdinalIgnoreCase))
                clean = clean[..^"_tx_cm".Length];
            else if (clean.EndsWith("_cm", StringComparison.OrdinalIgnoreCase))
                clean = clean[..^"_cm".Length];

            int skinIndex = clean.IndexOf("_skin", StringComparison.OrdinalIgnoreCase);
            if (skinIndex >= 0)
            {
                int nextUnder = clean.IndexOf('_', skinIndex + 5);
                if (nextUnder >= 0 && nextUnder + 1 < clean.Length)
                {
                    string formPart = clean[(nextUnder + 1)..];
                    if (!string.IsNullOrWhiteSpace(formPart))
                        return char.ToUpperInvariant(formPart[0]) + formPart[1..].Replace('_', ' ');
                }
            }
            return null;
        }

        public string Label { get; }
        internal VfxCharacterFormDefinition Definition { get; }
    }
}
