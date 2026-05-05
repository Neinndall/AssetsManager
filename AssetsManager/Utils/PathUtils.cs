using System;
using System.IO;
using System.Linq;

namespace AssetsManager.Utils
{
    public static class PathUtils
    {
        public static string SanitizeName(string name)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new string(name.Where(c => !invalidChars.Contains(c)).ToArray()).Trim();

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
    }
}
