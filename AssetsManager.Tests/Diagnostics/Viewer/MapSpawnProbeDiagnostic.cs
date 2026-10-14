using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// `map-spawn-probe <materials.bin>`: where MapCharacterSpawnSemantics stands every Character the map's
    /// entities name, and the neutral camps it reads, from an extracted map materials BIN.
    /// </summary>
    internal static class MapSpawnProbeDiagnostic
    {
        public static void Run(string[] args)
        {
            BinTree tree;
            using (FileStream stream = File.OpenRead(args[0]))
                tree = new BinTree(stream);
            IReadOnlyList<MapPlaceableChunkData> placeables = new MapPlaceableParser().Parse(tree);
            IReadOnlyList<MapCharacterData> characters = new MapCharacterParser().Parse(placeables);
            var scene = new MapSceneData(null, null, null, tree, null, null, placeables, characters, null, null);

            foreach (MapCharacterSpawnSemantics.Camp camp in MapCharacterSpawnSemantics.Camps(scene))
                Console.WriteLine($"[Spawn] camp {camp.Name,-22} at {camp.Position} variants={string.Join(",", camp.Variants)}");
            foreach (string character in characters.Select(item => MapCharacterSpawnSemantics.CharacterOfSkin(item.Skin))
                         .Where(name => name != null).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                var spawn = MapCharacterSpawnSemantics.SpawnTransform(scene, character);
                string yaw = spawn is { } transform ? $"{Math.Atan2(transform.M31, transform.M33) * 180 / Math.PI:0}°" : "-";
                Console.WriteLine($"[Spawn] {character,-32} -> {(spawn?.Translation.ToString() ?? "none")} yaw={yaw}");
            }
        }
    }
}
