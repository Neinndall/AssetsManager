using System;
using System.Globalization;

namespace AssetsManager.Utils
{
    public static class FormatUtils
    {
        public static DateTime ParseDate(string value, string format)
        {
            return DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : DateTime.MinValue;
        }

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

        private static readonly string[] Suffixes = { "B", "KB", "MB", "GB", "TB" };

        public static string FormatSize(long sizeInBytes)
        {
            int counter = 0;
            decimal number = (decimal)sizeInBytes;
            while (Math.Round(number / 1024) >= 1 && counter < Suffixes.Length - 1)
            {
                number = number / 1024;
                counter++;
            }
            return string.Format("{0:n1} {1}", number, Suffixes[counter]);
        }
    }
}
