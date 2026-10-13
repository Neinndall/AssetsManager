using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Views.Models.Monitor;

namespace AssetsManager.Services.Monitor;

public interface IAssetCatalogSource
{
    Task<AssetCatalogImportBatch> ReadAsync(CancellationToken cancellationToken, AssetCatalogQuery query = null);
}
