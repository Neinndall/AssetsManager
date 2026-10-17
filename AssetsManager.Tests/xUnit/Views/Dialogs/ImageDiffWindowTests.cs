using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AssetsManager.Views.Dialogs;
using AssetsManager.Views.Converters;
using AssetsManager.Views.Helpers;
using Xunit;

namespace AssetsManager.Tests.xUnit.Views.Dialogs;

public class ImageDiffWindowTests
{
    [Fact]
    public void OnionSkinAdjustRemainsResponsiveAfterLeavingTimeline()
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            ImageDiffWindow window = null;
            Application app = null;
            try
            {
                app = new Application();
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
                    ("SegmentedRadioButtonStyle", typeof(RadioButton)), ("TransparentIconButton", typeof(Button))
                }) app.Resources[key] = new Style(type);
                app.Resources[typeof(TextBlock)] = new Style(typeof(TextBlock));
                app.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
                app.Resources["InverseBooleanToVisibilityConverter"] = new InverseBooleanToVisibilityConverter();
                app.Resources["ValueMultiplierConverter"] = new ValueMultiplierConverter();

                window = new ImageDiffWindow();
                var adjust = (Slider)window.FindName("OverlaySlider");
                var overlay = (Image)window.FindName("NewImageOverlay");
                var clip = (RectangleGeometry)window.FindName("SliderClip");
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
                Select("OnionSkinBtn");
                CheckAdjust();
                foreach (string effect in new[] { "WipeEffectBtn", "FadeEffectBtn" })
                {
                    Select("TimelineBtn");
                    Select(effect);
                    ((Slider)window.FindName("TimelineSlider")).Value = 60;
                    Drain();
                    Select("OnionSkinBtn");
                    CheckAdjust();
                    Assert.True(clip.Rect.Width >= overlay.ActualWidth);
                    Select("SliderBtn");
                    Assert.Equal(1.0, overlay.Opacity);
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                window?.Close();
                app?.Shutdown();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Image comparison mode transitions timed out.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
