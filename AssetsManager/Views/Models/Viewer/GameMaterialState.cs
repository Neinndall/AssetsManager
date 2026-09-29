using System;
using System.Collections.Generic;
using LeagueToolkit.Hashing;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// The game state dynamic materials read: the equipped gear, the buffs the preview turns on, the animation
    /// clips playing, whether the character is dead and the preview time in seconds that time drivers read.
    /// An int converts to the resting state with that gear.
    /// </summary>
    internal sealed record GameMaterialState(
        int Gear,
        IReadOnlySet<string> Buffs,
        IReadOnlySet<uint> Animations,
        bool Dead = false,
        float Time = 0f)
    {
        private static readonly IReadOnlySet<string> NoBuffs = new HashSet<string>();
        private static readonly IReadOnlySet<uint> NoAnimations = new HashSet<uint>();

        internal static GameMaterialState Resting { get; } = new(0, NoBuffs, NoAnimations);

        public static implicit operator GameMaterialState(int gear) =>
            gear == 0 ? Resting : new GameMaterialState(gear, NoBuffs, NoAnimations);

        internal bool HasBuff(string script) =>
            !string.IsNullOrEmpty(script) && Buffs != null && Buffs.Contains(script);

        internal bool IsPlaying(uint animationHash) => Animations != null && Animations.Contains(animationHash);

        /// <summary>The hash animation conditions name a clip by: its name, or the hex hash an unresolved name keeps.</summary>
        internal static uint AnimationHash(string clipName) =>
            clipName?.Length == 8 && uint.TryParse(clipName, System.Globalization.NumberStyles.HexNumber, null, out uint hash)
                ? hash
                : Fnv1a.HashLower(clipName ?? string.Empty);

        internal static GameMaterialState From(int gear, IEnumerable<string> buffs, IEnumerable<uint> animations, bool dead = false) =>
            new(gear, new HashSet<string>(buffs ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase),
                new HashSet<uint>(animations ?? Array.Empty<uint>()), dead);

        /// <returns>
        /// The state a preview character is in with <paramref name="buffs"/> on while <paramref name="clip"/> and its
        /// parallel clips play; null at rest.
        /// </returns>
        internal static GameMaterialState Preview(IEnumerable<string> buffs, AnimationClipCatalogItem clip)
        {
            var animations = new HashSet<uint>();
            if (clip != null && !clip.IsBindPose)
            {
                animations.Add(AnimationHash(clip.Name));
                animations.UnionWith(clip.Clip?.ChildClipHashes ?? Array.Empty<uint>());
            }
            var enabled = new HashSet<string>(buffs ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            return enabled.Count == 0 && animations.Count == 0 ? null : new GameMaterialState(0, enabled, animations);
        }
    }
}
