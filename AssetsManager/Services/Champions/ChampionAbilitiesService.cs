using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Champions;

namespace AssetsManager.Services.Champions
{
    public class ChampionAbilitiesService
    {
        private readonly HashResolverService _hashResolverService;
        private readonly LogService _logService;
        private readonly ChampionCalculationService _calcService;

        public ChampionAbilitiesService(
            HashResolverService hashResolverService, 
            LogService logService,
            ChampionCalculationService calcService)
        {
            _hashResolverService = hashResolverService;
            _logService = logService;
            _calcService = calcService;
        }

        public void ExtractAbilities(ChampionInfo info, BinTree binTree, BinTreeObject rootObj, string champName, WadFile wad, Func<string, string> resolveString)
        {
            if (champName.Equals("Hwei", StringComparison.OrdinalIgnoreCase))
            {
                ParseHweiAbilities(info, binTree, wad, resolveString);
            }
            else if (champName.Equals("Aphelios", StringComparison.OrdinalIgnoreCase))
            {
                ParseApheliosAbilities(info, binTree, wad, resolveString);
            }
            else
            {
                // Passive extraction (Deep Crawler)
                string passivePath = ChampionBinService.GetPropValue<string>(rootObj, "mCharacterPassiveSpell");
                if (string.IsNullOrEmpty(passivePath))
                {
                    var pObj = binTree.Objects.Values.FirstOrDefault(o => _hashResolverService.ResolveBinHashGeneral(o.ClassHash) == "AbilityObject" && 
                               _hashResolverService.ResolveHash(o.PathHash).Contains("Passive", StringComparison.OrdinalIgnoreCase));
                    if (pObj != null) passivePath = _hashResolverService.ResolveHash(pObj.PathHash);
                }
                if (!string.IsNullOrEmpty(passivePath)) info.Passive = DeepParseAbility(binTree, passivePath, "P", wad, resolveString);

                // Normal Abilities QWER
                var abilityPaths = ChampionBinService.GetPropValue<List<string>>(rootObj, "mAbilities");
                if (abilityPaths != null)
                {
                    string[] keys = { "Q", "W", "E", "R" };
                    int kIdx = 0;
                    foreach (var path in abilityPaths)
                    {
                        var ability = DeepParseAbility(binTree, path, "", wad, resolveString);
                        if (ability != null)
                        {
                            if (path.Contains("Passive", StringComparison.OrdinalIgnoreCase))
                            {
                                if (info.Passive == null) { ability.Key = "P"; info.Passive = ability; }
                            }
                            else if (kIdx < 4)
                            {
                                ability.Key = keys[kIdx++];
                                info.Abilities.Add(ability);
                            }
                        }
                    }
                }

                // Fallback to spellNames
                if (info.Abilities.Count == 0)
                {
                    var spells = ChampionBinService.GetPropValue<List<string>>(rootObj, "spellNames");
                    if (spells != null)
                    {
                        string[] keys = { "Q", "W", "E", "R" };
                        for (int i = 0; i < Math.Min(spells.Count, 4); i++)
                        {
                            var ab = DeepParseAbility(binTree, $"Characters/{champName}/Spells/{spells[i]}", keys[i], wad, resolveString);
                            if (ab != null) info.Abilities.Add(ab);
                        }
                    }
                }
            }
        }

