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

        public static string FormatSize(long sizeInBytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int counter = 0;
            decimal number = (decimal)sizeInBytes;
            while (Math.Round(number / 1024) >= 1 && counter < suffixes.Length - 1)
            {
                number = number / 1024;
                counter++;
            }
            return string.Format("{0:n1} {1}", number, suffixes[counter]);
        }
    }
}
