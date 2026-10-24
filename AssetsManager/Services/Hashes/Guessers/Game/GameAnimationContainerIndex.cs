using System;
using System.Collections.Generic;

namespace AssetsManager.Services.Hashes.Guessers.Game;

internal sealed class GameAnimationContainerIndex
{
    private readonly Dictionary<string, HashSet<string>> _byCharacter = new(StringComparer.Ordinal);

    internal GameAnimationContainerIndex(IEnumerable<(string Character, string Container)> containers)
    {
        foreach (var (character, container) in containers)
        {
            if (!_byCharacter.TryGetValue(character, out var names))
                _byCharacter.Add(character, names = new HashSet<string>(StringComparer.Ordinal));
            names.Add(container);
            if (container == "base") names.Add("skin0");
        }
    }

    internal bool Contains(string[] folders)
    {
        if (folders.Length < 5 || folders[1] != "characters" ||
            !_byCharacter.TryGetValue(folders[2], out var names)) return false;
        // Preserve matches in every component after the character, including nested layouts.
        for (int i = 3; i < folders.Length; i++)
            if (names.Contains(folders[i])) return true;
        return false;
    }
}
