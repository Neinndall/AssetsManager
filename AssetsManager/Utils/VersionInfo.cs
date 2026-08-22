using System.Linq;
using System.Reflection;

namespace AssetsManager.Utils
{
    public static class VersionInfo
    {
        private static readonly string _version = ResolveVersion();

        public static string Version => _version;
        public static string BaseVersion => Version.Split('-')[0];
        public static string QaCommitSha => IsQA ? Version.Split('-').Last() : null;
        public static bool IsQA => Version.Contains('-');
        public static bool IsStable => !IsQA;

        private static string ResolveVersion()
        {
            var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
            if (assemblyVersion == null) return "vUnknown";

            string baseVersion = $"v{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
            if (assemblyVersion.Revision > 0)
            {
                baseVersion = $"{baseVersion}.{assemblyVersion.Revision}";
            }

            var infoVersion = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            if (string.IsNullOrEmpty(infoVersion) || !infoVersion.Contains('-')) return baseVersion;

            string identifier = infoVersion.Split('-').Last();
            return identifier.Length >= 7
                ? $"{baseVersion}-{identifier[..7]}"
                : $"{baseVersion}-{identifier}";
        }
    }
}
