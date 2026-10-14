using System.Windows.Media;

namespace AssetsManager.Views.Models.News
{
    public enum NewsCategory
    {
        AllNews,
        GameUpdates,
        Esports,
        Dev,
        Media,
        Community,
        Merch
    }

    public class NewsCategoryOption
    {
        public NewsCategory Category { get; init; }
        public string Name { get; init; }
        public Brush Accent { get; init; }
    }
}
