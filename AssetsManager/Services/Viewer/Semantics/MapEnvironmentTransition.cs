using System;
using System.Linq;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Semantics
{
    /// <summary>
    /// The game's environment transition: while a map state is entered, materials built with
    /// <c>ENV_TRANSITION</c> draw their permutation with that define on, driven by
    /// <c>TransitionFactorAndDirection</c> from 0 to 1 (elemental decorations rise and glow into place).
    /// </summary>
    internal static class MapEnvironmentTransition
    {
        internal const string Define = "ENV_TRANSITION";

        /// <summary>Whether any pass of the program declares the transition permutation.</summary>
        internal static bool Supports(GameMaterialProgram program) =>
            program?.Passes?.Any(pass => pass?.Defines?.Any(define => define.Name == Define) == true) == true;

        /// <summary>The same program with every pass compiled with <c>ENV_TRANSITION=1</c>.</summary>
        internal static GameMaterialProgram Transitioning(GameMaterialProgram program) =>
            program with
            {
                Passes = program.Passes
                    .Select(pass => pass with
                    {
                        Defines = pass.Defines
                            .Where(define => define.Name != Define)
                            .Append(new GameMaterialDefine(Define, "1", GameMaterialDefineSource.Pass))
                            .ToArray()
                    })
                    .ToArray()
            };

        /// <summary>The transition factor at <paramref name="nowMs"/>: 0 when it starts, 1 once it has run.</summary>
        internal static float Progress(long startMs, long durationMs, long nowMs) =>
            durationMs <= 0 ? 1f : Math.Clamp((nowMs - startMs) / (float)durationMs, 0f, 1f);
    }
}
