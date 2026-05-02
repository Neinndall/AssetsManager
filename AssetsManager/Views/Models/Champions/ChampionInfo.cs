using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Imaging;

namespace AssetsManager.Views.Models.Champions
{
    public class ChampionInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Lore { get; set; }
        public string IconPath { get; set; }
        public BitmapSource IconSource { get; set; }
        
        // General Info
        public int ChampionId { get; set; }
        public string Roles { get; set; }
        public string SearchTags { get; set; }
        public int DifficultyRank { get; set; }
        public int AttackRank { get; set; }
        public int DefenseRank { get; set; }
        public int MagicRank { get; set; }

        // Health Stats
        public float BaseHP { get; set; }
        public float HPPerLevel { get; set; }
        public float BaseHPRegen { get; set; }
        public float HPRegenPerLevel { get; set; }
        
        // Resources
        public ResourceInfo PrimaryResource { get; set; }
        public ResourceInfo SecondaryResource { get; set; }
        
        // Combat Stats (Offensive)
        public float BaseDamage { get; set; }
        public float DamagePerLevel { get; set; }
        public float BaseAttackSpeed { get; set; }
        public float AttackSpeedRatio { get; set; }
        public float AttackSpeedPerLevel { get; set; }
        public float AttackRange { get; set; }
        public float CritDamage { get; set; }
        public float CritChance { get; set; }
        public float AdaptiveForceToAPWeight { get; set; }

        // Combat Stats (Defensive)
        public float BaseArmor { get; set; }
        public float ArmorPerLevel { get; set; }
        public float BaseMR { get; set; }
        public float MRPerLevel { get; set; }
        
        // Utility Stats
        public float BaseMoveSpeed { get; set; }
        public float AcquisitionRange { get; set; }

        // Technical Collision
        public float SelectionHeight { get; set; }
        public float SelectionRadius { get; set; }
        public float PathfindingCollisionRadius { get; set; }

        public AbilityInfo Passive { get; set; }
        public List<AbilityInfo> Abilities { get; set; } = new List<AbilityInfo>();
        
        public Dictionary<string, AbilityInfo> ExtraAbilities { get; set; } = new Dictionary<string, AbilityInfo>();
    }

    public class ResourceInfo
    {
        public string TypeName { get; set; }
        public float BaseValue { get; set; }
        public float ValuePerLevel { get; set; }
        public float BaseRegen { get; set; }
        public float RegenPerLevel { get; set; }
        public bool IsShown { get; set; } = true;
        public bool DisplayAsPips { get; set; }
    }

    public class AbilityInfo
    {
        public string Name { get; set; }
        public string Summary { get; set; }
        public string Tooltip { get; set; }
        public string TooltipExtended { get; set; }
        public string Description { get; set; }
        public string IconPath { get; set; }
        public BitmapSource IconSource { get; set; }
        public string Key { get; set; }
        
        public float? CastTime { get; set; }
        public List<float> Cooldown { get; set; }
        public List<float> Cost { get; set; }
        public float? CastRange { get; set; }
        public float? CastRadius { get; set; }
        public string TargetingType { get; set; }
        
        public Dictionary<string, string> DataValues { get; set; } = new Dictionary<string, string>();

        // Display Helpers
        public string CooldownDisplay => Cooldown != null && Cooldown.Any(v => v > 0) ? string.Join("/", Cooldown.Select(v => v.ToString("0.##"))) : null;
        public string CostDisplay => Cost != null && Cost.Any(v => v > 0) ? string.Join("/", Cost.Select(v => v.ToString("0.##"))) : null;
    }
}
