using System;
using AssetsManager.Views.Models.Settings;

namespace AssetsManager.Views.Models.Monitor
{
    public class ConnectionInfo
    {
        public string Lockfile { get; set; }
        public int Port { get; set; }
        public string Password { get; set; }
        public string LocalApiUrl { get; set; }
    }

    public class TokenInfo
    {
        public string Jwt { get; set; }
        public DateTime Expiration { get; set; }
        public string Region { get; set; }
        public string Puuid { get; set; }
        public long SummonerId { get; set; }
        public string Platform { get; set; }
        public DateTime IssuedAt { get; set; }
    }

    public class ApiSettings
    {
        public ConnectionInfo Connection { get; set; } = new ConnectionInfo();
        public TokenInfo Token { get; set; } = new TokenInfo();
        public ApiClientTarget ClientTarget { get; set; } = ApiClientTarget.PBE;
        public bool OfflineCachePersistence { get; set; } = true;

        public bool UsePbeForApi
        {
            get => ClientTarget == ApiClientTarget.PBE;
            set => ClientTarget = value ? ApiClientTarget.PBE : ApiClientTarget.LIVE;
        }
    }
}
