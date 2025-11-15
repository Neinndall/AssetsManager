using System;

namespace AssetsManager.Views.Models
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
        public bool UsePbeForApi { get; set; }
    }
}
