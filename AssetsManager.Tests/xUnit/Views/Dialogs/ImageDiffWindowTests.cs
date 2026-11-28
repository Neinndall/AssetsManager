using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AssetsManager.Services.Core;
using AssetsManager.Services.Parsers;
using AssetsManager.Views.Dialogs;
using AssetsManager.Views.Converters;
using AssetsManager.Views.Helpers;
using Xunit;

namespace AssetsManager.Tests.xUnit.Views.Dialogs;

public class ImageDiffWindowTests : IClassFixture<ImageDiffWindowTests.WpfAppFixture>
{
    private readonly WpfAppFixture _wpf;

    public ImageDiffWindowTests(WpfAppFixture wpf)
    {
        _wpf = wpf;
    }

    public sealed class WpfAppFixture : IDisposable
    {
        private readonly Thread _staThread;
        private readonly ManualResetEventSlim _ready = new(false);
        private Dispatcher _dispatcher;
        private Application _app;
        private Exception _startupFailure;

        public WpfAppFixture()
        {
            _staThread = new Thread(() =>
            {
                try
                {
                    _dispatcher = Dispatcher.CurrentDispatcher;
                    _app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    EnsureResources(_app);
                }
                catch (Exception ex)
                {
                    _startupFailure = ex;
                }
                finally
                {
                    _ready.Set();
                }

                if (_startupFailure == null)
                    Dispatcher.Run();
            });
            _staThread.IsBackground = true;
            _staThread.SetApartmentState(ApartmentState.STA);
            _staThread.Start();

            if (!_ready.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("WPF test host initialization timed out.");
            if (_startupFailure != null)
                ExceptionDispatchInfo.Capture(_startupFailure).Throw();
        }

        public void Run(Action action)
        {
            Exception failure = null;
            _dispatcher.Invoke(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
            });
            if (failure != null)
                ExceptionDispatchInfo.Capture(failure).Throw();
        }

        public void Dispose()
        {
            if (_dispatcher != null && !_dispatcher.HasShutdownStarted)
            {
                _dispatcher.Invoke(() => _app?.Shutdown());
                _dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                _staThread.Join(TimeSpan.FromSeconds(5));
            }
            _ready.Dispose();
        }

