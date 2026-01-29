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
                // Intentamos obtener la versión informativa (que incluye sufijos como -Beta)
                var informationalVersion = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion;

                if (!string.IsNullOrEmpty(informationalVersion))
                {
                    // Si el string contiene el hash del commit (común en compilaciones automáticas), lo limpiamos
                    int plusIndex = informationalVersion.IndexOf('+');
                    if (plusIndex > 0)
                    {
                        informationalVersion = informationalVersion.Substring(0, plusIndex);
                    }

                    return informationalVersion.StartsWith("v") ? informationalVersion : $"v{informationalVersion}";
                }

                // Fallback a la versión estándar si falla la anterior
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