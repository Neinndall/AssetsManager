using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Champions
{
    public class ChampionCalculationService
    {
        public string ReplaceTooltipValues(string text, Dictionary<string, string> dataValues, BinTreeStruct calculations, Func<string, string> resolveString)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // Follows the complex LoL pattern: @Key@, @Key*multiplier@, @CalculationName@
            return Regex.Replace(text, @"@(\w+?)(\*[-\d\.]+)?@(%?)", m =>
            {
                string key = m.Groups[1].Value;
                string multiplierStr = m.Groups[2].Value;
                string percentSign = m.Groups[3].Value;

                // 1. Try DataValues (Direct numbers)
                if (dataValues.TryGetValue(key, out var val))
                {
                    return FormatWithMultiplier(val, multiplierStr, percentSign);
                }

                // 2. Try mSpellCalculations (Complex formulas)
                if (calculations != null)
                {
                    var calc = ChampionBinService.GetPropValue<BinTreeStruct>(calculations, key);
                    if (calc == null)
                    {
                        // Try technical hash (like {46898f30} from user example)
                        calc = calculations.Properties.Values.OfType<BinTreeStruct>()
                            .FirstOrDefault(s => s.Properties.Values.Any(p => p is BinTreeString str && str.Value == key));
                    }

                    if (calc != null)
                    {
                        return ResolveCalculation(calc, dataValues) + percentSign;
                    }
                }

                return m.Value;
            });
        }

        private string ResolveCalculation(BinTreeStruct calc, Dictionary<string, string> dataValues)
        {
            // Simplified port of champs.py GameCalculation logic
            var parts = ChampionBinService.GetPropValue<List<BinTreeProperty>>(calc, "mFormulaParts");
            if (parts == null || parts.Count == 0) return "0";

            // For now, take the first part logic
            var part = parts[0] as BinTreeStruct;
            if (part == null) return "0";

            string type = part.Properties.Values.OfType<BinTreeString>().FirstOrDefault()?.Value ?? "";
            
            if (type.Contains("ByCharLevelInterpolation"))
            {
                float start = ChampionBinService.GetPropValue<float>(part, "mStartValue");
                float end = ChampionBinService.GetPropValue<float>(part, "mEndValue");
                return $"{start*100:0.##}% - {end*100:0.##}%";
            }

            if (type.Contains("NamedDataValue"))
            {
                string dvName = ChampionBinService.GetPropValue<string>(part, "mDataValue");
                if (dataValues.TryGetValue(dvName, out var val)) return val;
            }

            return "0";
        }

        private string FormatWithMultiplier(string val, string multStr, string percent)
        {
            if (string.IsNullOrEmpty(multStr)) return val + percent;
            try
            {
                float mult = float.Parse(multStr.Replace("*", ""), System.Globalization.CultureInfo.InvariantCulture);
                var parts = val.Split('/').Select(p => 
                {
                    if (float.TryParse(p, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float pVal))
                        return (pVal * mult).ToString("0.##");
                    return p;
                });
                return string.Join("/", parts) + percent;
            }
            catch { return val + percent; }
        }
    }
}
