using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AssetsManager.Utils
{
    public static class PathUtils
    {
        /// <summary>
        /// Normalizes a Riot asset URL to a relative WAD path.
        /// e.g. "/lol-game-data/assets/ASSETS/Images/icon.png" -> "plugins/rcp-be-lol-game-data/global/default/assets/ASSETS/Images/icon.png"
        /// </summary>
        public static string NormalizeRiotIconPath(string url)
        {
            if (string.IsNullOrEmpty(url)) return string.Empty;

            string normalizedUrl = url.ToLowerInvariant();

            int assetsIndex = normalizedUrl.IndexOf("assets/", StringComparison.Ordinal);
            string basePath = assetsIndex >= 0 ? normalizedUrl.Substring(assetsIndex + 7) : normalizedUrl;

            // La raíz virtual /lol-game-data/assets/ mapea directamente a plugins/rcp-be-lol-game-data/global/default/
            // No debemos forzar un segmento "assets/" adicional ya que muchas rutas (como v1/...) están en la raíz.
            return $"plugins/rcp-be-lol-game-data/global/default/{basePath}";
        }

        /// <summary>
        /// Cleans a Riot skin or item name by removing parenthetical suffixes.
        /// e.g. "Crime City Nightmare Shaco (Underground)" -> "Crime City Nightmare Shaco"
        /// </summary>
        public static string CleanRiotName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;

            int bracketIndex = name.IndexOf('(');
            if (bracketIndex > 0)
            {
                return name.Substring(0, bracketIndex).Trim();
            }

            return name;
        }

        /// <summary>
        /// Cleans technical Riot suffixes from a Pass Event name.
        /// e.g. "2026_Season2_Act2_Pass_reward_track" -> "2026 Season 2 Act 2"
        /// </summary>
        public static string CleanPassName(string name, bool forUI = false)
        {
            if (string.IsNullOrEmpty(name)) return "Unknown_Pass";

            string clean = name.Replace("_Pass_reward_track", "")
                               .Replace("_reward_track", "")
                               .Replace("_Pass", "");

            if (forUI)
            {
                return clean.Replace("_", " ").Trim();
            }

            return clean;
        }

        private static readonly System.Collections.Generic.HashSet<char> InvalidFileNameChars = 
            new System.Collections.Generic.HashSet<char>(Path.GetInvalidFileNameChars());

        public static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;

            bool hasInvalid = false;
            for (int i = 0; i < name.Length; i++)
            {
                if (InvalidFileNameChars.Contains(name[i]))
                {
                    hasInvalid = true;
                    break;
                }
            }

            string sanitized;
            if (!hasInvalid)
            {
                sanitized = name.Trim();
            }
            else
            {
                var sb = new System.Text.StringBuilder(name.Length);
                for (int i = 0; i < name.Length; i++)
                {
                    char c = name[i];
                    if (!InvalidFileNameChars.Contains(c))
                    {
                        sb.Append(c);
                    }
                }
                sanitized = sb.ToString().Trim();
            }

            const int MaxLength = 240; // A bit less than 255 to be safe.
            if (sanitized.Length > MaxLength)
            {
                var extension = Path.GetExtension(sanitized);
                var newLength = MaxLength - extension.Length;
                sanitized = sanitized.Substring(0, newLength) + extension;
            }
            return sanitized;
        }

        public static string GetLogName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;

            string cleanName = name;
            // Clean count suffix if present (e.g., "file.wad (11)" -> "file.wad")
            int parenthesisIndex = cleanName.LastIndexOf(" (");
            if (parenthesisIndex > 0)
            {
                string potentialNumber = cleanName.Substring(parenthesisIndex + 2);
                if (potentialNumber.Length > 1 && potentialNumber.EndsWith(")") && int.TryParse(potentialNumber.Substring(0, potentialNumber.Length - 1), out _))
                {
                    cleanName = cleanName.Substring(0, parenthesisIndex).Trim();
                }
            }

            // If it's a path (backup grouped mode), take only the filename
            if (cleanName.Contains("\\") || cleanName.Contains("/"))
            {
                cleanName = Path.GetFileName(cleanName);
            }

            return cleanName;
        }

        public static string GetUniqueFilePath(string destinationDirectory, string fileName)
        {
            string sanitizedFileName = SanitizeName(fileName); // This now calls the local static method
            string filePath = Path.Combine(destinationDirectory, sanitizedFileName);

            if (!File.Exists(filePath))
            {
                return filePath;
            }

            string fileNameWithoutExt = Path.GetFileNameWithoutExtension(sanitizedFileName);
            string fileExt = Path.GetExtension(sanitizedFileName);
            int counter = 1;

            while (true)
            {
                string newFileName = $"{fileNameWithoutExt} ({counter}){fileExt}";
                string newFilePath = Path.Combine(destinationDirectory, newFileName);
                if (!File.Exists(newFilePath))
                {
                    return newFilePath;
                }
                counter++;
            }
        }
        
        /// <summary>
        /// Generates a unique local file path from a given URL, preserving the URL's directory structure.
        /// </summary>
        /// <param name="url">The full URL of the JSON file.</param>
        /// <returns>A relative path that can be appended to a base directory, e.g., "pbe/plugins/rcp-fe-lol-clash/global/default/trans.json".</returns>
        public static string GetUniqueLocalPathFromJsonUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return string.Empty;
            }

            try
            {
                Uri uri = new Uri(url);
                // Get the path part of the URL, e.g., "/pbe/plugins/rcp-fe-lol-paw/global/default/trans.json"
                string path = uri.AbsolutePath;

                // Remove leading slash if present
                if (path.StartsWith("/"))
                {
                    path = path.Substring(1);
                }

                // Remove "pbe/plugins/" prefix if it exists at the beginning of the path
                if (path.StartsWith("pbe/plugins/", StringComparison.OrdinalIgnoreCase))
                {
                    path = path.Substring("pbe/plugins/".Length);
                }

                // Define characters that are invalid for *filename segments* but *not* path separators
                // Path.GetInvalidFileNameChars() includes '/' and '\', which we want to preserve as separators.
                // So, we'll manually list common invalid filename characters that are NOT path separators.
                char[] invalidCharsForSegment = new char[] { '"', '<', '>', '|', ':', '*', '?' };

                // Sanitize each segment of the path
                string[] segments = path.Split('/');
                for (int i = 0; i < segments.Length; i++)
                {
                    foreach (char invalidChar in invalidCharsForSegment)
                    {
                        segments[i] = segments[i].Replace(invalidChar, '_');
                    }
                }
                path = string.Join("/", segments);

                return path;
            }
            catch (UriFormatException)
            {
                // Handle invalid URL format, return original or empty string
                return url; // Or throw an exception, or log an error
            }
        }

        public static string TruncateForDisplay(string text, int maxLength = 50)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            {
                return text;
            }
            return text.Substring(0, maxLength) + "...";
        }

        /// <summary>
        /// Robust check to see if a path is the same or a sub-path of another.
        /// </summary>
        public static bool IsSameOrSubPath(string root, string sub)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(sub)) return false;

            try
            {
                string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string s = Path.GetFullPath(sub).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

                return s.StartsWith(r, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// Truncates the given name at the first occurrence of a dot ('.').
        /// e.g. "character_skin01.skins_character" -> "character_skin01"
        /// </summary>
        public static string TruncateAtDot(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            int dotIndex = name.IndexOf('.');
            return dotIndex == -1 ? name : name.Substring(0, dotIndex);
        }

        /// <summary>
        /// Simplifies a full mesh/material identifier by taking only the last segment of the path.
        /// e.g. "Maps/KitPieces/TFT/Materials/Base/VertexAnimation/Bush_Wind_A" -> "Bush_Wind_A"
        /// </summary>
        public static string SimplifyMeshName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return "Default";

            // Split by both types of slashes and take the last non-empty part
            string[] parts = fullName.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts.Last() : fullName;
        }

        /// <summary>
        /// Normalizes a path by replacing backslashes with forward slashes and converting to lowercase.
        /// Intended for internal WAD virtual paths (e.g. "plugins/rcp-be-lol-game-data/...").
        /// </summary>
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            return path.Replace('\\', '/').ToLowerInvariant();
        }

        /// <summary>
        /// Normalizes a physical file system path (e.g. "C:\Riot Games\League of Legends").
        /// Resolves "." / ".." segments, mixed separators, redundant slashes and the long-path
        /// prefix via <see cref="Path.GetFullPath"/> (which on Windows returns the canonical
        /// "\" separator), then lowercases and trims trailing separators so two equivalent
        /// installation paths always produce the same key.
        /// </summary>
        public static string NormalizePhysicalPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;

            try
            {
                string absolute = Path.GetFullPath(path);
                return absolute.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                               .ToLowerInvariant();
            }
            catch
            {
                return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             .ToLowerInvariant();
            }
        }

        /// <summary>
        /// Converts a physical or mixed path into a clean virtual path for WAD lookups.
        /// </summary>
        public static string ToVirtualPath(string path)
        {
            return NormalizePath(path).TrimStart('/');
        }

        /// <summary>
        /// Builds the canonical identity key used to deduplicate WAD comparisons:
        /// normalized Version + OldPath + NewPath. Returns null if paths are missing.
        /// </summary>
        public static string BuildComparisonKey(string version, string oldPath, string newPath)
        {
            if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath)) return null;

            string normalizedVersion = string.IsNullOrWhiteSpace(version) ? "unknown" : version.Trim().ToLowerInvariant();
            string normalizedOld = NormalizePhysicalPath(oldPath);
            string normalizedNew = NormalizePhysicalPath(newPath);

            return $"{normalizedVersion}|{normalizedOld}|{normalizedNew}";
        }
    }
}
