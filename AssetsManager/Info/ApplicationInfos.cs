using System;
using System.Reflection;
using System.Linq;
using Material.Icons;

namespace AssetsManager.Info
{
    public static class ApplicationInfos
    {
        public static string Version
        {
            get
            {
                var assembly = Assembly.GetExecutingAssembly();
                var version = assembly.GetName().Version;
                if (version == null) return "vUnknown";

                string baseVersion = $"v{version.Major}.{version.Minor}.{version.Build}";
                if (version.Revision > 0)
                {
                    baseVersion = $"{baseVersion}.{version.Revision}";
                }

                // Check for SHA suffix in InformationalVersion (e.g., 3.2.3.0-abcdef1)
                var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (infoVersion != null && infoVersion.Contains("-"))
                {
                    var parts = infoVersion.Split('-');
                    string identifier = parts.Last();

                    // If the identifier is a hex SHA (usually 7+ chars), we take the first 7
                    if (identifier.Length >= 7)
                    {
                        return $"{baseVersion}-{identifier.Substring(0, 7)}";
                    }

                    return $"{baseVersion}-{identifier}";
                }

                return baseVersion;
            }
        }

        public static bool IsQA => Version.Contains("-");
        public static bool IsStable => !IsQA;

        public static string BuildType => IsQA ? "QA BUILD" : "STABLE BUILD";
        public static MaterialIconKind BuildIcon => IsQA ? MaterialIconKind.TestTube : MaterialIconKind.CheckDecagram;
        public static string BuildColorKey => IsQA ? "AccentOrange" : "AccentGreen";
    }
}
