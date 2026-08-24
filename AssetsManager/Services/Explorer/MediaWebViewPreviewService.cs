using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AssetsManager.Services.Explorer
{
    public sealed class MediaWebViewPreviewService
    {
        private const string BlankDocument = "<!DOCTYPE html><html><head><meta charset='UTF-8'></head><body></body></html>";

        private readonly LogService _logService;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly MediaTempFileStore _tempFileStore;
        private readonly string _audioPlayerTemplate;
        private readonly SemaphoreSlim _navigationLock = new(1, 1);
        private Task<CoreWebView2Environment> _environmentTask;
        private Task<WebView2> _initializationTask;
        private Grid _container;
        private WebView2 _webView;

        public MediaWebViewPreviewService(
            LogService logService,
            DirectoriesCreator directoriesCreator,
            MediaTempFileStore tempFileStore)
        {
            _logService = logService;
            _directoriesCreator = directoriesCreator;
            _tempFileStore = tempFileStore;

            Assembly assembly = Assembly.GetExecutingAssembly();
            using Stream stream = assembly.GetManifestResourceStream("AssetsManager.Resources.AudioPlayer.html")
                ?? throw new InvalidOperationException("Embedded audio player resource was not found.");
            using var reader = new StreamReader(stream);
            _audioPlayerTemplate = reader.ReadToEnd();
        }

        public void Initialize(Grid container)
        {
            if (_container != null && _container != container)
            {
                DisposePersistentWebView();
            }

            _container = container;
        }

        public async Task<bool> ShowAsync(
            byte[] data,
            string extension,
            string displayName,
            bool shouldAutoplay,
            CancellationToken cancellationToken)
        {
            if (_container == null)
            {
                return false;
            }

            string tempFilePath = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                tempFilePath = await _tempFileStore.CreateAsync(data, extension, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                string htmlContent = BuildDocument(tempFilePath, extension, displayName);

                PrepareTransition();
                bool navigationSucceeded = await NavigateAndShowAsync(htmlContent, shouldAutoplay, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                if (navigationSucceeded)
                {
                    _tempFileStore.Activate(tempFilePath);
                    tempFilePath = null;
                }

                return navigationSucceeded;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to create or show media preview for extension '{extension}'.");
                DisposePersistentWebView();
                return false;
            }
            finally
            {
                _tempFileStore.DeleteOrDefer(tempFilePath);
            }
        }

        public void Deactivate()
        {
            PrepareTransition();
            if (_webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.NavigateToString(BlankDocument);
            }
            else
            {
                _tempFileStore.RetryPending();
            }
        }

        public void ReleaseResources()
        {
            try
            {
                DisposePersistentWebView();
            }
            finally
            {
                _container = null;
            }
        }

        private async Task<bool> NavigateAndShowAsync(
            string htmlContent,
            bool shouldAutoplay,
            CancellationToken cancellationToken)
        {
            bool lockAcquired = false;
            try
            {
                await _navigationLock.WaitAsync(cancellationToken);
                lockAcquired = true;
                cancellationToken.ThrowIfCancellationRequested();

                WebView2 webView = await GetOrCreateWebViewAsync();
                cancellationToken.ThrowIfCancellationRequested();
                webView.Visibility = Visibility.Hidden;

                CoreWebView2 coreWebView = webView.CoreWebView2;
                ulong navigationId = 0;
                var navigationCompletion = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

                void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs args)
                {
                    navigationId = args.NavigationId;
                    coreWebView.NavigationStarting -= OnNavigationStarting;
                }

                void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs args)
                {
                    if (navigationId != 0 && args.NavigationId == navigationId)
                    {
                        navigationCompletion.TrySetResult(args);
                    }
                }

                coreWebView.NavigationStarting += OnNavigationStarting;
                coreWebView.NavigationCompleted += OnNavigationCompleted;
                using var cancellationRegistration = cancellationToken.Register(() =>
                    navigationCompletion.TrySetCanceled(cancellationToken));

                try
                {
                    coreWebView.Stop();
                    coreWebView.NavigateToString(htmlContent);
                    CoreWebView2NavigationCompletedEventArgs result = await navigationCompletion.Task;
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!result.IsSuccess)
                    {
                        throw new InvalidOperationException($"WebView2 navigation failed with status {result.WebErrorStatus}.");
                    }

                    _tempFileStore.RetryPending();
                    webView.Visibility = Visibility.Visible;

                    if (shouldAutoplay)
                    {
                        StartAutoplay(webView, cancellationToken);
                    }

                    return true;
                }
                finally
                {
                    coreWebView.NavigationStarting -= OnNavigationStarting;
                    coreWebView.NavigationCompleted -= OnNavigationCompleted;
                }
            }
            finally
            {
                if (lockAcquired)
                {
                    _navigationLock.Release();
                }
            }
        }

        private Task<WebView2> GetOrCreateWebViewAsync()
        {
            return _initializationTask ??= InitializePersistentWebViewAsync();
        }

        private async Task<WebView2> InitializePersistentWebViewAsync()
        {
            if (_container == null)
            {
                throw new InvalidOperationException("WebView2 container is not initialized.");
            }

            var webView = new WebView2
            {
                DefaultBackgroundColor = System.Drawing.Color.Transparent,
                Visibility = Visibility.Hidden
            };

            _webView = webView;
            _container.Children.Add(webView);

            CoreWebView2Environment environment = await GetEnvironmentAsync();
            await webView.EnsureCoreWebView2Async(environment);
            webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "preview.assets",
                _directoriesCreator.TempPreviewPath,
                CoreWebView2HostResourceAccessKind.Allow);
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            webView.CoreWebView2.Settings.IsSwipeNavigationEnabled = false;
            webView.CoreWebView2.NavigationCompleted += PersistentWebView_NavigationCompleted;
            return webView;
        }

        private async Task<CoreWebView2Environment> GetEnvironmentAsync()
        {
            _environmentTask ??= CoreWebView2Environment.CreateAsync(
                userDataFolder: _directoriesCreator.WebView2DataPath);

            try
            {
                return await _environmentTask;
            }
            catch
            {
                _environmentTask = null;
                throw;
            }
        }

        private string BuildDocument(string tempFilePath, string extension, string displayName)
        {
            string fileName = Path.GetFileName(tempFilePath);
            string fileUrl = $"https://preview.assets/{fileName}";

            if (!string.Equals(extension, ".webm", StringComparison.OrdinalIgnoreCase))
            {
                return _audioPlayerTemplate.Replace("{{DISPLAY_NAME}}", WebUtility.HtmlEncode(displayName))
                                           .Replace("{{FILE_EXTENSION}}", extension.ToUpperInvariant().TrimStart('.'))
                                           .Replace("{{FILE_URL}}", fileUrl);
            }

            return $@"
                    <!DOCTYPE html>
                    <html>
                    <head>
                        <meta charset='UTF-8'>
                        <style>
                            html, body {{
                                background-color: transparent !important;
                                margin: 0; padding: 0; height: 100vh;
                                display: flex; justify-content: center; align-items: center; overflow: hidden;
                            }}
                            video {{
                                max-width: 90%; max-height: 90%;
                                border-radius: 12px;
                                box-shadow: 0 4px 12px rgba(0,0,0,0.20);
                                background-color: #000;
                                opacity: 0;
                                transition: opacity 0.3s ease-out;
                            }}
                            video.loaded {{
                                opacity: 1;
                            }}
                        </style>
                    </head>
                    <body>
                        <video id='mediaElement' controls preload='auto' muted>
                            <source src='{fileUrl}' type='video/webm'>
                        </video>
                        <script>
                            const mediaElement = document.getElementById('mediaElement');
                            window.playMedia = () => {{
                                mediaElement.play().catch(e => console.log('Play error:', e));
                            }};
                            mediaElement.addEventListener('loadeddata', () => mediaElement.classList.add('loaded'));
                            setTimeout(() => mediaElement.classList.add('loaded'), 1000);
                        </script>
                    </body>
                    </html>";
        }

        private void StartAutoplay(WebView2 webView, CancellationToken cancellationToken)
        {
            _ = webView.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    if (!cancellationToken.IsCancellationRequested && webView.CoreWebView2 != null)
                    {
                        await webView.CoreWebView2.ExecuteScriptAsync("playMedia();");
                    }
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, "Failed to autoplay media.");
                }
            });
        }

        private void PrepareTransition()
        {
            if (_webView != null)
            {
                _webView.Visibility = Visibility.Hidden;
                _webView.CoreWebView2?.Stop();
            }

            _tempFileStore.RetireActive();
        }

        private void PersistentWebView_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            _tempFileStore.RetryPending();
        }

        private void DisposePersistentWebView()
        {
            _tempFileStore.RetireActive();
            WebView2 webView = _webView;
            _webView = null;
            _initializationTask = null;

            try
            {
                if (webView?.CoreWebView2 != null)
                {
                    webView.CoreWebView2.NavigationCompleted -= PersistentWebView_NavigationCompleted;
                    webView.CoreWebView2.Stop();
                }
            }
            finally
            {
                try
                {
                    if (webView != null)
                    {
                        if (_container?.Children.Contains(webView) == true)
                        {
                            _container.Children.Remove(webView);
                        }

                        webView.Dispose();
                    }
                }
                finally
                {
                    _tempFileStore.Release();
                }
            }
        }
    }
}
