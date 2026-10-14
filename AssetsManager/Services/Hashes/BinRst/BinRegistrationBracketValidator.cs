using System;
using System.Collections.Generic;
using AssetsManager.Views.Models.Hashes;

namespace AssetsManager.Services.Hashes
{
    /// <summary>
    /// Validates type name candidates against the lexicographical reflection registration order
    /// observed in the League of Legends Windows client binaries.
    /// </summary>
    public sealed class BinRegistrationBracketValidator
    {
        public readonly record struct RegistrationBracket(string LowerBound, string UpperBound, string Note);

        // Ground-truth anchor brackets documented from reflection registrars
        private static readonly Dictionary<uint, RegistrationBracket> KnownBrackets = new()
        {
            // IParticleSpawnDataGenerator (1519e8d2)
            [0x1519e8d2] = new("FloatGraphMaterialDriver", "IVfxEmissionSource", "Interface registered between FloatGraphMaterialDriver and IVfxEmissionSource"),

            // NavigationGridParticleSpawnDataGenerator (671b7351)
            [0x671b7351] = new("MutatorMapVisibilityController", "RefundAbilityPointsCheat", "Registered between MutatorMapVisibilityController and RefundAbilityPointsCheat"),

            // VfxMaterialContainer (3bf517c5)
            [0x3bf517c5] = new("VfxLingerDefinitionData", "VfxMaterialOverrideDefinitionData", "Registered between VfxLingerDefinitionData and VfxMaterialOverrideDefinitionData"),

            // VfxEmbeddedMaterial (d2807c60)
            [0x2807c60] = new("VfxCustomModelMeshTransformDefinitionData", "VfxEmissionLinkedMeshData", "Registered directly before VfxEmissionLinkedMeshData"),

            // VfxEmissionLinkedMeshData (526478f0)
            [0x526478f0] = new("VfxEmbeddedMaterial", "VfxEmissionMeshData", "Registered directly between VfxEmbeddedMaterial and VfxEmissionMeshData"),

            // VfxEmissionMeshData (cd5a34f5)
            [0xcd5a34f5] = new("VfxEmissionLinkedMeshData", "VfxEmissionSkeletonData", "Registered between VfxEmissionLinkedMeshData and VfxEmissionSkeletonData"),

            // VfxEmissionSkeletonData (3df230bf)
            [0x3df230bf] = new("VfxEmissionMeshData", "VfxEmissionSurfaceData", "Registered between VfxEmissionMeshData and VfxEmissionSurfaceData"),

            // NavGridRegionGroupDefinition (2bfb084c)
            [0x2bfb084c] = new("NavGridConfig", "NavGridRegionTagDefinition", "Registered directly after NavGridConfig"),

            // NavGridRegionTagDefinition (f6f4bb5f)
            [0x0f6f4bb5f] = new("NavGridRegionGroupDefinition", "NavGridRegionTagsLink", "Registered directly after NavGridRegionGroupDefinition"),

            // NavGridRegionTagsLink (f42cd443)
            [0x0f42cd443] = new("NavGridRegionTagDefinition", "NavGridTerrainConfig", "Registered between NavGridRegionTagDefinition and NavGridTerrainConfig"),

            // NavGridTerrainTagDefinition (d82714cc)
            [0xd82714cc] = new("NavGridTerrainConfig", "NavGridTerrainSettings", "Registered directly after NavGridTerrainConfig"),

            // Consecutive nav grid region cluster: 2d00e4da, 6b91544a, 41c19efe
            // NavGridRegionInputData (2d00e4da)
            [0x2d00e4da] = new("NarrativeBarksList", "NavGridRegionRenderData", "First of three consecutive NavGrid registrars"),

            // NavGridRegionRenderData (6b91544a)
            [0x6b91544a] = new("NavGridRegionInputData", "NavGridRegionVfxData", "Second of three consecutive NavGrid registrars"),

            // NavGridRegionVfxData (41c19efe) - Replaces false positive VfxPrimitiveCameraSegmentSeriesBeam
            [0x41c19efe] = new("NavGridRegionRenderData", "PlayerAugmentsViewController", "Registered between Narrative block and PlayerAugmentsViewController; impossible to be VfxPrimitive*")
        };

        public bool TryGetBracket(uint hash, out RegistrationBracket bracket) =>
            KnownBrackets.TryGetValue(hash, out bracket);

        /// <summary>
        /// Validates whether a candidate name conforms to the expected alphabetical order of its registrar bracket.
        /// </summary>
        public bool ValidateCandidate(uint hash, string candidateName, out string failureReason)
        {
            if (!KnownBrackets.TryGetValue(hash, out RegistrationBracket bracket))
            {
                failureReason = null;
                return true;
            }

            if (!string.IsNullOrEmpty(bracket.LowerBound) &&
                string.Compare(candidateName, bracket.LowerBound, StringComparison.OrdinalIgnoreCase) <= 0)
            {
                failureReason = $"Name '{candidateName}' lexicographically precedes lower bound '{bracket.LowerBound}'.";
                return false;
            }

            if (!string.IsNullOrEmpty(bracket.UpperBound) &&
                string.Compare(candidateName, bracket.UpperBound, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                failureReason = $"Name '{candidateName}' lexicographically exceeds upper bound '{bracket.UpperBound}'.";
                return false;
            }

            failureReason = null;
            return true;
        }

        /// <summary>
        /// Tests if an existing assigned name is a proven collision (such as 41c19efe mapped to VfxPrimitiveCameraSegmentSeriesBeam).
        /// </summary>
        public bool IsDisprovenCollision(uint hash, string candidateName)
        {
            if (string.IsNullOrWhiteSpace(candidateName)) return false;
            return !ValidateCandidate(hash, candidateName, out _);
        }
    }
}
