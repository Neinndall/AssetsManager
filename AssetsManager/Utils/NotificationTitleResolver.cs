using System;
using AssetsManager.Views.Models.News;
using AssetsManager.Views.Models.Notifications;

namespace AssetsManager.Utils
{
    public static class NotificationTitleResolver
    {
        /// <summary>
        /// Resolves a concise, adaptive title for Toast notifications and internal alerts
        /// based on the notification category, explicit title, message content, and news metadata.
        /// </summary>
        public static string ResolveToastTitle(
            string message,
            NotificationCategory category,
            string explicitTitle,
            NewsItemModel newsItem = null)
        {
            // 1. Tray Minimization / System Tray
            if ((!string.IsNullOrEmpty(message) && message.Contains("minimized to the tray", StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(explicitTitle, "System Tray", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(explicitTitle, "System", StringComparison.OrdinalIgnoreCase))
            {
                return "System";
            }

            // 2. PBE Status / Maintenance
            if (string.Equals(explicitTitle, "PBE Status Update", StringComparison.OrdinalIgnoreCase) ||
                (category == NotificationCategory.System && !string.IsNullOrEmpty(message) && message.Contains("PBE", StringComparison.OrdinalIgnoreCase)))
            {
                if (!string.IsNullOrEmpty(message))
                {
                    if (message.Contains("Maintenance ended", StringComparison.OrdinalIgnoreCase) ||
                        message.Contains("ONLINE", StringComparison.OrdinalIgnoreCase))
                    {
                        return "PBE Server Online";
                    }

                    if (message.Contains("maintenance", StringComparison.OrdinalIgnoreCase) ||
                        message.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
                        message.Contains("undergoing", StringComparison.OrdinalIgnoreCase))
                    {
                        return "PBE Maintenance";
                    }
                }

                return "PBE Status";
            }

            // 3. Asset Tracker (CDN discoveries)
            if (category == NotificationCategory.Tracker ||
                string.Equals(explicitTitle, "Asset Tracker Discovery", StringComparison.OrdinalIgnoreCase))
            {
                return "Asset Tracker";
            }

            // 4. Asset Watcher (Monitored local assets)
            if (category == NotificationCategory.Watcher ||
                string.Equals(explicitTitle, "Monitored Assets Updated", StringComparison.OrdinalIgnoreCase))
            {
                return "Asset Watcher";
            }

            // 5. Updates (App version or Hashes)
            if (category == NotificationCategory.Updates)
            {
                if (string.Equals(explicitTitle, "Hash Update", StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(message) && message.Contains("hashes", StringComparison.OrdinalIgnoreCase)))
                {
                    return "Hash Update";
                }

                if (string.Equals(explicitTitle, "App Update Available", StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(message) && message.Contains("version", StringComparison.OrdinalIgnoreCase)))
                {
                    return "App Update";
                }

                return "Software Update";
            }

            // 6. News
            if (category == NotificationCategory.News)
            {
                if (newsItem != null && !string.IsNullOrWhiteSpace(newsItem.CategoryTitle))
                {
                    return $"Riot News • {newsItem.CategoryTitle}";
                }

                return "Riot News";
            }

            // 7. Explicit Title Fallback (if non-empty and non-generic)
            if (!string.IsNullOrWhiteSpace(explicitTitle) &&
                !string.Equals(explicitTitle, "System Notification", StringComparison.OrdinalIgnoreCase))
            {
                return explicitTitle;
            }

            // Default fallback
            return "AssetsManager";
        }
    }
}
