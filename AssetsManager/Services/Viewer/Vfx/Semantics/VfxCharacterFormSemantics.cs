using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Vfx.Semantics
{
    internal static class VfxCharacterFormSemantics
    {
        /// <summary>
        /// Forms the Studio can preview: forms that reload the model with their own SKN, SKL or materials,
        /// and forms that fit the loaded owner mesh as authored; any other form cannot be previewed.
        /// </summary>
        internal static IReadOnlyList<VfxCharacterFormDefinition> CompatibleForms(
            IEnumerable<VfxCharacterFormDefinition> forms,
            VfxOwnerSceneContext owner)
        {
            var compatible = new List<VfxCharacterFormDefinition>();
            foreach (VfxCharacterFormDefinition form in forms ?? Array.Empty<VfxCharacterFormDefinition>())
            {
                if (form == null)
                    continue;
                if (form.ReloadsModel)
                {
                    compatible.Add(form);
                    continue;
                }
                if (form.HasMaterialOverrides)
                    continue;
                if (!string.IsNullOrWhiteSpace(form.MeshPath) && !SameCharacterAsset(form.MeshPath, owner?.MeshPath))
                    continue;
                if (!string.IsNullOrWhiteSpace(form.SkeletonPath) && !SameCharacterAsset(form.SkeletonPath, owner?.SkeletonPath))
                    continue;
                compatible.Add(form);
            }
            return compatible;
        }

        private static bool SameCharacterAsset(string left, string right) =>
            !string.IsNullOrWhiteSpace(right) && string.Equals(
                left.Replace('\\', '/'), right.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

        internal static IReadOnlyList<VfxCharacterFormOption> FormOptions(
            IEnumerable<VfxCharacterFormDefinition> forms,
            VfxOwnerSceneContext owner,
            SceneModel model)
        {
            var options = CompatibleForms(forms, owner)
                .Select(form => new VfxCharacterFormOption(form, FormLabel(form, model))).ToList();
            // Some skins author their own Base gear; others only author the alternate forms.
            if (options.Count > 0 && !options.Any(option =>
                string.Equals(option.Label, "Base", StringComparison.OrdinalIgnoreCase)))
                options.Insert(0, new VfxCharacterFormOption(VfxCharacterFormDefinition.CreateBase(), "Base"));
            return options;
        }

        internal static IReadOnlySet<uint> HiddenSubmeshes(
            IEnumerable<uint> initiallyHidden,
            VfxCharacterFormDefinition form,
            IEnumerable<ModelPart> parts = null,
            IEnumerable<VfxSubmeshCondition> conditions = null,
            GameMaterialState state = null)
        {
            IReadOnlySet<uint> hidden = FormHiddenSubmeshes(initiallyHidden, form, parts);
            if (conditions == null)
                return hidden;

            // The skin's persistent conditions apply last, as authored data over any inferred form lists.
            var result = new HashSet<uint>(hidden);
            GameMaterialState current = (state ?? GameMaterialState.Resting) with { Gear = form?.GearIndex ?? -1 };
            foreach (VfxSubmeshCondition condition in conditions)
            {
                if (condition.Condition.Evaluate(current) != true)
                    continue;
                result.ExceptWith(condition.Show);
                result.UnionWith(condition.Hide);
            }
            return result;
        }

        private static IReadOnlySet<uint> FormHiddenSubmeshes(
            IEnumerable<uint> initiallyHidden,
            VfxCharacterFormDefinition form,
            IEnumerable<ModelPart> parts)
        {
            // A reloading form's GearData initialSubmeshToHide, when authored, replaces the owner's
            // baseline; its GearData show/hide lists still apply on top.
            bool reloads = form is { ReloadsModel: true };
            var hidden = new HashSet<uint>(
                (reloads && form.InitialHiddenSubmeshHashes != null ? form.InitialHiddenSubmeshHashes : initiallyHidden)
                ?? Array.Empty<uint>());
            if (form == null || form.IsBase)
                return hidden;

            // Authored show/hide lists in GearData take absolute priority
            if ((form.ShowSubmeshHashes != null && form.ShowSubmeshHashes.Count > 0) ||
                (form.HideSubmeshHashes != null && form.HideSubmeshHashes.Count > 0))
            {
                if (form.ShowSubmeshHashes != null)
                    hidden.ExceptWith(form.ShowSubmeshHashes);
                if (form.HideSubmeshHashes != null)
                    hidden.UnionWith(form.HideSubmeshHashes);
                return hidden;
            }

            // Fallback for skins where Riot authored no submesh lists in GearData (e.g. Sett 66).
            // Reloading forms already carry their authored baseline, so nothing is inferred for them.
            if (parts != null && form.GearIndex >= 0 && !reloads)
            {
                var (inferredShow, inferredHide) = InferFormSubmeshes(form, initiallyHidden, parts);
                hidden.ExceptWith(inferredShow);
                hidden.UnionWith(inferredHide);
            }

            return hidden;
        }

        private static (IReadOnlyList<uint> Show, IReadOnlyList<uint> Hide) InferFormSubmeshes(
            VfxCharacterFormDefinition form,
            IEnumerable<uint> initiallyHidden,
            IEnumerable<ModelPart> parts)
        {
            var partsList = parts as IReadOnlyList<ModelPart> ?? parts.ToArray();
            string keyword = form.Name?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(keyword) || keyword.StartsWith("form ", StringComparison.Ordinal))
            {
                foreach (var part in partsList)
                {
                    string path = part.MaterialDefinition?.ResolveTextureSwap(
                        part.MaterialDefinition.BaseSamplerName, form.GearIndex);
                    string token = ExtractFormTokenFromTexturePath(path);
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        keyword = token.ToLowerInvariant();
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(keyword))
                return (Array.Empty<uint>(), Array.Empty<uint>());

            // Collect keywords for other gear forms so we don't accidentally show their submeshes
            var otherKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in partsList)
            {
                if (part.MaterialDefinition?.TextureSwaps == null) continue;
                foreach (var swap in part.MaterialDefinition.TextureSwaps)
                {
                    foreach (var opt in swap.Options)
                    {
                        if (opt.Condition.Kind == GameMaterialBoolKind.Gear && opt.Condition.GearIndex != form.GearIndex)
                        {
                            string otherToken = ExtractFormTokenFromTexturePath(opt.TexturePath);
                            if (!string.IsNullOrWhiteSpace(otherToken) &&
                                !string.Equals(otherToken, keyword, StringComparison.OrdinalIgnoreCase))
                            {
                                otherKeywords.Add(otherToken.ToLowerInvariant());
                            }
                        }
                    }
                }
            }

            var initiallyHiddenSet = new HashSet<uint>(initiallyHidden ?? Array.Empty<uint>());
            var show = new List<uint>();
            var hide = new List<uint>();

            foreach (var part in partsList)
            {
                uint hash = Fnv1a.HashLower(part.Name);
                string nameLower = part.Name.ToLowerInvariant();

                // Persistent geometry remains always visible
                if (nameLower.Contains("persistent"))
                    continue;

                // Base-specific submeshes must be hidden when an upgraded form is active
                if (nameLower.Contains("base"))
                {
                    hide.Add(hash);
                    continue;
                }

                // If submesh belongs to another known form, keep it hidden
                bool belongsToOtherForm = otherKeywords.Any(k => nameLower.Contains(k));
                if (belongsToOtherForm)
                {
                    hide.Add(hash);
                    continue;
                }

                // If submesh contains the form's keyword, show it
                if (nameLower.Contains(keyword))
                {
                    show.Add(hash);
                    continue;
                }

                // Submeshes authored in initialSubmeshToHide that are not spell effects
                // and do not belong to other forms belong to this transformation
                if (initiallyHiddenSet.Contains(hash) && !nameLower.Contains("snakew"))
                {
                    // Check if this submesh is associated with this form
                    if (keyword.Contains("gold") && (nameLower.Contains("emblem") || nameLower.Contains("envelope")))
                    {
                        show.Add(hash);
                    }
                }
            }

            return (show, hide);
        }

        internal static void RestoreAuthoredTextures(IEnumerable<ModelPart> parts)
        {
            foreach (ModelPart part in parts)
            {
                var material = part.MaterialDefinition;
                if (material == null) continue;
                string path = material.ResolveTextureSwap(material.BaseSamplerName, part.EquippedGearIndex);
                if (path != null)
                    part.SelectedTextureName = SknMaterialTextureResolver.MatchTextureKey(
                        path, part.AllTextures?.Keys.ToArray());
                else if (!string.IsNullOrEmpty(material.BaseTextureName))
                    part.SelectedTextureName = material.BaseTextureName;
            }
        }

        /// <summary>The name shown for a form: authored, else a texture-swap or body token, else its gear index.</summary>
        internal static string FormLabel(VfxCharacterFormDefinition definition, SceneModel model)
        {
            if (definition.IsBase)
                return "Base";

            string label = definition.Name;
            if (string.IsNullOrWhiteSpace(label) || label.StartsWith("Form ", StringComparison.Ordinal))
            {
                string swapName = ResolveNameFromTextureSwaps(definition.GearIndex, model);
                if (!string.IsNullOrWhiteSpace(swapName))
                {
                    label = swapName;
                }
                else
                {
                    // A body label is only presentation; authored hashes still control visibility.
                    string bodyLabel = model?.Parts?
                        .Where(part => definition.ShowSubmeshHashes.Contains(Fnv1a.HashLower(part.Name)))
                        .Select(part => BodyFormLabel(part.Name))
                        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
                    if (bodyLabel != null)
                        label = bodyLabel;
                }
            }
            if (string.IsNullOrWhiteSpace(label))
                label = $"Form {definition.GearIndex + 1}";
            return label;
        }

        private static string BodyFormLabel(string submeshName)
        {
            if (submeshName.StartsWith("Body_", StringComparison.OrdinalIgnoreCase))
                return submeshName["Body_".Length..].Replace('_', ' ');

            // Older body materials place the form after an arbitrary character prefix.
            const string suffix = "_Mat";
            int baseIndex = submeshName.IndexOf("_Base", StringComparison.OrdinalIgnoreCase);
            if (baseIndex < 0 || !submeshName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return null;
            string token = submeshName[(baseIndex + 1)..^suffix.Length];
            if (token.Equals("Base", StringComparison.OrdinalIgnoreCase))
                return "Base";
            if (!token.StartsWith("Base_", StringComparison.OrdinalIgnoreCase))
                return null;
            token = token["Base_".Length..].Replace('_', ' ').Trim();
            return token.Length > 0 ? char.ToUpperInvariant(token[0]) + token[1..] : null;
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
    }
}
