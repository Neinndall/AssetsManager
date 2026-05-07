using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using AssetsManager.Views.Models.Audio;
using AssetsManager.Utils.Framework;
using AssetsManager.Utils;

namespace AssetsManager.Services.Audio
{
    public class AudioPlayerService
    {
        private readonly AppSettings _settings;

        public ObservableRangeCollection<AudioPlaylistItem> Playlist { get; } = new ObservableRangeCollection<AudioPlaylistItem>();
        
        public string ActivePackName { get; set; } = "New Playlist";

        public event EventHandler<AudioPlaylistItem> RequestPlay;

        public AudioPlayerService(AppSettings settings)
        {
            _settings = settings;
        }

        public void AddToPlaylist(string name, string url, string originalUrl = null, byte[] data = null)
        {
            if (Playlist.Any(x => x.Url == url)) return;

            var item = new AudioPlaylistItem
            {
                Name = name,
                Url = url,
                OriginalUrl = originalUrl ?? url,
                Data = data,
                AddedAt = DateTime.Now
            };

            Playlist.Add(item);
        }

        public void RemoveFromPlaylist(AudioPlaylistItem item)
        {
            Playlist.Remove(item);
        }

        public void ClearPlaylist()
        {
            Playlist.Clear();
            ActivePackName = "New Playlist";
        }

        public void PlayItem(AudioPlaylistItem item)
        {
            RequestPlay?.Invoke(this, item);
        }

        public void SavePlaylist(string name)
        {
            var existing = _settings.AudioPlaylists.FirstOrDefault(x => x.Name == name);
            var pack = new AudioPlaylistPack
            {
                Name = name,
                Tracks = Playlist.Select(x => new SavedAudioTrack { Name = x.Name, Url = x.Url, OriginalUrl = x.OriginalUrl }).ToList()
            };

            if (existing != null)
            {
                _settings.AudioPlaylists.Remove(existing);
            }
            
            _settings.AudioPlaylists.Add(pack);
            ActivePackName = name;
            _settings.Save();
        }

        public void LoadPlaylist(string name)
        {
            var pack = _settings.AudioPlaylists.FirstOrDefault(x => x.Name == name);
            if (pack == null) return;

            Playlist.Clear();
            foreach (var track in pack.Tracks)
            {
                Playlist.Add(new AudioPlaylistItem
                {
                    Name = track.Name,
                    Url = track.Url,
                    OriginalUrl = track.OriginalUrl ?? track.Url,
                    AddedAt = DateTime.Now
                });
            }
            ActivePackName = name;
        }

        public void DeletePlaylist(string name)
        {
            var pack = _settings.AudioPlaylists.FirstOrDefault(x => x.Name == name);
            if (pack != null)
            {
                _settings.AudioPlaylists.Remove(pack);
                _settings.Save();
                if (ActivePackName == name) ActivePackName = "New Playlist";
            }
        }
    }

    public class AudioPlaylistItem
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public string OriginalUrl { get; set; }
        public byte[] Data { get; set; }
        public DateTime AddedAt { get; set; }
    }
}
