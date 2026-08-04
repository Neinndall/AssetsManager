using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using AssetsManager.Utils.Framework;

namespace AssetsManager.Views.Models.News
{
    public class NewsFeedModel : INotifyPropertyChanged
    {
        public ObservableRangeCollection<NewsItemModel> Items { get; } = new();

        public List<NewsCategoryOption> Categories { get; } = new()
        {
            new NewsCategoryOption { Category = NewsCategory.AllNews, Name = "All News", Accent = GetBrush("AccentBrush") },
            new NewsCategoryOption { Category = NewsCategory.Dev, Name = "Dev News", Accent = GetBrush("AccentBlue") },
            new NewsCategoryOption { Category = NewsCategory.Esports, Name = "Esports", Accent = GetBrush("AccentOrange") },
            new NewsCategoryOption { Category = NewsCategory.GameUpdates, Name = "Game Updates", Accent = GetBrush("AccentGreen") },
            new NewsCategoryOption { Category = NewsCategory.Lore, Name = "Lore", Accent = GetBrush("AccentPurple") },
            new NewsCategoryOption { Category = NewsCategory.Media, Name = "Media", Accent = GetBrush("AccentRed") },
            new NewsCategoryOption { Category = NewsCategory.PatchNotes, Name = "Patch Notes", Accent = GetBrush("AccentYellow") }
        };

        private NewsCategoryOption _selectedCategory;
        public NewsCategoryOption SelectedCategory
        {
            get => _selectedCategory;
            set => SetProperty(ref _selectedCategory, value);
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        private bool _hasError;
        public bool HasError
        {
            get => _hasError;
            set => SetProperty(ref _hasError, value);
        }

        private string _statusText;
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        private string _errorMessage;
        public string ErrorMessage
        {
            get => _errorMessage;
            set => SetProperty(ref _errorMessage, value);
        }

        private NewsItemModel _selectedArticle;
        public NewsItemModel SelectedArticle
        {
            get => _selectedArticle;
            set => SetProperty(ref _selectedArticle, value);
        }

        private bool _isDetailVisible;
        public bool IsDetailVisible
        {
            get => _isDetailVisible;
            set => SetProperty(ref _isDetailVisible, value);
        }

        private string _fullArticleText;
        public string FullArticleText
        {
            get => _fullArticleText;
            set => SetProperty(ref _fullArticleText, value);
        }

        private string _fullArticleHtml;
        public string FullArticleHtml
        {
            get => _fullArticleHtml;
            set => SetProperty(ref _fullArticleHtml, value);
        }

        private bool _isLoadingFullArticle;
        public bool IsLoadingFullArticle
        {
            get => _isLoadingFullArticle;
            set => SetProperty(ref _isLoadingFullArticle, value);
        }

        private string _fullArticleBanner;
        public string FullArticleBanner
        {
            get => _fullArticleBanner;
            set => SetProperty(ref _fullArticleBanner, value);
        }

        public int TotalCount => Items.Count;

        public void SetItems(IEnumerable<NewsItemModel> items)
        {
            Items.ReplaceRange(items);
            OnPropertyChanged(nameof(TotalCount));
        }

        private static Brush GetBrush(string key)
        {
            return Application.Current?.TryFindResource(key) as Brush ?? Brushes.DodgerBlue;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
