using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class StudioControl
    {
        internal void EmitterLane_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: VfxEmitterDiagnosticItem item }) return;
            _model.SelectedEmitter = item;
            Focus();
        }

        internal void EmitterSolo_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: VfxEmitterDiagnosticItem item })
                ToggleEmitterSolo(item);
        }

        internal void EmitterMute_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: VfxEmitterDiagnosticItem item })
                ToggleEmitterMuted(item);
        }

        private void ToggleEmitterSolo(VfxEmitterDiagnosticItem item)
        {
            if (item == null) return;
            _vfxRenderer?.SetAllEmittersVisibility(true);
            item.IsSolo = !item.IsSolo;
        }

        private void ToggleEmitterMuted(VfxEmitterDiagnosticItem item)
        {
            if (item == null) return;
            _vfxRenderer?.SetAllEmittersVisibility(true);
            item.IsMuted = !item.IsMuted;
        }

        internal void ToggleSoloAll_Click(object sender, RoutedEventArgs e)
        {
            if (_model.Emitters.Count == 0) return;
            bool newSolo = !_model.Emitters.All(emitter => emitter.IsSolo);
            _vfxRenderer?.SetAllEmittersVisibility(true);
            try
            {
                _isBulkEmitterStateChange = true;
                foreach (VfxEmitterDiagnosticItem emitter in _model.Emitters)
                    emitter.IsSolo = newSolo;
            }
            finally
            {
                _isBulkEmitterStateChange = false;
            }
            UpdateEmittersVisibility();
        }

        internal void ToggleMuteAll_Click(object sender, RoutedEventArgs e)
        {
            if (_model.Emitters.Count == 0) return;
            bool newMute = !_model.Emitters.All(emitter => emitter.IsMuted);
            try
            {
                _isBulkEmitterStateChange = true;
                foreach (VfxEmitterDiagnosticItem emitter in _model.Emitters)
                    emitter.IsMuted = newMute;
            }
            finally
            {
                _isBulkEmitterStateChange = false;
            }

            _vfxRenderer?.SetAllEmittersVisibility(!newMute);
            UpdateEmittersVisibility();
        }

        private void UpdateEmittersVisibility()
        {
            bool hasSolo = _model.Emitters.Any(em => em.IsSolo);
            _model.HasAnySolo = hasSolo;

            foreach (var emitter in _model.Emitters)
            {
                bool visible;
                if (hasSolo)
                {
                    visible = emitter.IsSolo && !emitter.IsMuted;
                }
                else
                {
                    visible = !emitter.IsMuted;
                }
                emitter.IsEnabled = visible;
                _vfxRenderer?.SetEmitterVisibility(emitter.SourceOrder, visible);
            }

            _model.IsAllMuted = _model.Emitters.Count > 0 && _model.Emitters.All(em => em.IsMuted);
        }

        internal void EmitterFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyEmitterFilter();
        }

        internal void ClearEmitterFilter_Click(object sender, RoutedEventArgs e)
        {
            if (_model != null)
            {
                _model.EmitterFilterText = string.Empty;
            }
            ApplyEmitterFilter();
        }

        private void ApplyEmitterFilter()
        {
            if (_model == null) return;
            string filter = _model.EmitterFilterText?.Trim() ?? string.Empty;
            var view = CollectionViewSource.GetDefaultView(_model.Emitters);
            if (view != null)
            {
                if (string.IsNullOrWhiteSpace(filter))
                {
                    view.Filter = null;
                }
                else
                {
                    view.Filter = obj =>
                    {
                        if (obj is VfxEmitterDiagnosticItem item)
                        {
                            return (item.Name != null && item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) ||
                                   item.IndexNumber.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                   (item.PrimitiveKindName != null && item.PrimitiveKindName.Contains(filter, StringComparison.OrdinalIgnoreCase));
                        }
                        return false;
                    };
                }
            }
        }

        private const double RulerLoopDragThreshold = 4d;
        private bool _isRulerDragging;
        private bool _isRulerCreatingLoop;
        private double _rulerPressX;

        private static string DescribeTextureSources(VfxEmitterDefinition emitter)
        {
            if (emitter is null) return "N/A";

            var sources = new List<string>();
            bool hasVisualTexture = !string.IsNullOrWhiteSpace(emitter.TexturePath) ||
                                    !string.IsNullOrWhiteSpace(emitter.TextureMultPath);
            AddTextureSource(sources, "Base", emitter.TexturePath);
            AddTextureSource(sources, "Mult", emitter.TextureMultPath);
            AddTextureSource(sources, "Distortion", emitter.Distortion?.NormalMapTexturePath);
            AddTextureSource(sources, "Erosion", emitter.AlphaErosion?.TexturePath);
            AddTextureSource(sources, "Reflection", emitter.Reflection?.TexturePath);
            AddTextureSource(sources, "Palette", emitter.PaletteDefinition?.PaletteTexturePath);
            if (!hasVisualTexture || !string.Equals(
                    emitter.ParticleColorTexturePath,
                    "ASSETS/Shared/Particles/DefaultColorOverlifetime.dds",
                    StringComparison.OrdinalIgnoreCase))
            {
                AddTextureSource(sources, "Color LUT", emitter.ParticleColorTexturePath);
            }
            return sources.Count == 0 ? "N/A" : string.Join(" | ", sources);
        }

        private static (string Status, Brush Brush) DescribeTextureStatus(
            VfxEmitterDefinition emitter,
            BitmapSource texture)
        {
            if (texture != null) return ("Resolved", Brushes.LightGreen);
            if (!string.IsNullOrWhiteSpace(emitter.TexturePath)) return ("MISSING", Brushes.OrangeRed);
            if (!string.IsNullOrWhiteSpace(emitter.TextureMultPath)) return ("Mult stage", Brushes.DarkOrange);
            if (!string.IsNullOrWhiteSpace(emitter.Distortion?.NormalMapTexturePath))
                return ("Distortion stage", Brushes.DarkOrange);
            if (!string.IsNullOrWhiteSpace(emitter.ParticleColorTexturePath))
                return ("LUT only", Brushes.DarkOrange);
            return ("None", Brushes.Gray);
        }

        private static void AddTextureSource(List<string> sources, string role, string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
                sources.Add($"{role}: {path}");
        }
    }
}
