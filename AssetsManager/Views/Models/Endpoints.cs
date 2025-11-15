using System.Collections.Generic;

namespace AssetsManager.Views.Models
{
  public static class Endpoints
  {
    public static string BaseUrlPBE => "https://pbe-red.lol.sgp.pvp.net";
    public static string BaseUrlLive => "https://{region}-red.lol.sgp.pvp.net";

    public static Dictionary<string, string> GetLocalEndpoints() => new Dictionary<string, string>
        {
            { "entitlementsToken", "/entitlements/v1/token" }
        };

    public static Dictionary<string, string> GetRemoteEndpoints() => new Dictionary<string, string>
        {
            { "sales", "/storefront/v3/view/skins" },
            { "mithic_shop", "/catalog/v1/products/d1c2664a-5938-4c41-8d1b-61fd51052c22/stores" }
        };
  }
}
