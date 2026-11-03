using System;
using System.Collections.Generic;
using System.Linq;
using LeagueToolkit.Core.Renderer;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Rendering.GameShaders
{
    /// <summary>
    /// Finds the ShaderCache permutation of a define set. A permutation key concatenates the requested
    /// <c>NAME=VALUE</c> pairs the TOC declares as base defines, sorted by name, and hashes it with XxHash64.
    /// Runtime fallback requires one define list compiled in both stages and preserves engine defines.
    /// Single-stage diagnostic lookup retains its weighted fallback policy.
    /// </summary>
    internal static class GameShaderPermutationLookup
    {
        // Three changes cover every fallback the skin sweep needs; each extra level multiplies the candidates by ~60.
        internal const int MaxChanges = 3;

        private static readonly HashSet<string> KeptDefines = new(StringComparer.Ordinal)
        {
            "DISABLE_FOW", "DISABLE_SHADOWS", "NUM_BLEND_WEIGHTS", "LOW_QUALITY_MODE",
            "FORCE_MANTIS_LIGHTING", "USE_DYNAMIC_LIGHTING", "GENERATE_SHADOW_MAP", "IS_AROUND_GAME"
        };

        internal sealed record PairMatch(int VertexIndex, int PixelIndex, Match Selection);

        /// <summary>Finds one define list compiled in both stages; ties follow TOC declaration order, as in Hexshade.</summary>
        internal static PairMatch FindPair(ShaderToc vertex, ShaderToc pixel,
            IEnumerable<KeyValuePair<string, string>> defines, bool fallback = true, int maxChanges = MaxChanges)
        {
            var requested = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var define in defines) requested.TryAdd(define.Key, define.Value);
            var vertexLookup = LookupFor(vertex);
            var pixelLookup = LookupFor(pixel);

            PairMatch Select(SortedDictionary<string, string> state, IReadOnlyList<Change> changes)
            {
                int vertexIndex = vertexLookup(state);
                if (vertexIndex < 0) return null;
                int pixelIndex = pixelLookup(state);
                return pixelIndex < 0 ? null : new PairMatch(vertexIndex, pixelIndex,
                    new Match(vertexIndex, state, changes));
            }

            PairMatch exact = Select(requested, Array.Empty<Change>());
            if (exact != null || !fallback) return exact;

            var moves = new List<Change>();
            foreach (var group in vertex.BaseDefines.Concat(pixel.BaseDefines).GroupBy(d => d.Name, StringComparer.Ordinal))
            {
                if (KeptDefines.Contains(group.Key)) continue;
                string[] values = group.Select(d => d.Value).Distinct(StringComparer.Ordinal).ToArray();
                requested.TryGetValue(group.Key, out string current);
                // An undeclared value does not contribute to either TOC's key.
                if (!values.Contains(current, StringComparer.Ordinal)) current = null;
                foreach (string value in values)
                    if (value != current)
                        moves.Add(new Change(group.Key, value, current == null ? ChangeKind.Add : ChangeKind.Change));
                if (current != null) moves.Add(new Change(group.Key, null, ChangeKind.Drop));
            }

            var chosen = new List<Change>();
            PairMatch SearchPair(int remaining, int start)
            {
                if (remaining == 0)
                {
                    var state = new SortedDictionary<string, string>(requested, StringComparer.Ordinal);
                    foreach (Change move in chosen)
                        if (move.Value == null) state.Remove(move.Name);
                        else state[move.Name] = move.Value;
                    return Select(state, chosen.ToArray());
                }
                for (int at = start; at < moves.Count; at++)
                {
                    if (chosen.Any(move => move.Name == moves[at].Name)) continue;
                    chosen.Add(moves[at]);
                    PairMatch found = SearchPair(remaining - 1, at + 1);
                    chosen.RemoveAt(chosen.Count - 1);
                    if (found != null) return found;
                }
                return null;
            }
            for (int changes = 1; changes <= maxChanges; changes++)
            {
                PairMatch found = SearchPair(changes, 0);
                if (found != null) return found;
            }
            return null;
        }

        private static Func<SortedDictionary<string, string>, int> LookupFor(ShaderToc toc)
        {
            var indices = new Dictionary<ulong, int>();
            for (int i = toc.ShaderHashes.Count - 1; i >= 0; i--) indices[toc.ShaderHashes[i]] = i;
            var declared = new HashSet<uint>(toc.BaseDefines.Select(d => d.Hash));
            return state =>
            {
                string key = string.Concat(state.Select(d => new ShaderMacroDefinition(d.Key, d.Value))
                    .Where(d => declared.Contains(d.Hash)).Select(d => d.ToString()));
                return indices.TryGetValue(XxHash64Ext.Hash(key), out int index) ? index : -1;
            };
        }

        internal sealed record Match(
            int Index,
            IReadOnlyDictionary<string, string> Defines,
            IReadOnlyList<Change> Changes)
        {
            internal bool Exact => Changes.Count == 0;
        }

        internal enum ChangeKind
        {
            Add,
            Change,
            Drop
        }

        /// <summary>One define edit; <see cref="Value"/> is null when the define is dropped.</summary>
        internal readonly record struct Change(string Name, string Value, ChangeKind Kind)
        {
            public override string ToString() => Kind switch
            {
                ChangeKind.Add => $"+{Name}={Value}",
                ChangeKind.Change => $"{Name}->{Value}",
                _ => $"-{Name}"
            };
        }

        /// <returns>The permutation key of <paramref name="defines"/> for a TOC with <paramref name="baseDefines"/>.</returns>
        internal static string Key(IEnumerable<KeyValuePair<string, string>> defines, IReadOnlyList<ShaderMacroDefinition> baseDefines)
        {
            var declared = new HashSet<uint>(baseDefines.Select(define => define.Hash));
            return string.Concat(defines
                .Select(define => new ShaderMacroDefinition(define.Key, define.Value))
                .Where(define => declared.Contains(define.Hash))
                .OrderBy(define => define.Name, StringComparer.Ordinal)
                .Select(define => define.ToString()));
        }

        /// <returns>
        /// The exact permutation of <paramref name="defines"/> or, with <paramref name="fallback"/>, the nearest one;
        /// null when none is within <paramref name="maxChanges"/>.
        /// </returns>
        internal static Match Find(ShaderToc toc, IEnumerable<KeyValuePair<string, string>> defines, bool fallback = true, int maxChanges = MaxChanges)
        {
            var indexByHash = new Dictionary<ulong, int>(toc.ShaderHashes.Count);
            for (int index = toc.ShaderHashes.Count - 1; index >= 0; index--)
                indexByHash[toc.ShaderHashes[index]] = index;

            // Values keyed by name; only defines the TOC declares take part in the key.
            var declared = new HashSet<uint>(toc.BaseDefines.Select(define => define.Hash));
            var requested = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach ((string name, string value) in defines)
            {
                if (declared.Contains(new ShaderMacroDefinition(name, value).Hash))
                    requested[name] = value;
            }

            int Lookup(SortedDictionary<string, string> state) =>
                indexByHash.TryGetValue(XxHash64Ext.Hash(string.Concat(state.Select(pair => $"{pair.Key}={pair.Value}"))), out int found)
                    ? found
                    : -1;

            int exact = Lookup(requested);
            if (exact >= 0)
                return new Match(exact, requested, Array.Empty<Change>());
            if (!fallback)
                return null;

            Change[] moves = Moves(toc.BaseDefines, requested);
            for (int changes = 1; changes <= maxChanges; changes++)
            {
                var best = (Match: (Match)null, Cost: int.MaxValue);
                Search(moves, requested, changes, 0, new List<Change>(), Lookup, ref best);
                if (best.Match != null)
                    return best.Match;
            }
            return null;
        }

        private static Change[] Moves(IReadOnlyList<ShaderMacroDefinition> baseDefines, IReadOnlyDictionary<string, string> requested)
        {
            var moves = new List<Change>();
            foreach (IGrouping<string, ShaderMacroDefinition> group in baseDefines
                         .GroupBy(define => define.Name, StringComparer.Ordinal)
                         .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                string[] values = group.Select(define => define.Value).Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal).ToArray();
                if (!requested.TryGetValue(group.Key, out string current))
                {
                    moves.AddRange(values.Select(value => new Change(group.Key, value, ChangeKind.Add)));
                    continue;
                }
                moves.AddRange(values.Where(value => value != current).Select(value => new Change(group.Key, value, ChangeKind.Change)));
                moves.Add(new Change(group.Key, null, ChangeKind.Drop));
            }
            return moves.OrderBy(move => move.Kind).ToArray();
        }

        // Every combination of `remaining` moves on distinct defines; the cheapest hit wins, and the first
        // one in move order among equals, so the choice is deterministic.
        private static void Search(
            Change[] moves,
            SortedDictionary<string, string> requested,
            int remaining,
            int start,
            List<Change> chosen,
            Func<SortedDictionary<string, string>, int> lookup,
            ref (Match Match, int Cost) best)
        {
            if (remaining == 0)
            {
                int cost = chosen.Sum(move => (int)move.Kind);
                if (cost >= best.Cost)
                    return;

                var state = new SortedDictionary<string, string>(requested, StringComparer.Ordinal);
                foreach (Change move in chosen)
                {
                    if (move.Kind == ChangeKind.Drop)
                        state.Remove(move.Name);
                    else
                        state[move.Name] = move.Value;
                }
                int index = lookup(state);
                if (index >= 0)
                    best = (new Match(index, state, chosen.ToArray()), cost);
                return;
            }

            for (int at = start; at < moves.Length; at++)
            {
                if (chosen.Any(move => move.Name == moves[at].Name))
                    continue;
                chosen.Add(moves[at]);
                Search(moves, requested, remaining - 1, at + 1, chosen, lookup, ref best);
                chosen.RemoveAt(chosen.Count - 1);
            }
        }
    }
}
