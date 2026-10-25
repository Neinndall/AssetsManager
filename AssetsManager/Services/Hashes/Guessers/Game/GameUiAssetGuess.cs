using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Hashes.Guessers.Game;

internal sealed partial class GameHashGuesser
{
    private void GuessUiAssetPaths(HashGuessEngine engine, string sourcePath, string wad, ulong chunk,
        CancellationToken cancellationToken, Func<BinTree> treeFactory)
    {
        if (!sourcePath.Contains("narrativebarks", StringComparison.OrdinalIgnoreCase) &&
            !sourcePath.Contains("pingradial", StringComparison.OrdinalIgnoreCase)) return;
        Match mode = Regex.Match(sourcePath.Replace('\\', '/'),
            @"^clientstates/gameplay/ux/lol/([^/]+)/([^/]+)/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!mode.Success) return;
        string modeName = mode.Groups[1].Value.ToLowerInvariant();
        bool portraits = mode.Groups[2].Value.EndsWith("narrativebarks", StringComparison.OrdinalIgnoreCase);
        if (!portraits && !mode.Groups[2].Value.Equals(modeName + "pingradial", StringComparison.OrdinalIgnoreCase)) return;
        string prefix = portraits ? $"assets/ux/narrativebarks/{modeName}/portrait_" : $"assets/ux/{modeName}/hud/{modeName}_ping_";
        const string labelPrefix = "championsplash_";
        string[] themes = null;
        foreach (var obj in treeFactory().Objects.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (obj.ClassHash != Fnv1a.HashLower("UiElementIconData") ||
                obj.Properties.GetValueOrDefault(Fnv1a.HashLower("TextureData")) is not BinTreeStruct texture ||
                texture.Properties.GetValueOrDefault(Fnv1a.HashLower("TextureName")) is not BinTreeWadChunkLink link ||
                !engine.UnknownHashes.Contains(link.Value)) continue;

            string name = obj.Properties.GetValueOrDefault(Fnv1a.HashLower("name")) switch
            {
                BinTreeString text => text.Value,
                BinTreeHash hash => _resolveBinHash?.Invoke(hash.Value),
                _ => null
            };
            name ??= _resolveBinHash?.Invoke(obj.PathHash);
            string label = GetBasename(name?.Replace('\\', '/') ?? "").ToLowerInvariant();
            if (!portraits)
            {
                string file = label switch
                {
                    "background" => "wheel_default.png",
                    "center_bg" => "wheel_center_default.png",
                    _ => null
                };
                Match segment = Regex.Match(label, @"^([1-8])[nesw]+_buttonhover$", RegexOptions.CultureInvariant);
                if (segment.Success) file = $"wheel_highlight_0{segment.Groups[1].Value}.png";
                if (file != null)
                    engine.CheckNormalizedParts(prefix, file, "", HashGuessStrategy.AtlasReference,
                        wad, chunk, expectedHash: link.Value);
                continue;
            }
            int separator = label.LastIndexOf('_');
            if (!label.StartsWith(labelPrefix, StringComparison.Ordinal) || separator <= labelPrefix.Length) continue;
            string character = label[(separator + 1)..], declaredTheme = label[labelPrefix.Length..separator];
            if (character.Length == 0 || !character.All(char.IsAsciiLetterOrDigit)) continue;

            // Display labels can retain a longer champion name than its asset directory.
            var aliases = GetCharacters().Where(value => value.Length >= 3 && character.StartsWith(value, StringComparison.Ordinal))
                .Append(character).Distinct(StringComparer.Ordinal);
            foreach (string alias in aliases)
            {
                string suffix = $"_{alias}.png";
                if (engine.CheckNormalizedParts(prefix, declaredTheme, suffix, HashGuessStrategy.AtlasReference,
                    wad, chunk, expectedHash: link.Value)) break;
                // Worlds display labels also cover cosplay art, which uses a separate theme name.
                if (declaredTheme == "worlds" && engine.CheckNormalizedParts(prefix, "cosplayer", suffix,
                    HashGuessStrategy.AtlasReference, wad, chunk, expectedHash: link.Value)) break;

                // Theme labels can differ from the art names; derive alternatives from existing filenames.
                themes ??= Corpus.GetOrCreate("ui-portrait-theme-prefixes", paths =>
                    HashGuessEngine.BuildBasenameWordlist(paths, 2, 48)
                        .Where(word => char.IsAsciiLetter(word[0]) && word.All(char.IsAsciiLetterOrDigit))
                        .SelectMany(word => Enumerable.Range(2, Math.Min(word.Length, 32) - 1).Reverse().Select(length => word[..length]))
                        .Distinct(StringComparer.Ordinal).ToArray());
                foreach (string theme in themes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (engine.CheckNormalizedParts(prefix, theme, suffix, HashGuessStrategy.AtlasReference,
                        wad, chunk, expectedHash: link.Value)) break;
                }
                if (!engine.UnknownHashes.Contains(link.Value)) break;
            }
        }
    }
}