        private static void EnsureResources(Application app)
        {
            if (app.Resources.Contains("BooleanToVisibilityConverter"))
                return;

            // Keep the production window and bindings without loading application-wide control templates.
            foreach (string theme in new[] { "Font", "Colors" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"/AssetsManager;component/Themes/{theme}.xaml", UriKind.Relative)
                });
            foreach (var (key, type) in new[]
            {
                ("ToolSectionLabel", typeof(TextBlock)), ("SettingsCard", typeof(Border)),
                ("HudWindowStyle", typeof(HudWindow)), ("HUDSliderStyle", typeof(Slider)),
                ("ModernComboBox", typeof(ComboBox)), ("ModernIconRadioButton", typeof(RadioButton)),
                ("SegmentedRadioButtonStyle", typeof(RadioButton)), ("TransparentIconButton", typeof(Button)),
                ("PrimaryButton", typeof(Button)), ("ModernCheckBox", typeof(CheckBox)),
                ("ModernProgressBar", typeof(ProgressBar))
            }) app.Resources[key] = new Style(type);
            app.Resources[typeof(TextBlock)] = new Style(typeof(TextBlock));
            app.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
            app.Resources["InverseBooleanToVisibilityConverter"] = new InverseBooleanToVisibilityConverter();
            app.Resources["ValueMultiplierConverter"] = new ValueMultiplierConverter();
        }
    }

    [Fact]
    public void OnionSkinAdjustRemainsResponsiveAfterLeavingTimeline()
    {
        _wpf.Run(() =>
        {
            ImageDiffWindow window = null;
            try
            {
                window = new ImageDiffWindow();
                var adjust = (Slider)window.FindName("OverlaySlider");
                var oldOverlay = (Image)window.FindName("OldImageOverlay");
                var overlay = (Image)window.FindName("NewImageOverlay");
                var clip = (RectangleGeometry)window.FindName("SliderClip");
                var oldClip = (RectangleGeometry)window.FindName("OldSliderClip");
                var infoLabel = (TextBlock)window.FindName("OverlayInfoLabel");
                void Drain() => window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                void Select(string name)
                {
                    ((RadioButton)window.FindName(name)).IsChecked = true;
                    Drain();
                }
                void CheckAdjust()
                {
                    foreach (double value in new[] { 0.0, 25.0, 75.0, 100.0 })
                    {
                        adjust.Value = value;
                        Drain();
                        Assert.Equal(value / 100.0, overlay.Opacity, 6);
                    }
                }

                Select("SliderBtn");
                Assert.Equal("Slider Overlay View", infoLabel.Text);
                Select("OnionSkinBtn");
                Assert.Equal("Onion Skin Transparency", infoLabel.Text);
                CheckAdjust();
                foreach (string effect in new[] { "WipeEffectBtn", "FadeEffectBtn", "CutEffectBtn" })
                {
                    Select("TimelineBtn");
                    Assert.Equal("Timeline Frame Sequence", infoLabel.Text);
                    Select(effect);
                    ((Slider)window.FindName("TimelineSlider")).Value = 60;
                    Drain();
                    Select("OnionSkinBtn");
                    CheckAdjust();
                    Assert.Equal(1.0, oldOverlay.Opacity);
                    Assert.True(clip.Rect.Width >= overlay.ActualWidth);
                    Assert.True(oldClip.Rect.Width >= oldOverlay.ActualWidth);
                    Select("SliderBtn");
                    Assert.Equal(1.0, overlay.Opacity);
                }
            }
            finally
            {
                window?.Close();
            }
        });
    }

    [Fact]
    public void SvgImagesLoadAndDisplayInImageDiffWindow()
    {
        const string oldSvg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"24\" height=\"24\" viewBox=\"0 0 24 24\"><rect width=\"24\" height=\"24\" fill=\"#ff0000\"/></svg>";
        const string newSvg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"48\" height=\"32\" viewBox=\"0 0 48 32\"><rect width=\"48\" height=\"32\" fill=\"#00ff00\"/></svg>";

        var diffViewService = new DiffViewService(null, null, null, null, null, null, new SvgParser());
        ImageSource oldImage = diffViewService.LoadDiffImage(Encoding.UTF8.GetBytes(oldSvg), ".svg");
        ImageSource newImage = diffViewService.LoadDiffImage(Encoding.UTF8.GetBytes(newSvg), ".svg");

        Assert.Null(diffViewService.LoadDiffImage(null, ".svg"));
        Assert.IsType<DrawingImage>(oldImage);
        Assert.IsType<DrawingImage>(newImage);

        _wpf.Run(() =>
        {
            ImageDiffWindow window = null;
            try
            {
                // Case 1: NEW-only SVG (no OLD version)
                window = new ImageDiffWindow(null, newImage, null, "plugins/rcp-fe-lol-premade-voice/global/default/team-voice-eat-close.svg");
                Assert.Null(((Image)window.FindName("OldImage")).Source);
                Assert.Same(newImage, ((Image)window.FindName("NewImage")).Source);
                Assert.Equal("N/A", ((TextBlock)window.FindName("OldFileNameLabel")).Text);
                Assert.Equal("Resolution: 48×32", ((TextBlock)window.FindName("ResolutionLabel")).Text);

                // Case 2: MODIFIED SVG via batch loader (both OLD and NEW versions with different dimensions)
                window.LoadAndDisplayPreloadedBatchAsync(
                    new List<(ImageSource, ImageSource, string, string)>
                    {
                        (oldImage, newImage, "old.svg", "new.svg")
                    },
                    0);
                Assert.Same(oldImage, ((Image)window.FindName("OldImage")).Source);
                Assert.Same(newImage, ((Image)window.FindName("NewImage")).Source);
                Assert.Equal("Resolution: 24×24 → 48×32", ((TextBlock)window.FindName("ResolutionLabel")).Text);
            }
            finally
            {
                window?.Close();
            }
        });
    }
}
