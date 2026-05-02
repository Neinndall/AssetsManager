using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Wad;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Champions;

namespace AssetsManager.Services.Champions
{
    public class ChampionDataService
    {
        private readonly LogService _logService;
        private readonly HashResolverService _hashResolverService;
        private readonly AppSettings _appSettings;
        private readonly ChampionInfoService _infoService;
        private readonly ChampionAbilitiesService _abilitiesService;
        private Dictionary<string, string> _stringTable;

        public ChampionDataService(
            LogService logService, 
            HashResolverService hashResolverService, 
            AppSettings appSettings,
            ChampionInfoService infoService,
            ChampionAbilitiesService abilitiesService)
        {
            _logService = logService;
            _hashResolverService = hashResolverService;
            _appSettings = appSettings;
            _infoService = infoService;
            _abilitiesService = abilitiesService;
        }

        public async Task LoadStringTableAsync()
        {
            if (_stringTable != null) return;
            try
            {
                string lolPath = _appSettings.LolPbeDirectory ?? _appSettings.LolLiveDirectory;
                if (string.IsNullOrEmpty(lolPath)) return;
                string localizedDir = Path.Combine(lolPath, "Game", "DATA", "FINAL", "Localized");
                if (!Directory.Exists(localizedDir)) return;
                string globalWad = Directory.GetFiles(localizedDir, "Global.*.wad.client").FirstOrDefault();
                if (globalWad == null) return;

                await Task.Run(() =>
                {
                    using var wad = new WadFile(globalWad);
                    var stringTableChunk = wad.Chunks.Values.Cast<WadChunk?>().FirstOrDefault(c => 
                        _hashResolverService.ResolveHash(c.Value.PathHash).EndsWith("lol.stringtable", StringComparison.OrdinalIgnoreCase));

                    if (stringTableChunk != null)
                    {
                        using var ms = new MemoryStream();
                        using var decompressed = wad.LoadChunkDecompressed(stringTableChunk.Value);
                        ms.Write(decompressed.Span);
                        ms.Position = 0;
                        _stringTable = StringTableUtils.ResolveStringTable(ms, _hashResolverService);
                    }
                });
            }
            catch (Exception ex) { _logService.LogError(ex, "Failed to load string table."); }
        }

        public async Task<List<ChampionInfo>> GetChampionsAsync()
        {
            await LoadStringTableAsync();
            var champions = new List<ChampionInfo>();
            string lolPath = _appSettings.LolPbeDirectory ?? _appSettings.LolLiveDirectory;
            if (string.IsNullOrEmpty(lolPath)) return champions;
            string championsDir = Path.Combine(lolPath, "Game", "DATA", "FINAL", "Champions");
            if (!Directory.Exists(championsDir)) return champions;

            foreach (var wadPath in Directory.GetFiles(championsDir, "*.wad.client"))
            {
                try
                {
                    var champ = await Task.Run(() => ParseChampionWad(wadPath));
                    if (champ != null) champions.Add(champ);
                }
                catch (Exception ex) { _logService.LogWarning($"Failed {Path.GetFileName(wadPath)}: {ex.Message}"); }
            }
            return champions.OrderBy(c => c.Name).ToList();
        }

        private ChampionInfo ParseChampionWad(string wadPath)
        {
            using var wad = new WadFile(wadPath);
            string champName = Path.GetFileNameWithoutExtension(wadPath).Replace(".wad", "");
            var binChunk = wad.Chunks.Values.Cast<WadChunk?>().FirstOrDefault(c => 
                _hashResolverService.ResolveHash(c.Value.PathHash).EndsWith($"{champName.ToLowerInvariant()}.bin", StringComparison.OrdinalIgnoreCase));

            if (binChunk == null) return null;
            using var decompressed = wad.LoadChunkDecompressed(binChunk.Value);
            using var ms = new MemoryStream();
            ms.Write(decompressed.Span);
            ms.Position = 0;
            var binTree = new BinTree(ms);
            
            var rootObj = binTree.Objects.Values.FirstOrDefault(o => 
                _hashResolverService.ResolveBinHashGeneral(o.ClassHash).Equals("CharacterRecord", StringComparison.OrdinalIgnoreCase));
            if (rootObj == null) return null;

            var info = new ChampionInfo { Id = champName };

            // 1. Info & Stats (Using InfoService)
            _infoService.ExtractBasicInfo(info, rootObj, champName);
            info.PrimaryResource = _infoService.ParseResource(rootObj, "primaryAbilityResource");
            info.SecondaryResource = _infoService.ParseResource(rootObj, "secondaryAbilityResource");
            _infoService.ExtractMetaAndLore(info, rootObj, champName, ResolveString);

            // 2. Abilities (Using AbilitiesService)
            _abilitiesService.ExtractAbilities(info, binTree, rootObj, champName, wad, ResolveString);

            // 3. Icon Handling (Orchestrated here as it needs WadFile)
            string[] iconPatterns = {
                $"assets/characters/{champName.ToLowerInvariant()}/hud/{champName.ToLowerInvariant()}_circle.tex",
                $"assets/characters/{champName.ToLowerInvariant()}/hud/{champName.ToLowerInvariant()}_circle_0.tex"
            };
            foreach (var path in iconPatterns) { 
                info.IconSource = LoadTextureFromWad(wad, path, 64, 64); 
                if (info.IconSource != null) break; 
            }

            // Final name resolution
            info.Name = ResolveString(info.Name);
            
            return info;
        }

        private BitmapSource LoadTextureFromWad(WadFile wad, string path, int width, int height)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try {
                var chunk = wad.FindChunk(path.ToLowerInvariant());
                using var decompressed = wad.LoadChunkDecompressed(chunk);
                using var ms = new MemoryStream();
                ms.Write(decompressed.Span);
                ms.Position = 0;
                return TextureUtils.LoadTexture(ms, Path.GetExtension(path), width, height);
            } catch { return null; }
        }

        private string ResolveString(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            string lk = key.StartsWith("@") ? key.Substring(1) : key;
            if (_stringTable != null)
            {
                if (_stringTable.TryGetValue(lk, out var r)) return Clean(r);
                if (_stringTable.TryGetValue(lk.ToLowerInvariant(), out var rl)) return Clean(rl);
            }
            return key;
        }

        private string Clean(string s) => s.Replace("<br>", "\n").Replace("<br/>", "\n")
            .Replace("<b>", "").Replace("</b>", "").Replace("<i>", "").Replace("</i>", "")
            .Replace("<font color='#FFFFFF'>", "").Replace("<font color='#cccccc'>", "").Replace("<font color='#ff6666'>", "")
            .Replace("<font color='#EE9400'>", "").Replace("<font color='#FFCC33'>", "").Replace("</font>", "").Replace("  ", " ");
    }
}
