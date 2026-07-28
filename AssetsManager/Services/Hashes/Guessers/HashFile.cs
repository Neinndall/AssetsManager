using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Hashes.Guessers
{
    internal sealed class HashFile
    {
        private readonly object _sync = new();
        private Dictionary<ulong, string> _hashes;
        private IReadOnlyList<string> _paths;
        private DateTime _lastWriteUtc;
        private long _length = -1;
        private long _revision;

        internal HashFile(HashGuessDomain domain, string path)
        {
            Domain = domain;
            Path = path ?? throw new ArgumentNullException(nameof(path));
        }

        internal HashFile(HashGuessDomain domain, IEnumerable<string> paths)
        {
            Domain = domain;
            Path = string.Empty;
            _hashes = paths.Select(PathUtils.NormalizePath)
                .Where(path => path.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(path => XxHash64Ext.Hash(path), path => path);
            _paths = _hashes.Values.ToArray();
            _revision = 1;
        }

        internal HashGuessDomain Domain { get; }
        internal string Path { get; }
        internal long Revision { get { lock (_sync) return _revision; } }

        internal IReadOnlyDictionary<ulong, string> Load(bool force = false)
        {
            lock (_sync)
            {
                if (Path.Length == 0) return _hashes ?? new Dictionary<ulong, string>();

                var info = new FileInfo(Path);
                bool unchanged = info.Exists
                    ? info.LastWriteTimeUtc == _lastWriteUtc && info.Length == _length
                    : _length == -1;
                if (!force && _hashes != null && unchanged) return _hashes;

                var hashes = new Dictionary<ulong, string>();
                if (File.Exists(Path))
                {
                    foreach (string line in File.ReadLines(Path))
                    {
                        int separator = line.IndexOf(' ');
                        if (separator <= 0 || separator == line.Length - 1) continue;
                        if (!ulong.TryParse(line.AsSpan(0, separator), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash)) continue;
                        string value = PathUtils.NormalizePath(line[(separator + 1)..]);
                        if (value.Length > 0) hashes[hash] = value;
                    }
                }

                _hashes = hashes;
                _paths = hashes.Values.ToArray();
                _revision++;
                info.Refresh();
                _lastWriteUtc = info.Exists ? info.LastWriteTimeUtc : default;
                _length = info.Exists ? info.Length : -1;
                return _hashes;
            }
        }

        internal IReadOnlyList<string> LoadPaths(bool force = false)
        {
            lock (_sync)
            {
                Load(force);
                return _paths ?? Array.Empty<string>();
            }
        }

        internal static HashSet<ulong> LoadUnknownFromExport(string directory)
        {
            var unknown = new HashSet<ulong>();
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return unknown;
            foreach (string file in Directory.EnumerateFiles(directory, "*.unknown.txt", SearchOption.TopDirectoryOnly))
            foreach (string line in File.ReadLines(file))
                if (ulong.TryParse(line.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash)) unknown.Add(hash);
            return unknown;
        }

        internal void Invalidate()
        {
            lock (_sync)
            {
                _hashes = null;
                _paths = null;
                _lastWriteUtc = default;
                _length = -1;
                _revision++;
            }
        }
    }
}
