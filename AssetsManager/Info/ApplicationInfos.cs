using System;
using System.Reflection;

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
    }
}
