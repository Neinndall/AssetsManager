using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace AssetsManager.Views.Models.News
{
    public class NewsItemModel
    {
        public string Title { get; private set; }
        public string ImageUrl { get; private set; }
        public string Description { get; private set; }
        public DateTime PublishedAt { get; private set; }
        public string CategoryId { get; private set; }
        public string CategoryTitle { get; private set; }
        public string ActionUrl { get; private set; }
        public string ActionType { get; private set; }
        public List<string> Tags { get; private set; } = new();
        public string ProductId { get; private set; }

        public bool HasTags => Tags.Count > 0;
        public bool IsVideo => string.Equals(ActionType, "youtube_video", StringComparison.OrdinalIgnoreCase);
        public bool IsVideoLink => !string.IsNullOrEmpty(ActionUrl) &&
                                   (ActionUrl.Contains("youtube.com/", StringComparison.OrdinalIgnoreCase) ||
                                    ActionUrl.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase));
        public bool HasImage => !string.IsNullOrEmpty(ImageUrl);

        public Brush CategoryBrush => ResolveCategoryBrush(CategoryId);

        public string DescriptionDisplay => string.IsNullOrWhiteSpace(Description) ? "No description available for this article." : Description;

        private static readonly Dictionary<string, string> CategoryBrushKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            ["dev"] = "AccentBlue",
            ["esports"] = "AccentOrange",
            ["game-updates"] = "AccentGreen",
            ["lore"] = "AccentPurple",
            ["media"] = "AccentRed",
            ["patch_notes"] = "AccentYellow",
            ["merch"] = "AccentTeal",
            ["community"] = "AccentBrush"
        };

        private static Brush ResolveCategoryBrush(string categoryId)
        {
            if (!string.IsNullOrEmpty(categoryId) && CategoryBrushKeys.TryGetValue(categoryId, out var key))
            {
                var resource = Application.Current?.TryFindResource(key) as Brush;
                if (resource != null) return resource;
            }
            return Application.Current?.TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue;
        }

        public static NewsItemModel FromJson(JsonElement element)
        {
            if (!element.TryGetProperty("title", out var titleElement) ||
                string.IsNullOrEmpty(titleElement.GetString()))
            {
                return null;
            }

            var model = new NewsItemModel
            {
                Title = titleElement.GetString(),
                ImageUrl = ReadMediaUrl(element),
                Description = ReadDescription(element),
                PublishedAt = ReadPublishedAt(element)
            };

            if (element.TryGetProperty("category", out var category))
            {
                model.CategoryId = ReadString(category, "machineName") ?? "other";
                model.CategoryTitle = ReadString(category, "title");
            }

            if (element.TryGetProperty("action", out var action))
            {
                model.ActionType = ReadString(action, "type");
                if (action.TryGetProperty("payload", out var payload))
                {
                    model.ActionUrl = ResolveActionUrl(ReadString(payload, "url"));
                }
            }

            if (element.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            {
                foreach (var tag in tags.EnumerateArray())
                {
                    var tagName = ReadString(tag, "title");
                    if (!string.IsNullOrEmpty(tagName)) model.Tags.Add(tagName);
                }
            }

            if (element.TryGetProperty("product", out var product))
            {
                model.ProductId = ReadString(product, "machineName");
            }

            return model;
        }

        private static string ReadMediaUrl(JsonElement element)
        {
            string url = null;
            if (element.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Object)
            {
                url = ReadString(media, "url");
            }
            if (string.IsNullOrEmpty(url) && element.TryGetProperty("imageMedia", out var imageMedia) && imageMedia.ValueKind == JsonValueKind.Object)
            {
                url = ReadString(imageMedia, "url");
            }
            if (!string.IsNullOrEmpty(url) && url.StartsWith("//", StringComparison.Ordinal))
            {
                return "https:" + url;
            }
            return url;
        }

        private static string ReadDescription(JsonElement element)
        {
            if (element.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.Object)
            {
                return ReadString(description, "body");
            }
            return null;
        }

        private static DateTime ReadPublishedAt(JsonElement element)
        {
            if (element.TryGetProperty("publishedAt", out var publishedAt) &&
                DateTimeOffset.TryParse(publishedAt.GetString(), out var parsed))
            {
                return parsed.LocalDateTime;
            }
            return DateTime.MinValue;
        }

        private static string ReadString(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
            return null;
        }

        private static string ResolveActionUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (url.StartsWith("//", StringComparison.Ordinal)) return "https:" + url;
            if (url.StartsWith("/", StringComparison.Ordinal)) return "https://www.leagueoflegends.com" + url;
            return url;
        }
    }
}
