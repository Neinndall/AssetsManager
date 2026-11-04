using System;
using System.Collections.Generic;
using System.Globalization;
using LeagueToolkit.Hashing;

namespace AssetsManager.Views.Models.Viewer
{
    internal sealed record GameMaterialAnimation(uint Hash, float Start, float Duration);

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
        internal IReadOnlyList<GameMaterialAnimation> AnimationPlayback { get; init; } = Array.Empty<GameMaterialAnimation>();
        internal float AnimationDuration { get; init; }
        internal bool LoopAnimation { get; init; } = true;

        internal static GameMaterialState Resting { get; } = new(0, NoBuffs, NoAnimations);

        public static implicit operator GameMaterialState(int gear) =>
            gear == 0 ? Resting : new GameMaterialState(gear, NoBuffs, NoAnimations);

        internal bool HasBuff(string script) =>
            !string.IsNullOrEmpty(script) && Buffs != null && Buffs.Contains(script);

        private float AnimationTime => float.IsFinite(Time)
            ? LoopAnimation && AnimationDuration > 0f ? Math.Max(0f, Time) % AnimationDuration : Math.Max(0f, Time)
            : 0f;

        internal bool IsPlaying(uint animationHash)
        {
            if (Animations?.Contains(animationHash) != true) return false;
            bool timed = false;
            foreach (var animation in AnimationPlayback)
            {
                if (animation.Hash != animationHash) continue;
                timed = true;
                if (ContainsTime(animation)) return true;
            }
            return !timed;
        }

        private bool ContainsTime(GameMaterialAnimation animation) => AnimationTime >= animation.Start &&
            (AnimationTime < animation.Start + animation.Duration ||
             !LoopAnimation && AnimationTime >= AnimationDuration && animation.Start + animation.Duration >= AnimationDuration);

        internal float AnimationFraction(uint animationHash)
        {
            foreach (var animation in AnimationPlayback)
                if (animation.Hash == animationHash && animation.Duration > 0f && ContainsTime(animation))
                    return Math.Clamp((AnimationTime - animation.Start) / animation.Duration, 0f, 1f);
            return 0f;
        }

        internal static string SpellBuffKey(uint spellHash) => $"spell:{spellHash:x8}";

        internal static bool TrySpellBuffHash(string key, out uint hash)
        {
            hash = 0;
            return key?.StartsWith("spell:", StringComparison.OrdinalIgnoreCase) == true &&
                   uint.TryParse(key.AsSpan(6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash);
        }

        /// <summary>The hash animation conditions name a clip by: its name, or the hex hash an unresolved name keeps.</summary>
        internal static uint AnimationHash(string clipName) =>
            clipName?.Length == 8 && uint.TryParse(clipName, System.Globalization.NumberStyles.HexNumber, null, out uint hash)
                ? hash
                : Fnv1a.HashLower(clipName ?? string.Empty);

        internal static GameMaterialState From(int gear, IEnumerable<string> buffs, IEnumerable<uint> animations, bool dead = false) =>
            new(gear, new HashSet<string>(buffs ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase),
                new HashSet<uint>(animations ?? Array.Empty<uint>()), dead);

        /// <returns>
        /// The actor's buffs and selected animation playlist, or null at rest.
        /// </returns>
        internal static GameMaterialState Preview(IEnumerable<string> buffs, AnimationClipCatalogItem clip,
            IReadOnlyDictionary<uint, VfxSpellPreview> spells = null, bool loop = true)
        {
            var animations = new HashSet<uint>();
            var playback = new List<GameMaterialAnimation>();
            if (clip != null && !clip.IsBindPose)
            {
                animations.Add(AnimationHash(clip.Name));
                if (clip.Clip?.OwnerPathHash > 0)
                    animations.Add(clip.Clip.OwnerPathHash);
                if (clip.Duration > 0f && float.IsFinite(clip.Duration))
                    foreach (uint hash in animations)
                        playback.Add(new(hash, 0f, clip.Duration));
                foreach (var animation in clip.MaterialAnimations ?? Array.Empty<GameMaterialAnimation>())
                {
                    animations.Add(animation.Hash);
                    playback.Add(animation);
                }
            }
            var enabled = new HashSet<string>(buffs ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            // A clip preview has no gameplay scripts. Use the spell's authored cast-clip link
            // to supply its spell-keyed material state without changing the actor's manual toggles.
            if (spells != null)
            {
                foreach (var spell in spells)
                {
                    bool cast = !string.IsNullOrWhiteSpace(spell.Value?.AnimationName) &&
                                animations.Contains(AnimationHash(spell.Value.AnimationName));
                    if (cast || (!string.IsNullOrWhiteSpace(spell.Value?.ScriptName) && enabled.Contains(spell.Value.ScriptName)))
                        enabled.Add(SpellBuffKey(spell.Key));
                }
            }
            return enabled.Count == 0 && animations.Count == 0 ? null : new GameMaterialState(0, enabled, animations)
            {
                AnimationPlayback = playback,
                AnimationDuration = clip?.Duration ?? 0f,
                LoopAnimation = loop
            };
        }
    }
}
