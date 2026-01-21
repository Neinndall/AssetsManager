using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Explorer;

namespace AssetsManager.Services.Explorer
{
    public class FavoritesManager
    {
        private readonly AppSettings _appSettings;
        public ObservableCollection<FavoriteItemModel> Favorites { get; private set; }

        public FavoritesManager(AppSettings appSettings)
        {
            _appSettings = appSettings;
            Favorites = new ObservableCollection<FavoriteItemModel>();
            LoadFavorites();
        }

        private void LoadFavorites()
        {
            Favorites.Clear();
            if (_appSettings.FavoritePaths != null)
            {
                foreach (var path in _appSettings.FavoritePaths)
                {
                    // Validación básica para no mostrar favoritos rotos si el usuario borró la config manualmente
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        Favorites.Add(CreateViewModel(path));
                    }
                }
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
