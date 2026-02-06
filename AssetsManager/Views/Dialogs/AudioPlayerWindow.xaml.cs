using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AssetsManager.Services.Audio;
using System.Windows.Threading;

namespace AssetsManager.Views.Dialogs
{
    public partial class AudioPlayerWindow : Window
    {
        private readonly AudioPlayerService _audioPlayerService;
        private readonly MediaPlayer _mediaPlayer = new MediaPlayer();
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private bool _isDragging = false;

        public AudioPlayerWindow(AudioPlayerService audioPlayerService)
        {
            InitializeComponent();
            _audioPlayerService = audioPlayerService;
            this.DataContext = _audioPlayerService;

            _timer.Interval = TimeSpan.FromMilliseconds(500);
            _timer.Tick += Timer_Tick;

            _mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
            _mediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (!_isDragging && _mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                AudioSlider.Value = _mediaPlayer.Position.TotalSeconds;
            }
        }

        private void MediaPlayer_MediaOpened(object sender, EventArgs e)
        {
            if (_mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                AudioSlider.Maximum = _mediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
            }
            _timer.Start();
        }

        private void MediaPlayer_MediaEnded(object sender, EventArgs e)
        {
            _timer.Stop();
            AudioSlider.Value = 0;
            PlayIcon.Kind = Material.Icons.MaterialIconKind.Play;
        }

        private void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            var selectedItem = PlaylistListBox.SelectedItem as AudioPlaylistItem;
            if (selectedItem == null) return;

            if (PlayIcon.Kind == Material.Icons.MaterialIconKind.Play)
            {
                _mediaPlayer.Open(new Uri(selectedItem.Url));
                _mediaPlayer.Play();
                PlayIcon.Kind = Material.Icons.MaterialIconKind.Pause;
                CurrentTrackName.Text = selectedItem.Name;
            }
            else
            {
                _mediaPlayer.Pause();
                PlayIcon.Kind = Material.Icons.MaterialIconKind.Play;
            }
        }

        private void RemoveItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AudioPlaylistItem item)
            {
                _audioPlayerService.RemoveFromPlaylist(item);
            }
        }

        private void ClearPlaylist_Click(object sender, RoutedEventArgs e)
        {
            _audioPlayerService.ClearPlaylist();
            _mediaPlayer.Stop();
            CurrentTrackName.Text = "No track selected";
            PlayIcon.Kind = Material.Icons.MaterialIconKind.Play;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                foreach (string file in files)
                {
                    string ext = System.IO.Path.GetExtension(file).ToLower();
                    if (ext == ".mp3" || ext == ".wav" || ext == ".ogg")
                    {
                        _audioPlayerService.AddToPlaylist(System.IO.Path.GetFileName(file), file);
                    }
                }
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void Close_Click(object sender, RoutedEventArgs e)
        {
            _mediaPlayer.Stop();
            this.Close();
        }
    }
}
