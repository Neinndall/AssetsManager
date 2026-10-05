using System;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class StudioControl
    {
        private void MapSunToggle_Click(object sender, RoutedEventArgs e)
        {
            if (MapSunToggle?.IsChecked == true)
            {
                if (MapPostToggle != null) MapPostToggle.IsChecked = false;
                if (MapPostPanel != null) MapPostPanel.Visibility = Visibility.Collapsed;
                if (MapSunPanel != null) MapSunPanel.Visibility = Visibility.Visible;
                if (_mapSceneRuntime != null) SyncMapPreviewControls();
            }
            else
            {
                if (MapSunToggle != null) MapSunToggle.IsChecked = true;
            }
        }

        private void MapPostToggle_Click(object sender, RoutedEventArgs e)
        {
            if (MapPostToggle?.IsChecked == true)
            {
                if (MapSunToggle != null) MapSunToggle.IsChecked = false;
                if (MapSunPanel != null) MapSunPanel.Visibility = Visibility.Collapsed;
                if (MapPostPanel != null) MapPostPanel.Visibility = Visibility.Visible;
                if (_mapSceneRuntime != null) SyncMapPreviewControls();
            }
            else
            {
                if (MapPostToggle != null) MapPostToggle.IsChecked = true;
            }
        }

        private void MapSunReset_Click(object sender, RoutedEventArgs e)
        {
            _mapSunPreviewOverride = null;
            SyncMapPreviewControls();
            MarkMapLightingDirty();
        }

        private void MapPostReset_Click(object sender, RoutedEventArgs e)
        {
            _hasMapPostEffectsOverride = false;
            _mapPostEffectsOverride = null;
            _mapSsaoPreviewOverride = null;
            SyncMapPreviewControls();
            MarkMapLightingDirty();
        }

        private void MapSunControl_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
            CommitMapSunControls();

        private void MapSunColor_TextChanged(object sender, TextChangedEventArgs e) =>
            CommitMapSunControls();

        private void MapSsaoControl_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingMapPreviewControls || _mapSceneRuntime == null || MapSsaoQualityCombo == null)
                return;

            _mapSsaoPreviewOverride = new MapSsaoPreviewOverride(
                MapSsaoEnabledCheck.IsChecked == true,
                new MapSsaoData(
                    MapSsaoQualityCombo.SelectedIndex <= 0 ? 0u : 1u,
                    (float)MapSsaoRadiusSlider.Value,
                    (float)MapSsaoBiasSlider.Value,
                    (float)MapSsaoPowerSlider.Value,
                    (float)MapSsaoIntensitySlider.Value,
                    (float)MapSsaoBufferScaleSlider.Value,
                    MapSsaoEdgeAwareCheck.IsChecked == true));
            MarkMapLightingDirty();
        }

        private void MapPostControl_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingMapPreviewControls || _mapSceneRuntime == null || MapDepthFogColorTextBox == null)
                return;
            UpdateColorSwatch(MapDepthFogColorSwatch, MapDepthFogColorTextBox.Text);
            UpdateColorSwatch(MapHeightFogColorSwatch, MapHeightFogColorTextBox.Text);
            if (!TryParseMapPreviewColor(MapDepthFogColorTextBox.Text, out Vector4 depthColor))
                depthColor = Vector4.UnitW;
            if (!TryParseMapPreviewColor(MapHeightFogColorTextBox.Text, out Vector4 heightColor))
                heightColor = Vector4.UnitW;

            _hasMapPostEffectsOverride = true;
            _mapPostEffectsOverride = new MapPostEffectsData(
                new MapFogData(
                    MapDepthFogEnabledCheck.IsChecked == true,
                    depthColor,
                    (float)MapDepthFogStartSlider.Value,
                    (float)MapDepthFogEndSlider.Value,
                    (float)MapDepthFogMaxSlider.Value),
                new MapFogData(
                    MapHeightFogEnabledCheck.IsChecked == true,
                    heightColor,
                    (float)MapHeightFogStartSlider.Value,
                    (float)MapHeightFogEndSlider.Value,
                    (float)MapHeightFogMaxSlider.Value),
                new MapDepthOfFieldData(
                    MapDofEnabledCheck.IsChecked == true,
                    (float)MapDofFocalSlider.Value,
                    (float)MapDofWidthSlider.Value,
                    (float)MapDofCocSlider.Value));
            MarkMapLightingDirty();
        }

        private void CommitMapSunControls()
        {
            if (_isUpdatingMapPreviewControls || _mapSceneRuntime == null || MapSunColorTextBox == null)
                return;
            UpdateColorSwatch(MapSunColorSwatch, MapSunColorTextBox.Text);
            UpdateColorSwatch(MapSunSkyColorSwatch, MapSunSkyColorTextBox.Text);
            UpdateColorSwatch(MapSunGroundColorSwatch, MapSunGroundColorTextBox.Text);
            if (!TryParseMapPreviewColor(MapSunColorTextBox.Text, out Vector4 color))
                color = Vector4.One;
            if (!TryParseMapPreviewColor(MapSunSkyColorTextBox.Text, out Vector4 sky))
                sky = Vector4.One;
            if (!TryParseMapPreviewColor(MapSunGroundColorTextBox.Text, out Vector4 ground))
                ground = new Vector4(0.1f, 0.1f, 0.1f, 1f);

            _mapSunPreviewOverride = new MapSunPreviewOverride(
                MapPreviewSemantics.SunDirection(
                    (float)MapSunAzimuthSlider.Value,
                    (float)MapSunElevationSlider.Value),
                color,
                (float)MapSunStrengthSlider.Value,
                sky,
                ground,
                (float)MapSunAmbientSlider.Value);
            MarkMapLightingDirty();
        }

        private void SyncMapPreviewControls()
        {
            if (_mapSceneRuntime == null || MapSunAzimuthSlider == null)
                return;

            _isUpdatingMapPreviewControls = true;
            try
            {
                MapSunPreviewOverride sun = _mapSunPreviewOverride ??
                    MapPreviewSemantics.OwnSun(_mapSceneRuntime.Scene.Sun);
                (float azimuth, float elevation) = MapPreviewSemantics.SunAngles(sun.Direction);
                MapSunAzimuthSlider.Value = azimuth;
                MapSunElevationSlider.Value = elevation;
                MapSunStrengthSlider.Value = sun.Strength;
                MapSunAmbientSlider.Value = sun.Ambient;
                MapSunColorTextBox.Text = FormatMapPreviewColor(sun.Color);
                MapSunSkyColorTextBox.Text = FormatMapPreviewColor(sun.SkyColor);
                MapSunGroundColorTextBox.Text = FormatMapPreviewColor(sun.GroundColor);
                UpdateColorSwatch(MapSunColorSwatch, MapSunColorTextBox.Text);
                UpdateColorSwatch(MapSunSkyColorSwatch, MapSunSkyColorTextBox.Text);
                UpdateColorSwatch(MapSunGroundColorSwatch, MapSunGroundColorTextBox.Text);

                MapPostEffectsData post = _hasMapPostEffectsOverride
                    ? _mapPostEffectsOverride ?? MapPreviewSemantics.NoPostEffects
                    : MapPreviewSemantics.OwnPostEffects(_mapSceneRuntime.Scene.PostEffects);
                MapDepthFogEnabledCheck.IsChecked = post.DepthFog.Enabled;
                MapDepthFogColorTextBox.Text = FormatMapPreviewColor(post.DepthFog.Color);
                UpdateColorSwatch(MapDepthFogColorSwatch, MapDepthFogColorTextBox.Text);
                MapDepthFogStartSlider.Value = post.DepthFog.Start;
                MapDepthFogEndSlider.Value = post.DepthFog.End;
                MapDepthFogMaxSlider.Value = post.DepthFog.MaxIntensity;
                MapHeightFogEnabledCheck.IsChecked = post.HeightFog.Enabled;
                MapHeightFogColorTextBox.Text = FormatMapPreviewColor(post.HeightFog.Color);
                UpdateColorSwatch(MapHeightFogColorSwatch, MapHeightFogColorTextBox.Text);
                MapHeightFogStartSlider.Value = post.HeightFog.Start;
                MapHeightFogEndSlider.Value = post.HeightFog.End;
                MapHeightFogMaxSlider.Value = post.HeightFog.MaxIntensity;
                MapDofEnabledCheck.IsChecked = post.DepthOfField.Enabled;
                MapDofFocalSlider.Value = post.DepthOfField.FocalDistance;
                MapDofWidthSlider.Value = post.DepthOfField.InFocusWidth;
                MapDofCocSlider.Value = post.DepthOfField.Coc;

                MapSsaoPreviewOverride ssao = _mapSsaoPreviewOverride ??
                    MapPreviewSemantics.OwnSsao(_mapSceneRuntime.Scene.AmbientOcclusion);
                MapSsaoEnabledCheck.IsChecked = ssao.Enabled;
                MapSsaoQualityCombo.SelectedIndex = ssao.Settings.SampleQuality == 0 ? 0 : 1;
                MapSsaoRadiusSlider.Value = ssao.Settings.SampleRadius;
                MapSsaoBiasSlider.Value = ssao.Settings.Bias;
                MapSsaoPowerSlider.Value = ssao.Settings.Power;
                MapSsaoIntensitySlider.Value = ssao.Settings.Intensity;
                MapSsaoBufferScaleSlider.Value = ssao.Settings.BufferScale;
                MapSsaoEdgeAwareCheck.IsChecked = ssao.Settings.EdgeAwareBlur;
            }
            finally
            {
                _isUpdatingMapPreviewControls = false;
            }
        }

        private static void UpdateColorSwatch(System.Windows.Controls.Border swatch, string hex)
        {
            if (swatch == null) return;
            if (TryParseMapPreviewColor(hex, out Vector4 v))
            {
                swatch.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(
                    (byte)Math.Round(Math.Clamp(v.X, 0f, 1f) * 255f),
                    (byte)Math.Round(Math.Clamp(v.Y, 0f, 1f) * 255f),
                    (byte)Math.Round(Math.Clamp(v.Z, 0f, 1f) * 255f)));
            }
        }

        private static bool TryParseMapPreviewColor(string text, out Vector4 value)
        {
            value = Vector4.One;
            if (string.IsNullOrWhiteSpace(text)) return false;
            try
            {
                string candidate = text.Trim();
                if (!candidate.StartsWith("#") && (candidate.Length == 3 || candidate.Length == 6 || candidate.Length == 8))
                    candidate = "#" + candidate;
                if (candidate.StartsWith("#") && candidate.Length == 4)
                    candidate = $"#{candidate[1]}{candidate[1]}{candidate[2]}{candidate[2]}{candidate[3]}{candidate[3]}";
                object converted = ColorConverter.ConvertFromString(candidate);
                if (converted is not System.Windows.Media.Color color) return false;
                value = new Vector4(
                    color.R / 255f,
                    color.G / 255f,
                    color.B / 255f,
                    color.A / 255f);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
        }

        private static string FormatMapPreviewColor(Vector4 value)
        {
            static byte Channel(float channel) =>
                (byte)Math.Round(Math.Clamp(channel, 0f, 1f) * 255f, MidpointRounding.AwayFromZero);
            return $"#{Channel(value.X):X2}{Channel(value.Y):X2}{Channel(value.Z):X2}";
        }
    }
}
