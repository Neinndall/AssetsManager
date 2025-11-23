using System;
using System.IO;
using System.Text.RegularExpressions;

namespace AssetsManager.Utils
{
    public static class PathUtils
    {
        /// <summary>
        /// Generates a unique local file path from a given URL, preserving the URL's directory structure.
        /// </summary>
        /// <param name="url">The full URL of the JSON file.</param>
        /// <returns>A relative path that can be appended to a base directory, e.g., "pbe/plugins/rcp-fe-lol-paw/global/default/trans.json".</returns>
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
    }
}
