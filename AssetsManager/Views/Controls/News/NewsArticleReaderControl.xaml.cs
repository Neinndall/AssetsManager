using System;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Utils;
using AssetsManager.Views.Models.News;

namespace AssetsManager.Views.Controls.News
{
    /// <summary>
    /// Inline article reader for the News module. Receives the feed model through its
    /// DataContext (assigned by the parent view) and notifies the parent via BackRequested.
    /// Rendering is fully native (no browser component): rich articles are converted to a
    /// FlowDocument, external-site articles show clean plain text, and video articles get
    /// a thumbnail panel with a watch-on-YouTube action.
    /// </summary>
    public partial class NewsArticleReaderControl : UserControl
    {
        public event EventHandler BackRequested;

        private NewsFeedModel _model;
        private readonly HttpClient _httpClient;
        private string _lastRenderedHtml;
        private string _lastLoadedImageUrl;

        public NewsArticleReaderControl()
        {
            InitializeComponent();
            _httpClient = App.ServiceProvider?.GetService<HttpClient>() ?? new HttpClient();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_model != null)
            {
                _model.PropertyChanged -= Model_PropertyChanged;
            }

            _model = e.NewValue as NewsFeedModel;

            if (_model != null)
            {
                _model.PropertyChanged += Model_PropertyChanged;
                RefreshLayout();
            }
        }

        private void Model_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(NewsFeedModel.IsDetailVisible):
                    if (_model.IsDetailVisible) RefreshLayout();
                    break;
                case nameof(NewsFeedModel.FullArticleHtml):
                case nameof(NewsFeedModel.FullArticleText):
                case nameof(NewsFeedModel.FullArticleBanner):
                case nameof(NewsFeedModel.SelectedArticle):
                    RefreshLayout();
                    break;
            }
        }

        private void RefreshLayout()
        {
            if (_model == null || !_model.IsDetailVisible)
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            var article = _model.SelectedArticle;
            bool isVideo = article != null && (article.IsVideo || article.IsVideoLink);

            UpdateHeader(article, isVideo);

            if (isVideo)
            {
                VideoPanel.Visibility = Visibility.Visible;
                PlainTextPanel.Visibility = Visibility.Collapsed;
                ArticleDocumentViewer.Visibility = Visibility.Collapsed;
                LoadingOverlay.Visibility = Visibility.Collapsed;
                UpdateVideoPanel(article);
                return;
            }

            VideoPanel.Visibility = Visibility.Collapsed;

            string html = _model.FullArticleHtml;
            if (!string.IsNullOrWhiteSpace(html))
            {
                PlainTextPanel.Visibility = Visibility.Collapsed;
                ArticleDocumentViewer.Visibility = Visibility.Visible;
                if (!string.Equals(_lastRenderedHtml, html, StringComparison.Ordinal))
                {
                    _lastRenderedHtml = html;
                    try
                    {
                        ArticleDocumentViewer.Document = ArticleHtmlToFlowDocument.Parse(html, _httpClient);
                    }
                    catch (Exception)
                    {
                        ArticleDocumentViewer.Visibility = Visibility.Collapsed;
                        PlainTextPanel.Visibility = Visibility.Visible;
                    }
                }
            }
            else
            {
                ArticleDocumentViewer.Visibility = Visibility.Collapsed;
                PlainTextPanel.Visibility = Visibility.Visible;
            }

            LoadingOverlay.Visibility = Visibility.Collapsed;
        }

        private void UpdateHeader(NewsItemModel article, bool isVideo)
        {
            bool isDetail = _model != null && _model.IsDetailVisible;
            ArticleHeader.Visibility = isDetail ? Visibility.Visible : Visibility.Collapsed;
            if (!isDetail) return;

            ArticleTitleText.Text = article?.Title ?? string.Empty;
            ArticleTitleText.Visibility = string.IsNullOrEmpty(ArticleTitleText.Text) ? Visibility.Collapsed : Visibility.Visible;

            bool hasCategory = !string.IsNullOrEmpty(article?.CategoryTitle);
            ArticleCategoryChip.Visibility = hasCategory ? Visibility.Visible : Visibility.Collapsed;
            if (hasCategory) ArticleCategoryText.Text = article.CategoryTitle.ToUpperInvariant();

            var publishedAt = article?.PublishedAt ?? DateTime.MinValue;
            ArticleDateText.Text = publishedAt > DateTime.MinValue ? publishedAt.ToLocalTime().ToString("MMMM d, yyyy") : string.Empty;
            ArticleDateText.Visibility = string.IsNullOrEmpty(ArticleDateText.Text) ? Visibility.Collapsed : Visibility.Visible;

            string imageUrl = _model.FullArticleBanner ?? article?.ImageUrl;
            bool hasImage = !string.IsNullOrWhiteSpace(imageUrl);
            BannerPanel.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
            if (hasImage && !string.Equals(_lastLoadedImageUrl, imageUrl, StringComparison.Ordinal))
            {
                _lastLoadedImageUrl = imageUrl;
                BannerImage.Visibility = Visibility.Collapsed;
                LoadImageAsync(BannerImage, imageUrl);
            }
        }

        private void UpdateVideoPanel(NewsItemModel article)
        {
            VideoDescriptionText.Text = string.IsNullOrWhiteSpace(article?.Description)
                ? "No description available for this video."
                : article.Description;
        }

        private void WatchVideo_Click(object sender, RoutedEventArgs e)
        {
            string url = _model?.SelectedArticle?.ActionUrl;
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception)
            {
                // Ignore: video could not be opened.
            }
        }

        private async void LoadImageAsync(Image image, string url)
        {
            try
            {
                byte[] bytes = await Task.Run(async () =>
                {
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, url);
                        request.Headers.TryAddWithoutValidation("User-Agent",
                            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                        using var response = await _httpClient.SendAsync(request);
                        if (!response.IsSuccessStatusCode) return null;
                        return await response.Content.ReadAsByteArrayAsync();
                    }
                    catch
                    {
                        return null;
                    }
                });

                if (bytes == null || bytes.Length == 0) return;

                var bitmap = new BitmapImage();
                using (var stream = new MemoryStream(bytes))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                }
                image.Source = bitmap;
                image.Visibility = Visibility.Visible;
            }
            catch (Exception)
            {
                // Ignore broken images.
            }
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            BackRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
