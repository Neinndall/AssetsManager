using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Parsing;

/// <summary>Character skin classes supported by the shared model and VFX preview pipeline.</summary>
internal static class CharacterSkinSchema
{
    private static readonly uint Skin = Fnv1a.HashLower("SkinCharacterDataProperties");
    private static readonly uint TftSkin = Fnv1a.HashLower("TftSkinCharacterDataProperties");

    internal static bool IsSkin(uint classHash) => classHash == Skin || classHash == TftSkin;
}
