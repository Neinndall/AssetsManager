using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class VfxInspectorControl
    {
        private bool HasSelectedSystemReady()
            => _model.SelectedSystem != null &&
               _pendingSystem == null &&
               ReferenceEquals(_inspectedSystem, _model.SelectedSystem) &&
               _vfxRenderer?.ActiveSystem != null;

        private bool HasSelectedSpellReady()
            => _model.SelectedSpell != null &&
               _activeSpellPlan?.Availability == VfxSpellAvailability.Supported &&
               _vfxRenderer?.ActiveSystem != null;

        private bool HasSelectedAnimationReady()
            => _model.IsAnimationMode &&
               _model.SelectedAnimation != null &&
               SameAnimationClip(_activeAnimationClip, _model.SelectedAnimation) &&
               _activeAnimationClip?.AnimationAsset != null &&
               _vfxRenderer?.ActiveSystem != null;

        private bool TryPlaySelectedTimedPreview(bool restartWhenPlaying)
        {
            bool ended = _model.CurrentTime >= _model.TotalDuration;
            bool restart = ended || (restartWhenPlaying && _model.IsPlaying);
            if (_model.SelectedMapNode?.Kind == MapBrowserNodeKind.Clip &&
                _model.SelectedMapNode.Payload is MapCharacterClipSelection mapClip)
            {
                if (!HasSelectedMapClipReady())
                    _ = PlayMapCharacterClipAsync(mapClip);
                else
                {
                    if (restart)
                        SeekTimeline(0d);
                    _model.IsPlaying = true;
                    _vfxRenderer?.Play();
                }
                return true;
            }

            if (_model.SelectedSpell != null)
            {
                if (!HasSelectedSpellReady())
                    RequestSpellPreview(_model.SelectedSpell);
                else
                    ResumeTimedPreview(restart);
                return true;
            }

            if (_model.IsAnimationMode && _model.SelectedAnimation != null)
            {
                if (_model.SelectedAnimation.IsBindPose)
                    return false;
                if (!HasSelectedAnimationReady())
                    _ = PlaySelectedAnimationAsync(_model.SelectedAnimation);
                else
                    ResumeTimedPreview(restart);
                return true;
            }

            return false;
        }

        private void ResumeTimedPreview(bool restartFromBeginning)
        {
            if (restartFromBeginning)
                SeekTimeline(0d);
            _model.IsPlaying = true;
            _vfxRenderer?.Play();
        }

        private void Play_Click(object sender, RoutedEventArgs e)
        {
            if (TryPlaySelectedTimedPreview(restartWhenPlaying: true))
                return;

            if (_model.SelectedSystem != null)
            {
                if (!HasSelectedSystemReady())
                {
                    RequestSystemInspection(_model.SelectedSystem);
                }
                else
                {
                    _vfxRenderer.Stop();
                    _model.CurrentTime = 0;
                    _vfxRenderer.Seek(0);
                    _vfxRenderer.Play();
                    _model.IsPlaying = true;
                }
            }
        }

        private void StopResume_Click(object sender, RoutedEventArgs e)
        {
            if (_model.IsPlaying)
            {
                _vfxRenderer?.Pause();
                _model.IsPlaying = false;
            }
            else
            {
                if (TryPlaySelectedTimedPreview(restartWhenPlaying: false))
                    return;

                if (_model.SelectedSystem != null)
                {
                    if (!HasSelectedSystemReady())
                    {
                        RequestSystemInspection(_model.SelectedSystem);
                    }
                    else if (_vfxRenderer.PlaybackTime >= _model.TotalDuration)
                    {
                        _vfxRenderer.Stop();
                        _vfxRenderer.Play();
                        _model.IsPlaying = true;
                    }
                    else
                    {
                        _vfxRenderer.Play();
                        _model.IsPlaying = true;
                    }
                }
            }
        }

        private bool _isUserSeeking;

        private void TimeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_model.IsPlaying || _isUserSeeking)
                SeekTimeline(e.NewValue);
        }

        private void TimeSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _isUserSeeking = true;
        }

        private void TimeSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isUserSeeking = false;
        }

        private void Speed_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_model == null) return;
            if (SpeedComboBox?.SelectedItem is ComboBoxItem item &&
                float.TryParse(item.Tag?.ToString(), System.Globalization.CultureInfo.InvariantCulture, out float speed))
            {
                SetPlaybackSpeed(speed, updateControl: false);
            }
        }

        private void ChancePinButton_Click(object sender, RoutedEventArgs e)
        {
            if (_model?.HasStandaloneSystem != true || ChancePinPopup == null ||
                Environment.TickCount64 - _chancePinClosedTicks < 250) return;
            SyncChancePinControls();
            CloseAllToolbarPopups(ChancePinPopup);
            ChancePinPopup.IsOpen = !ChancePinPopup.IsOpen;
        }

        private void ChancePinPopup_Closed(object sender, EventArgs e)
            => _chancePinClosedTicks = Environment.TickCount64;

        private void TimelineOptionsButton_Click(object sender, RoutedEventArgs e)
        {
            if (TimelineOptionsPopup == null || Environment.TickCount64 - _timelineOptionsClosedTicks < 250) return;
            CloseAllToolbarPopups(TimelineOptionsPopup);
            TimelineOptionsPopup.IsOpen = !TimelineOptionsPopup.IsOpen;
        }

        private void TimelineOptionsPopup_Closed(object sender, EventArgs e)
            => _timelineOptionsClosedTicks = Environment.TickCount64;

        private void SyncChancePinControls()
        {
            if (ChancePinToggle == null || ChancePinSlider == null) return;
            _isUpdatingChancePinControls = true;
            try
            {
                float? chance = _vfxRenderer?.PinnedBirthChance;
                ChancePinToggle.IsChecked = chance.HasValue;
                if (chance.HasValue) ChancePinSlider.Value = chance.Value;
                ChancePinValueText.Text = ChancePinSlider.Value.ToString("F2", CultureInfo.InvariantCulture);
            }
            finally
            {
                _isUpdatingChancePinControls = false;
            }
        }

        private void ChancePinToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_model?.HasStandaloneSystem != true || _vfxRenderer == null) return;
            if (ChancePinToggle.IsChecked == true)
                ApplyPinnedBirthChance((float)ChancePinSlider.Value);
            else
                _vfxRenderer.SetPinnedBirthChance(null);
        }

        private void ChancePinSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingChancePinControls || !IsLoaded || ChancePinToggle?.IsChecked != true ||
                _model?.HasStandaloneSystem != true || _vfxRenderer == null) return;
            ApplyPinnedBirthChance((float)e.NewValue);
        }

        private void ApplyPinnedBirthChance(float chance)
        {
            chance = Math.Clamp(chance, 0f, 1f);
            _vfxRenderer?.SetPinnedBirthChance(chance);
            if (ChancePinValueText != null)
                ChancePinValueText.Text = chance.ToString("F2", CultureInfo.InvariantCulture);
        }

        private void SetPlaybackSpeed(double speed, bool updateControl = true)
        {
            float normalized = VfxRenderSession.NormalizePlaybackSpeed(speed);
            _model.Speed = normalized;
            if (_vfxRenderer?.ActiveSystem != null)
                _vfxRenderer.ActiveSystem.Speed = normalized;

            if (!updateControl || SpeedComboBox == null) return;
            for (int index = 0; index < SpeedComboBox.Items.Count; index++)
            {
                if (SpeedComboBox.Items[index] is not ComboBoxItem item ||
                    !double.TryParse(
                        item.Tag?.ToString(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double candidate))
                {
                    continue;
                }

                if (Math.Abs(candidate - normalized) <= 1e-6)
                {
                    SpeedComboBox.SelectedIndex = index;
                    break;
                }
            }
        }

        internal static float ResolveSimulationFrameDelta(TimeSpan delta, bool discard)
            => discard ? 0f : (float)Math.Max(0d, delta.TotalSeconds);

        internal static double PlaybackSpeedDetent(double speed, int direction)
        {
            if (direction >= 0)
            {
                foreach (double detent in PlaybackSpeedDetents)
                    if (detent > speed + 1e-6) return detent;
                return PlaybackSpeedDetents[^1];
            }

            for (int index = PlaybackSpeedDetents.Length - 1; index >= 0; index--)
                if (PlaybackSpeedDetents[index] < speed - 1e-6) return PlaybackSpeedDetents[index];
            return PlaybackSpeedDetents[0];
        }

        private void BgMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_model == null) return;
            if (BgComboBox?.SelectedItem is ComboBoxItem item)
            {
                _model.BgMode = item.Tag?.ToString() ?? item.Content?.ToString() ?? "Dark";
            }
        }

        private void CopyDebugReport_Click(object sender, RoutedEventArgs e)
        {
            if (_model.SelectedSystem == null)
            {
                MessageBox.Show("Selecciona primero un sistema VFX de la lista para generar el reporte de depuración.", "VFX Inspector", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# INFORME COMPLETO DE DIAGNÓSTICO DE VISUALIZACIÓN VFX");
            sb.AppendLine($"Fecha/Hora: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Sistema VFX: {_model.SelectedSystem.Name}");
            sb.AppendLine($"Ruta Partícula: {_model.SelectedSystem.Definition?.ParticlePath ?? "N/A"}");
            sb.AppendLine($"Hash de Ruta: 0x{_model.SelectedSystem.Definition?.PathHash ?? 0:X8}");
            sb.AppendLine($"Duración Calculada: {_model.TotalDuration:F2} s");
            sb.AppendLine($"Emisores Totales: {_model.Emitters.Count}");
            sb.AppendLine($"Texturas Cargadas: {_model.Textures.Count}");
            sb.AppendLine();

            sb.AppendLine("## EMISORES Y PROPIEDADES DE RENDERIZADO");
            int idx = 1;
            foreach (var emitter in _model.Emitters)
            {
                var d = emitter.EmitterDef;
                sb.AppendLine($"### Emisor {idx++}: {emitter.Name}");
                sb.AppendLine($"  - Estado: {(emitter.IsEnabled ? "ACTIVO" : "DESACTIVADO")}");
                sb.AppendLine($"  - Modo Mezcla (BlendMode): {emitter.BlendMode} (Valor Original BIN: {d?.BlendMode})");
                sb.AppendLine($"  - Tipo Primitiva: {(d?.IsMeshPrimitive == true ? "MALLA 3D (.scb/.sco)" : (d?.IsGroundLayer == true ? "CAPA SUELO 3D" : "QUAD BILLBOARD 2D"))}");
                sb.AppendLine($"  - Malla 3D Ruta: {emitter.MeshPath} (Estado GPU: {emitter.MeshStatus})");
                sb.AppendLine($"  - Textura Principal: {emitter.TexturePath} (Estado GPU: {emitter.TextureStatus})");
                sb.AppendLine($"  - Textura Multiplicadora: {d?.TextureMultPath ?? "N/A"}");
                sb.AppendLine($"  - Textura Color Lookup: {d?.ParticleColorTexturePath ?? "N/A"}");
                sb.AppendLine($"  - Textura Paleta: {d?.PaletteDefinition?.PaletteTexturePath ?? "N/A"}");
                sb.AppendLine($"  - Rejilla Atlas (TexDiv): {emitter.TexDiv}");
                if (d != null)
                {
                    var bs = d.BirthScale.Constant;
                    sb.AppendLine($"  - Escala Inicial (BirthScale): X={bs.X:F1}, Y={bs.Y:F1}, Z={bs.Z:F1}");
                    sb.AppendLine($"  - Bucle Infinito (IsLoop): {d.IsLoop}");
                    sb.AppendLine($"  - Emisor Único (IsSingleParticle): {d.IsSingleParticle}");
                    sb.AppendLine($"  - Flags Orientación: OrientadoDirección={d.IsDirectionOriented}, CuadriláteroArbitrario={d.IsArbitraryQuad}, Terreno={d.IsFollowingTerrain}, Suelo={d.IsGroundLayer}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("## TEXTURAS EN MEMORIA GPU");
            foreach (var tex in _model.Textures)
            {
                sb.AppendLine($"  - {tex.AuthoredPath} => [{tex.Width}x{tex.Height}] ({tex.Status})");
            }

            string reportText = sb.ToString();
            Clipboard.SetText(reportText);
            _model.LogMessages.Add("[DEBUG EXPORT] Reporte completo de depuración copiado al Portapapeles.");
            MessageBox.Show("¡Reporte de Depuración Completo copiado al Portapapeles de Windows!\n\nPuedes pegarlo directamente en la conversación para que analicemos cualquier anomalía visual.", "VFX Inspector", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
