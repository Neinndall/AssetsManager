using System;
using System.Reflection;
using System.Linq;
using Material.Icons;

namespace AssetsManager.Info
{
    public static class ApplicationInfos
    {
        private static string _cachedVersion;
        private static bool _isQA;
        private static string _buildType;
        private static MaterialIconKind _buildIcon;
        private static string _buildColorKey;

        public static string Version
        {
            get
            {
                if (_cachedVersion != null) return _cachedVersion;

                var assembly = Assembly.GetExecutingAssembly();
                var version = assembly.GetName().Version;
                if (version == null) { _cachedVersion = "vUnknown"; return _cachedVersion; }

                string baseVersion = $"v{version.Major}.{version.Minor}.{version.Build}";
                if (version.Revision > 0)
                {
                    baseVersion = $"{baseVersion}.{version.Revision}";
                }

                var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (infoVersion != null && infoVersion.Contains("-"))
                {
                    var parts = infoVersion.Split('-');
                    string identifier = parts.Last();

                    if (identifier.Length >= 7)
                    {
                        _cachedVersion = $"{baseVersion}-{identifier.Substring(0, 7)}";
                        return _cachedVersion;
                    }

                    _cachedVersion = $"{baseVersion}-{identifier}";
                    return _cachedVersion;
                }

                _cachedVersion = baseVersion;
                return _cachedVersion;
            }
        }

        public static bool IsQA { get { _ = Version; return _isQA; } }
        public static bool IsStable { get { _ = Version; return !_isQA; } }
        public static string BuildType { get { _ = Version; return _buildType; } }
        public static MaterialIconKind BuildIcon { get { _ = Version; return _buildIcon; } }
        public static string BuildColorKey { get { _ = Version; return _buildColorKey; } }

        static ApplicationInfos()
        {
            _ = Version;
            _isQA = _cachedVersion.Contains("-");
            _buildType = _isQA ? "Experimental Build" : "Stable Build";
            _buildIcon = _isQA ? MaterialIconKind.Flask : MaterialIconKind.CheckDecagram;
            _buildColorKey = _isQA ? "AccentOrange" : "AccentGreen";
        }
    }
}
