using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Services.Core;
using AssetsManager.Services.News;
using AssetsManager.Views.Models.News;

namespace AssetsManager.Views
{
    public partial class NewsWindow : UserControl
    {
        private enum NewsTypeFilter { All, Articles, Videos }

        private readonly IServiceProvider _serviceProvider;
        private readonly NewsService _newsService;
        private readonly LogService _logService;
        private readonly NewsFeedModel _viewModel;
        private readonly List<NewsItemModel> _allItems = new();
        private NewsCategory _currentCategory;
        private NewsTypeFilter _typeFilter = NewsTypeFilter.All;
        private bool _sortOldestFirst;
        private bool _isInitialized;

        public NewsWindow(IServiceProvider serviceProvider)
        {
            InitializeComponent();

            _serviceProvider = serviceProvider;
            _newsService = serviceProvider.GetRequiredService<NewsService>();
            _logService = serviceProvider.GetRequiredService<LogService>();
            _viewModel = new NewsFeedModel();
            DataContext = _viewModel;
            NewsArticleReader.DataContext = _viewModel;

            Loaded += NewsWindow_Loaded;
        }

        private async void NewsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isInitialized) return;
            _isInitialized = true;

            var pendingArticle = _newsService.TakePendingArticle();
            if (pendingArticle != null)
            {
                var category = MapNewsCategory(pendingArticle.CategoryId);
                _viewModel.SelectedCategory = _viewModel.Categories.FirstOrDefault(c => c.Category == category) ?? _viewModel.Categories[0];
                await LoadCategoryAsync(category, forceRefresh: true);
                var article = _allItems.FirstOrDefault(item => item.ActionUrl == pendingArticle.ActionUrl);
                if (article != null) OpenDetail(article);
                return;
            }

