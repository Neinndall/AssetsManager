using System;
using System.Collections.Generic;

namespace AssetsManager.Services.Hashes.Guessers
{
    internal sealed class HashCorpusIndex
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, object> _derived = new(StringComparer.Ordinal);

        internal HashCorpusIndex(long revision, IReadOnlyList<string> paths)
        {
            Revision = revision;
            Paths = paths;
        }

        internal long Revision { get; }
        internal IReadOnlyList<string> Paths { get; }

        internal T GetOrCreate<T>(string key, Func<IReadOnlyList<string>, T> factory)
        {
            lock (_sync)
            {
                if (_derived.TryGetValue(key, out object value)) return (T)value;
                T created = factory(Paths);
                _derived.Add(key, created);
                return created;
            }
        }
    }
}