        public AbilityInfo DeepParseAbility(BinTree binTree, string path, string key, WadFile wad, Func<string, string> resolveString)
        {
            if (string.IsNullOrEmpty(path)) return null;

            uint objHash;
            if (path.StartsWith("{") && path.EndsWith("}")) 
                objHash = uint.Parse(path.Trim('{', '}'), System.Globalization.NumberStyles.HexNumber);
            else 
                objHash = LeagueToolkit.Hashing.Fnv1a.HashLower(path);

            if (!binTree.Objects.TryGetValue(objHash, out var obj)) return null;

            string cls = _hashResolverService.ResolveBinHashGeneral(obj.ClassHash);
            if (cls == "AbilityObject")
            {
                string rootSpell = ChampionBinService.GetPropValue<string>(obj, "mRootSpell");
                if (!string.IsNullOrEmpty(rootSpell)) return DeepParseAbility(binTree, rootSpell, key, wad, resolveString);
            }
            if (cls == "SpellObject")
            {
                var spell = ChampionBinService.GetPropValue<BinTreeStruct>(obj, "mSpell");
                if (spell == null) return null;
                
                var info = new AbilityInfo { Key = key };
                
                var icons = ChampionBinService.GetPropValue<List<string>>(spell, "mImgIconName");
                if (icons != null && icons.Count > 0)
                {
                    info.IconPath = icons[0];
                    if (wad != null) info.IconSource = LoadTextureFromWad(wad, info.IconPath, 48, 48);
                }
                
                info.CastTime = ChampionBinService.GetPropValue<float>(spell, "mCastTime");
                info.Cooldown = ChampionBinService.GetPropValue<List<float>>(spell, "cooldownTime");
                info.Cost = ChampionBinService.GetPropValue<List<float>>(spell, "mana");
                info.CastRange = ChampionBinService.GetPropValue<float>(spell, "castRange");
                info.CastRadius = ChampionBinService.GetPropValue<float>(spell, "castRadius");
                
                var targetData = ChampionBinService.GetPropValue<BinTreeStruct>(spell, "mTargetingTypeData");
                if (targetData != null) info.TargetingType = _hashResolverService.ResolveBinHashGeneral(targetData.ClassHash);

                // DataValues (mDataValues)
                var dataValues = ChampionBinService.GetPropValue<List<BinTreeProperty>>(spell, "mDataValues");
                if (dataValues != null)
                {
                    foreach (var dv in dataValues.OfType<BinTreeStruct>())
                    {
                        string dvName = ChampionBinService.GetPropValue<string>(dv, "mName");
                        var values = ChampionBinService.GetPropValue<List<float>>(dv, "mValues");
                        if (!string.IsNullOrEmpty(dvName) && values != null)
                        {
                            var filtered = values.Skip(1).Take(5).ToList();
                            if (filtered.Count > 0)
                            {
                                if (filtered.All(v => v == filtered[0])) info.DataValues[dvName] = filtered[0].ToString("0.##");
                                else info.DataValues[dvName] = string.Join("/", filtered.Select(v => v.ToString("0.##")));
                            }
                        }
                    }
                }

                // Calculations (mSpellCalculations)
                var calculations = ChampionBinService.GetPropValue<BinTreeStruct>(spell, "mSpellCalculations");

                // Localization logic (mClientData -> mLocKeys)
                var clientData = ChampionBinService.GetPropValue<BinTreeStruct>(spell, "mClientData");
                if (clientData != null)
                {
                    var tooltipData = ChampionBinService.GetPropValue<BinTreeStruct>(clientData, "mTooltipData");
                    if (tooltipData != null)
                    {
                        var locKeys = ChampionBinService.GetPropValue<BinTreeStruct>(tooltipData, "mLocKeys");
                        if (locKeys != null)
                        {
                            info.Name = resolveString(ChampionBinService.GetPropValue<string>(locKeys, "keyName"));
                            info.Summary = resolveString(ChampionBinService.GetPropValue<string>(locKeys, "keySummary"));
                            info.Description = resolveString(ChampionBinService.GetPropValue<string>(locKeys, "keyTooltip"));
                            info.TooltipExtended = resolveString(ChampionBinService.GetPropValue<string>(locKeys, "keyTooltipExtended") ?? ChampionBinService.GetPropValue<string>(locKeys, "keyTooltipExtendedBelowLine"));
                        }
                    }
                }
                
                if (string.IsNullOrEmpty(info.Name)) info.Name = resolveString(ChampionBinService.GetPropValue<string>(spell, "mDisplayName"));
                if (string.IsNullOrEmpty(info.Description)) info.Description = resolveString(ChampionBinService.GetPropValue<string>(spell, "mDescription"));

                // Dynamic Replacement Engine
                info.Description = _calcService.ReplaceTooltipValues(info.Description, info.DataValues, calculations, resolveString);
                info.Summary = _calcService.ReplaceTooltipValues(info.Summary, info.DataValues, calculations, resolveString);
                info.TooltipExtended = _calcService.ReplaceTooltipValues(info.TooltipExtended, info.DataValues, calculations, resolveString);

                return info;
            }
            return null;
        }

        private void ParseHweiAbilities(ChampionInfo info, BinTree binTree, WadFile wad, Func<string, string> resolveString)
        {
            string[] keys = { "QQ", "QW", "QE", "WQ", "WW", "WE", "EQ", "EW", "EE", "R" };
            foreach (var k in keys) {
                string path = $"Characters/Hwei/Spells/Hwei{(k == "R" ? "R" : k[0].ToString())}Ability/Hwei{k}";
                var ab = DeepParseAbility(binTree, path, k, wad, resolveString);
                if (ab != null) info.Abilities.Add(ab);
            }
        }

        private void ParseApheliosAbilities(ChampionInfo info, BinTree binTree, WadFile wad, Func<string, string> resolveString)
        {
            var apheliosSpells = new Dictionary<string, string> { 
                { "SeverumQ", "Characters/Aphelios/Spells/ApheliosSeverumQAbility/ApheliosSeverumQ" }, 
                { "InfernumQ", "Characters/Aphelios/Spells/ApheliosInfernumQAbility/ApheliosInfernumQ" }, 
                { "GravitumQ", "Characters/Aphelios/Spells/ApheliosGravitumQAbility/ApheliosGravitumQ" }, 
                { "CalibrumQ", "Characters/Aphelios/Spells/ApheliosCalibrumQAbility/ApheliosCalibrumQ" },
                { "W", "Characters/Aphelios/Spells/ApheliosWAbility/ApheliosW" }, { "R", "Characters/Aphelios/Spells/ApheliosRAbility/ApheliosR" }
            };
            foreach (var kvp in apheliosSpells) {
                var ab = DeepParseAbility(binTree, kvp.Value, kvp.Key, wad, resolveString);
                if (ab != null) info.Abilities.Add(ab);
            }
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
    }
}