            _viewModel.SelectedCategory = _viewModel.Categories[0];
            await LoadCategoryAsync(_viewModel.SelectedCategory.Category);
        }

        private static NewsCategory MapNewsCategory(string categoryId)
        {
            return categoryId?.ToLowerInvariant() switch
            {
                "dev" => NewsCategory.Dev,
                "esports" => NewsCategory.Esports,
                "game-updates" => NewsCategory.GameUpdates,
                "media" => NewsCategory.Media,
                "community" => NewsCategory.Community,
                "merch" => NewsCategory.Merch,
                _ => NewsCategory.AllNews
            };
        }

        private async void CategoryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized || _viewModel.SelectedCategory == null) return;
            if (_viewModel.IsBusy) return;
            await LoadCategoryAsync(_viewModel.SelectedCategory.Category);
        }

        private async Task LoadCategoryAsync(NewsCategory category, bool forceRefresh = false)
        {
            if (_viewModel.IsBusy) return;

            _currentCategory = category;
            _viewModel.IsBusy = true;
            _viewModel.HasError = false;
            SearchBox.Text = string.Empty;

            try
            {
                var items = await _newsService.GetNewsAsync(category, forceRefresh);
                _allItems.Clear();
                _allItems.AddRange(items);
                ApplyFilters();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _viewModel.HasError = true;
                _viewModel.ErrorMessage = ex.Message;
            }
            finally
            {
                _viewModel.IsBusy = false;
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            _ = LoadCategoryAsync(_currentCategory, forceRefresh: true);
        }

        private void Retry_Click(object sender, RoutedEventArgs e)
        {
            _ = LoadCategoryAsync(_currentCategory, forceRefresh: true);
        }

        private void SearchBox_SearchTextChanged(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;
            ApplyFilters();
        }

        private void TypeFilter_Checked(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized || sender is not RadioButton radio) return;

            _typeFilter = radio.Tag switch
            {
                "Articles" => NewsTypeFilter.Articles,
                "Videos" => NewsTypeFilter.Videos,
                _ => NewsTypeFilter.All
            };
            ApplyFilters();
        }

        private void Sort_Click(object sender, RoutedEventArgs e)
        {
            _sortOldestFirst = !_sortOldestFirst;
            SortIcon.Kind = _sortOldestFirst ? Material.Icons.MaterialIconKind.SortAscending : Material.Icons.MaterialIconKind.SortDescending;
            SortButton.ToolTip = _sortOldestFirst ? "Sort: oldest first" : "Sort: newest first";
            ApplyFilters();
        }

        private void ApplyFilters()
        {
            string query = SearchBox.Text?.Trim();
            IEnumerable<NewsItemModel> filtered = _allItems;

            if (!string.IsNullOrEmpty(query))
            {
                filtered = filtered.Where(item =>
                    item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    (item.Description != null && item.Description.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                    (item.Tags != null && item.Tags.Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase))));
            }

            switch (_typeFilter)
            {
                case NewsTypeFilter.Articles:
                    filtered = filtered.Where(item => !item.IsVideo && !item.IsVideoLink);
                    break;
                case NewsTypeFilter.Videos:
                    filtered = filtered.Where(item => item.IsVideo || item.IsVideoLink);
                    break;
            }

            filtered = _sortOldestFirst
                ? filtered.OrderBy(item => item.PublishedAt)
                : filtered.OrderByDescending(item => item.PublishedAt);

            bool hasConstraints = !string.IsNullOrEmpty(query) || _typeFilter != NewsTypeFilter.All;
            _viewModel.EmptyStateTitle = hasConstraints ? "NO RESULTS FOUND" : "NO NEWS AVAILABLE";
            _viewModel.EmptyStateHint = hasConstraints
                ? "Try a different search term or clear the filters."
                : "Select a category or press refresh to load the latest articles.";

            _viewModel.SetItems(filtered);
        }

        private void ReadMore_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is NewsItemModel item)
            {
                OpenDetail(item);
            }
        }

        private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject original) return;
            if (FindAncestor<Button>(original) != null) return;

            if ((sender as FrameworkElement)?.Tag is NewsItemModel item)
            {
                OpenDetail(item);
            }
        }

        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T match) return match;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private async void OpenDetail(NewsItemModel item)
        {
            if (item == null) return;

            if (!string.IsNullOrEmpty(item.ActionUrl))
            {
                _ = _newsService.MarkAsSeenAsync(item.ActionUrl, item.PublishedAt);
            }

            _viewModel.SelectedArticle = item;
            _viewModel.FullArticleText = item.DescriptionDisplay;
            _viewModel.FullArticleBanner = null;
            _viewModel.FullArticleAuthors = null;
            _viewModel.IsLoadingFullArticle = true;
            _viewModel.IsDetailVisible = true;

            if (!string.IsNullOrEmpty(item.ActionUrl) && !item.IsVideo && !item.IsVideoLink)
            {
                var content = await _newsService.FetchArticleFullContentAsync(item.ActionUrl);
                if (content != null)
                {
                    if (!string.IsNullOrWhiteSpace(content.Html))
                    {
                        _viewModel.FullArticleHtml = content.Html;
                        _viewModel.FullArticleText = null;
                    }
                    else if (!string.IsNullOrWhiteSpace(content.PlainText))
                    {
                        _viewModel.FullArticleText = content.PlainText;
                    }
                    _viewModel.FullArticleBanner = content.BannerUrl;
                    _viewModel.FullArticleAuthors = content.Authors;
                }
                else
                {
                    _logService.LogWarning($"OpenDetail: no full article content for '{item.Title}' (ActionUrl={item.ActionUrl}).");
                }
            }
            else
            {
                _logService.LogDebug($"OpenDetail: no fetch (IsVideo={item.IsVideo}, ActionUrl={item.ActionUrl}) for '{item.Title}'.");
            }

            _viewModel.IsLoadingFullArticle = false;
        }

        private void NewsArticleReader_BackRequested(object sender, EventArgs e)
        {
            _viewModel.IsDetailVisible = false;
            _viewModel.SelectedArticle = null;
            _viewModel.FullArticleText = null;
            _viewModel.FullArticleHtml = null;
            _viewModel.FullArticleBanner = null;
            _viewModel.FullArticleAuthors = null;
            _viewModel.IsLoadingFullArticle = false;
        }
    }
}
