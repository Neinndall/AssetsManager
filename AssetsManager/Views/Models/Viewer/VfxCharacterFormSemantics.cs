using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Services.Viewer.Resolvers;
using LeagueToolkit.Hashing;

namespace AssetsManager.Views.Models.Viewer
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
                    string token = VfxCharacterFormOption.ExtractFormTokenFromTexturePath(path);
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
                            string otherToken = VfxCharacterFormOption.ExtractFormTokenFromTexturePath(opt.TexturePath);
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
    }
}
