using System;
using System.Reflection;
using Material.Icons;

namespace AssetsManager.Info
{
    public static class ApplicationInfos
    {
        public static string Version
        {
            get
            {
                var version = Assembly.GetExecutingAssembly().GetName().Version;
                if (version == null) return "vUnknown";

                string baseVersion = $"v{version.Major}.{version.Minor}.{version.Build}";
                if (version.Revision > 0)
                {
                    return $"{baseVersion}.{version.Revision}";
                }
                return baseVersion;
            }
        }

        public static bool IsStable => true;
        public static string BuildType => IsStable ? "STABLE BUILD" : "DEVELOPMENT BUILD";
        public static MaterialIconKind BuildIcon => IsStable ? MaterialIconKind.CheckDecagram : MaterialIconKind.FlaskOutline;
        public static string BuildColorKey => IsStable ? "AccentGreen" : "AccentOrange";
    }
}
