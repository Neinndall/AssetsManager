using System.IO;
using System.Linq;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;
using AssetsManager.Views.Models.Explorer;

namespace AssetsManager.Services.Explorer
{
    public class FavoritesManager
    {
        private readonly AppSettings _appSettings;
        public ObservableRangeCollection<FavoriteItemModel> Favorites { get; private set; }

        public FavoritesManager(AppSettings appSettings)
        {
            _appSettings = appSettings;
            Favorites = new ObservableRangeCollection<FavoriteItemModel>();
            LoadFavorites();
        }

        private void LoadFavorites()
        {
            if (_appSettings.FavoritePaths != null)
            {
                var viewModels = _appSettings.FavoritePaths
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(CreateViewModel)
                    .ToList();

                Favorites.ReplaceRange(viewModels);
            }
            else
            {
                Favorites.Clear();
            }
        }

        public void AddFavorite(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            // Evitar duplicados
            if (_appSettings.FavoritePaths.Contains(path)) return;

            _appSettings.FavoritePaths.Add(path);
            Favorites.Add(CreateViewModel(path));
            
            AppSettings.SaveSettings(_appSettings);
        }

        public void RemoveFavorite(FavoriteItemModel item)
        {
            if (item == null) return;

            if (_appSettings.FavoritePaths.Contains(item.FullPath))
            {
                _appSettings.FavoritePaths.Remove(item.FullPath);
                Favorites.Remove(item);
                AppSettings.SaveSettings(_appSettings);
            }
        }

        private FavoriteItemModel CreateViewModel(string path)
        {
            // Lógica profesional: Path.GetFileName maneja ambos tipos de barras automáticamente.
            // Solo nos aseguramos de quitar barras al final si las hubiera.
            var name = Path.GetFileName(path.TrimEnd('/', '\\'));
            
            return new FavoriteItemModel
            {
                FullPath = path,
                DisplayName = name
            };
        }
    }
}
