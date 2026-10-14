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
        internal void MapSunToggle_Click(object sender, RoutedEventArgs e)
        {
            if (StudioInspectorView.MapSunToggle?.IsChecked == true)
            {
                if (StudioInspectorView.MapPostToggle != null) StudioInspectorView.MapPostToggle.IsChecked = false;
                if (StudioInspectorView.MapPostPanel != null) StudioInspectorView.MapPostPanel.Visibility = Visibility.Collapsed;
                if (StudioInspectorView.MapSunPanel != null) StudioInspectorView.MapSunPanel.Visibility = Visibility.Visible;
                if (_mapSceneRuntime != null) SyncMapPreviewControls();
            }
            else
            {
                if (StudioInspectorView.MapSunToggle != null) StudioInspectorView.MapSunToggle.IsChecked = true;
            }
        }

        internal void MapPostToggle_Click(object sender, RoutedEventArgs e)
        {
            if (StudioInspectorView.MapPostToggle?.IsChecked == true)
            {
                if (StudioInspectorView.MapSunToggle != null) StudioInspectorView.MapSunToggle.IsChecked = false;
                if (StudioInspectorView.MapSunPanel != null) StudioInspectorView.MapSunPanel.Visibility = Visibility.Collapsed;
                if (StudioInspectorView.MapPostPanel != null) StudioInspectorView.MapPostPanel.Visibility = Visibility.Visible;
                if (_mapSceneRuntime != null) SyncMapPreviewControls();
            }
            else
            {
                if (StudioInspectorView.MapPostToggle != null) StudioInspectorView.MapPostToggle.IsChecked = true;
            }
        }

        internal void MapSunReset_Click(object sender, RoutedEventArgs e)
        {
            _mapSunPreviewOverride = null;
            SyncMapPreviewControls();
            MarkMapLightingDirty();
        }

        internal void MapPostReset_Click(object sender, RoutedEventArgs e)
        {
            _hasMapPostEffectsOverride = false;
            _mapPostEffectsOverride = null;
            _mapSsaoPreviewOverride = null;
            SyncMapPreviewControls();
            MarkMapLightingDirty();
        }

        internal void MapSunControl_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
            CommitMapSunControls();

        internal void MapSunColor_TextChanged(object sender, TextChangedEventArgs e) =>
            CommitMapSunControls();

        internal void MapSsaoControl_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingMapPreviewControls || _mapSceneRuntime == null || StudioInspectorView.MapSsaoQualityCombo == null)
                return;

            _mapSsaoPreviewOverride = new MapSsaoPreviewOverride(
                StudioInspectorView.MapSsaoEnabledCheck.IsChecked == true,
                new MapSsaoData(
                    StudioInspectorView.MapSsaoQualityCombo.SelectedIndex <= 0 ? 0u : 1u,
                    (float)StudioInspectorView.MapSsaoRadiusSlider.Value,
                    (float)StudioInspectorView.MapSsaoBiasSlider.Value,
                    (float)StudioInspectorView.MapSsaoPowerSlider.Value,
                    (float)StudioInspectorView.MapSsaoIntensitySlider.Value,
                    (float)StudioInspectorView.MapSsaoBufferScaleSlider.Value,
                    StudioInspectorView.MapSsaoEdgeAwareCheck.IsChecked == true));
            MarkMapLightingDirty();
        }

        internal void MapPostControl_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingMapPreviewControls || _mapSceneRuntime == null || StudioInspectorView.MapDepthFogColorTextBox == null)
                return;
            UpdateColorSwatch(StudioInspectorView.MapDepthFogColorSwatch, StudioInspectorView.MapDepthFogColorTextBox.Text);
            UpdateColorSwatch(StudioInspectorView.MapHeightFogColorSwatch, StudioInspectorView.MapHeightFogColorTextBox.Text);
            if (!TryParseMapPreviewColor(StudioInspectorView.MapDepthFogColorTextBox.Text, out Vector4 depthColor))
                depthColor = Vector4.UnitW;
            if (!TryParseMapPreviewColor(StudioInspectorView.MapHeightFogColorTextBox.Text, out Vector4 heightColor))
                heightColor = Vector4.UnitW;

            _hasMapPostEffectsOverride = true;
            _mapPostEffectsOverride = new MapPostEffectsData(
                new MapFogData(
                    StudioInspectorView.MapDepthFogEnabledCheck.IsChecked == true,
                    depthColor,
                    (float)StudioInspectorView.MapDepthFogStartSlider.Value,
                    (float)StudioInspectorView.MapDepthFogEndSlider.Value,
                    (float)StudioInspectorView.MapDepthFogMaxSlider.Value),
                new MapFogData(
                    StudioInspectorView.MapHeightFogEnabledCheck.IsChecked == true,
                    heightColor,
                    (float)StudioInspectorView.MapHeightFogStartSlider.Value,
                    (float)StudioInspectorView.MapHeightFogEndSlider.Value,
                    (float)StudioInspectorView.MapHeightFogMaxSlider.Value),
                new MapDepthOfFieldData(
                    StudioInspectorView.MapDofEnabledCheck.IsChecked == true,
                    (float)StudioInspectorView.MapDofFocalSlider.Value,
                    (float)StudioInspectorView.MapDofWidthSlider.Value,
                    (float)StudioInspectorView.MapDofCocSlider.Value));
            MarkMapLightingDirty();
        }

        private void CommitMapSunControls()
        {
            if (_isUpdatingMapPreviewControls || _mapSceneRuntime == null || StudioInspectorView.MapSunColorTextBox == null)
                return;
            UpdateColorSwatch(StudioInspectorView.MapSunColorSwatch, StudioInspectorView.MapSunColorTextBox.Text);
            UpdateColorSwatch(StudioInspectorView.MapSunSkyColorSwatch, StudioInspectorView.MapSunSkyColorTextBox.Text);
            UpdateColorSwatch(StudioInspectorView.MapSunGroundColorSwatch, StudioInspectorView.MapSunGroundColorTextBox.Text);
            if (!TryParseMapPreviewColor(StudioInspectorView.MapSunColorTextBox.Text, out Vector4 color))
                color = Vector4.One;
            if (!TryParseMapPreviewColor(StudioInspectorView.MapSunSkyColorTextBox.Text, out Vector4 sky))
                sky = Vector4.One;
            if (!TryParseMapPreviewColor(StudioInspectorView.MapSunGroundColorTextBox.Text, out Vector4 ground))
                ground = new Vector4(0.1f, 0.1f, 0.1f, 1f);

            _mapSunPreviewOverride = new MapSunPreviewOverride(
                MapPreviewSemantics.SunDirection(
                    (float)StudioInspectorView.MapSunAzimuthSlider.Value,
                    (float)StudioInspectorView.MapSunElevationSlider.Value),
                color,
                (float)StudioInspectorView.MapSunStrengthSlider.Value,
                sky,
                ground,
                (float)StudioInspectorView.MapSunAmbientSlider.Value);
            MarkMapLightingDirty();
        }

        private void SyncMapPreviewControls()
        {
            if (_mapSceneRuntime == null || StudioInspectorView.MapSunAzimuthSlider == null)
                return;

            _isUpdatingMapPreviewControls = true;
            try
            {
                MapSunPreviewOverride sun = _mapSunPreviewOverride ??
                    MapPreviewSemantics.OwnSun(_mapSceneRuntime.Scene.Sun);
                (float azimuth, float elevation) = MapPreviewSemantics.SunAngles(sun.Direction);
                StudioInspectorView.MapSunAzimuthSlider.Value = azimuth;
                StudioInspectorView.MapSunElevationSlider.Value = elevation;
                StudioInspectorView.MapSunStrengthSlider.Value = sun.Strength;
                StudioInspectorView.MapSunAmbientSlider.Value = sun.Ambient;
                StudioInspectorView.MapSunColorTextBox.Text = FormatMapPreviewColor(sun.Color);
                StudioInspectorView.MapSunSkyColorTextBox.Text = FormatMapPreviewColor(sun.SkyColor);
                StudioInspectorView.MapSunGroundColorTextBox.Text = FormatMapPreviewColor(sun.GroundColor);
                UpdateColorSwatch(StudioInspectorView.MapSunColorSwatch, StudioInspectorView.MapSunColorTextBox.Text);
                UpdateColorSwatch(StudioInspectorView.MapSunSkyColorSwatch, StudioInspectorView.MapSunSkyColorTextBox.Text);
                UpdateColorSwatch(StudioInspectorView.MapSunGroundColorSwatch, StudioInspectorView.MapSunGroundColorTextBox.Text);

                MapPostEffectsData post = _hasMapPostEffectsOverride
                    ? _mapPostEffectsOverride ?? MapPreviewSemantics.NoPostEffects
                    : MapPreviewSemantics.OwnPostEffects(_mapSceneRuntime.Scene.PostEffects);
                StudioInspectorView.MapDepthFogEnabledCheck.IsChecked = post.DepthFog.Enabled;
                StudioInspectorView.MapDepthFogColorTextBox.Text = FormatMapPreviewColor(post.DepthFog.Color);
                UpdateColorSwatch(StudioInspectorView.MapDepthFogColorSwatch, StudioInspectorView.MapDepthFogColorTextBox.Text);
                StudioInspectorView.MapDepthFogStartSlider.Value = post.DepthFog.Start;
                StudioInspectorView.MapDepthFogEndSlider.Value = post.DepthFog.End;
                StudioInspectorView.MapDepthFogMaxSlider.Value = post.DepthFog.MaxIntensity;
                StudioInspectorView.MapHeightFogEnabledCheck.IsChecked = post.HeightFog.Enabled;
                StudioInspectorView.MapHeightFogColorTextBox.Text = FormatMapPreviewColor(post.HeightFog.Color);
                UpdateColorSwatch(StudioInspectorView.MapHeightFogColorSwatch, StudioInspectorView.MapHeightFogColorTextBox.Text);
                StudioInspectorView.MapHeightFogStartSlider.Value = post.HeightFog.Start;
                StudioInspectorView.MapHeightFogEndSlider.Value = post.HeightFog.End;
                StudioInspectorView.MapHeightFogMaxSlider.Value = post.HeightFog.MaxIntensity;
                StudioInspectorView.MapDofEnabledCheck.IsChecked = post.DepthOfField.Enabled;
                StudioInspectorView.MapDofFocalSlider.Value = post.DepthOfField.FocalDistance;
                StudioInspectorView.MapDofWidthSlider.Value = post.DepthOfField.InFocusWidth;
                StudioInspectorView.MapDofCocSlider.Value = post.DepthOfField.Coc;

                MapSsaoPreviewOverride ssao = _mapSsaoPreviewOverride ??
                    MapPreviewSemantics.OwnSsao(_mapSceneRuntime.Scene.AmbientOcclusion);
                StudioInspectorView.MapSsaoEnabledCheck.IsChecked = ssao.Enabled;
                StudioInspectorView.MapSsaoQualityCombo.SelectedIndex = ssao.Settings.SampleQuality == 0 ? 0 : 1;
                StudioInspectorView.MapSsaoRadiusSlider.Value = ssao.Settings.SampleRadius;
                StudioInspectorView.MapSsaoBiasSlider.Value = ssao.Settings.Bias;
                StudioInspectorView.MapSsaoPowerSlider.Value = ssao.Settings.Power;
                StudioInspectorView.MapSsaoIntensitySlider.Value = ssao.Settings.Intensity;
                StudioInspectorView.MapSsaoBufferScaleSlider.Value = ssao.Settings.BufferScale;
                StudioInspectorView.MapSsaoEdgeAwareCheck.IsChecked = ssao.Settings.EdgeAwareBlur;
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
