using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Champions;

namespace AssetsManager.Services.Champions
{
    public class ChampionInfoService
    {
        private readonly HashResolverService _hashResolverService;
        private readonly LogService _logService;

        public ChampionInfoService(HashResolverService hashResolverService, LogService logService)
        {
            _hashResolverService = hashResolverService;
            _logService = logService;
        }

        public void ExtractBasicInfo(ChampionInfo info, BinTreeObject rootObj, string champName)
        {
            // Name resolution
            string rawName = ChampionBinService.GetPropValue<string>(rootObj, "mCharacterName");
            info.Name = string.IsNullOrEmpty(rawName) ? champName : rawName;

            // Stats Parity with champs.py and provided Ahri example
            info.BaseHP = ChampionBinService.GetStatValue(rootObj, "baseHP");
            info.HPPerLevel = ChampionBinService.GetStatValue(rootObj, "hpPerLevel");
            info.BaseHPRegen = ChampionBinService.GetStatValue(rootObj, "baseStaticHPRegen");
            info.HPRegenPerLevel = ChampionBinService.GetStatValue(rootObj, "hpRegenPerLevel");

            info.BaseDamage = ChampionBinService.GetStatValue(rootObj, "baseDamage");
            info.DamagePerLevel = ChampionBinService.GetStatValue(rootObj, "damagePerLevel");
            info.BaseArmor = ChampionBinService.GetStatValue(rootObj, "baseArmor");
            info.ArmorPerLevel = ChampionBinService.GetStatValue(rootObj, "armorPerLevel");
            
            // MR and MR per level (with technical fallback from user example)
            info.BaseMR = ChampionBinService.GetStatValue(rootObj, "BaseMR", "baseSpellBlock");
            info.MRPerLevel = ChampionBinService.GetStatValue(rootObj, "01262a25", "spellBlockPerLevel");

            info.BaseMoveSpeed = ChampionBinService.GetStatValue(rootObj, "baseMoveSpeed");
            info.AttackRange = ChampionBinService.GetStatValue(rootObj, "attackRange");
            info.BaseAttackSpeed = ChampionBinService.GetStatValue(rootObj, "attackSpeed");
            info.AttackSpeedRatio = ChampionBinService.GetStatValue(rootObj, "attackSpeedRatio");
            info.AttackSpeedPerLevel = ChampionBinService.GetStatValue(rootObj, "attackSpeedPerLevel");
            
            info.AcquisitionRange = ChampionBinService.GetPropValue<float>(rootObj, "acquisitionRange");
            info.AdaptiveForceToAPWeight = ChampionBinService.GetPropValue<float>(rootObj, "mAdaptiveForceToAbilityPowerWeight");
            info.CritDamage = ChampionBinService.GetPropValue<float>(rootObj, "CritDamageMultiplier", 2.0f);
            info.CritChance = ChampionBinService.GetPropValue<float>(rootObj, "baseCritChance", 0f);

            info.SelectionHeight = ChampionBinService.GetPropValue<float>(rootObj, "selectionHeight");
            info.SelectionRadius = ChampionBinService.GetPropValue<float>(rootObj, "selectionRadius");
            info.PathfindingCollisionRadius = ChampionBinService.GetPropValue<float>(rootObj, "pathfindingCollisionRadius");
        }

        public ResourceInfo ParseResource(BinTreeObject root, string resourceName)
        {
            var resStruct = ChampionBinService.GetPropValue<BinTreeStruct>(root, resourceName);
            if (resStruct == null) return null;

            int arType = ChampionBinService.GetPropValue<int>(resStruct, "arType");
            var info = new ResourceInfo
            {
                TypeName = ResolveResourceType(arType),
                IsShown = ChampionBinService.GetPropValue<bool>(resStruct, "arIsShown", true),
                DisplayAsPips = ChampionBinService.GetPropValue<bool>(resStruct, "arDisplayAsPips")
            };

            // Try specific resource hashes provided in Ahri example
            info.BaseValue = ChampionBinService.GetStatValue(resStruct, "arBase", "726ee5cd");
            info.ValuePerLevel = ChampionBinService.GetStatValue(resStruct, "arPerLevel", "6216bf7b");
            info.BaseRegen = ChampionBinService.GetStatValue(resStruct, "arBaseStaticRegen", "c4ab3550");
            info.RegenPerLevel = ChampionBinService.GetStatValue(resStruct, "arRegenPerLevel", "3a509002");

            return info;
        }

        public void ExtractMetaAndLore(ChampionInfo info, BinTreeObject rootObj, string champName, Func<string, string> resolveString)
        {
            var toolData = ChampionBinService.GetPropValue<BinTreeStruct>(rootObj, "characterToolData");
            if (toolData != null)
            {
                info.ChampionId = ChampionBinService.GetPropValue<int>(toolData, "championId");
                info.Description = resolveString(ChampionBinService.GetPropValue<string>(toolData, "description"));
                info.Roles = ChampionBinService.GetPropValue<string>(toolData, "roles");
                info.SearchTags = ChampionBinService.GetPropValue<string>(toolData, "searchTags");
                info.DifficultyRank = ChampionBinService.GetPropValue<int>(toolData, "difficultyRank");
                info.AttackRank = ChampionBinService.GetPropValue<int>(toolData, "attackRank");
                info.DefenseRank = ChampionBinService.GetPropValue<int>(toolData, "defenseRank");
                info.MagicRank = ChampionBinService.GetPropValue<int>(toolData, "magicRank");

                string[] loreKeys = { 
                    $"game_character_lore_{champName.ToLowerInvariant()}", 
                    $"character_{champName.ToLowerInvariant()}_lore",
                    $"Character_{champName}_Lore"
                };
                foreach (var lk in loreKeys)
                {
                    var l = resolveString(lk);
                    if (l != lk) { info.Lore = l; break; }
                }
            }
        }

        private string ResolveResourceType(int arType) => arType switch {
            0 => "Mana", 1 => "Energy", 2 => "None", 3 => "Shield", 4 => "Battlefury", 5 => "Dragonfury",
            6 => "Rage", 7 => "Heat", 8 => "Gnarfury", 9 => "Ferocity", 10 => "BloodWell", 11 => "Wind",
            12 => "Ammo", 13 => "Moonlight", 14 => "Other", _ => "Resource"
        };
    }
}
