using System;

namespace AssetsManager.Utils
{
    public static class FormatUtils
    {
        public static string FormatTimeRemaining(DateTime endTime)
        {
            var remaining = endTime.ToLocalTime() - DateTime.Now;

            if (remaining.TotalSeconds <= 0)
            {
                return "Expired";
            }
            if (remaining.TotalDays >= 1)
            {
                return $"Expires in {remaining.Days}d {remaining.Hours}h";
            }
            if (remaining.TotalHours >= 1)
            {
                return $"Expires in {remaining.Hours}h {remaining.Minutes}m";
            }
            return $"Expires in {remaining.Minutes}m";
        }
    }
}
