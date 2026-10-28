using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AssetsManager.Services.Hashes;
using AssetsManager.Utils;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Parsers
{
    public readonly record struct ImageAutoAtlasSprite(
        ulong SpriteHash,
        float UMin,
        float VMin,
        float UMax,
        float VMax,
        uint TextureIndex);

    public sealed class ImageAutoAtlas
    {
        public const string MagicHeader = "IMAA";

        public IReadOnlyList<ulong> TextureHashes { get; }
        public IReadOnlyList<ImageAutoAtlasSprite> Sprites { get; }

        public ImageAutoAtlas(IReadOnlyList<ulong> textureHashes, IReadOnlyList<ImageAutoAtlasSprite> sprites)
        {
            TextureHashes = textureHashes ?? Array.Empty<ulong>();
            Sprites = sprites ?? Array.Empty<ImageAutoAtlasSprite>();
        }

        public static bool IsAtlas(byte[] data) => IsImaa(data);

        public static bool IsAtlas(ReadOnlySpan<byte> data) => IsImaa(data);

        public static bool IsImaa(byte[] data) =>
            data != null && data.Length >= 4 && data[0] == 0x49 && data[1] == 0x4D && data[2] == 0x41 && data[3] == 0x41;

        public static bool IsImaa(ReadOnlySpan<byte> data) =>
            data.Length >= 4 && data[0] == 0x49 && data[1] == 0x4D && data[2] == 0x41 && data[3] == 0x41;

        public static bool TryRead(byte[] data, out ImageAutoAtlas atlas)
        {
            atlas = null;
            if (data == null || data.Length < 12 || !IsImaa(data))
                return false;

            try
            {
                using var ms = new MemoryStream(data, writable: false);
                using var reader = new BinaryReader(ms);

                string magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
                if (magic != MagicHeader) return false;

                uint textureCount = reader.ReadUInt32();
                if (textureCount > (ms.Length - ms.Position - 4) / 8) return false;
                var textures = new List<ulong>((int)textureCount);
                for (int i = 0; i < textureCount; i++)
                    textures.Add(reader.ReadUInt64());

                if (ms.Position + 4 > ms.Length) return false;
                uint spriteCount = reader.ReadUInt32();
                if ((long)spriteCount * 28 != ms.Length - ms.Position) return false;
                var sprites = new List<ImageAutoAtlasSprite>((int)spriteCount);

                for (int i = 0; i < spriteCount; i++)
                {
                    ulong spriteHash = reader.ReadUInt64();
                    float uMin = reader.ReadSingle();
                    float vMin = reader.ReadSingle();
                    float uMax = reader.ReadSingle();
                    float vMax = reader.ReadSingle();
                    uint texIndex = reader.ReadUInt32();
                    if (texIndex >= textureCount) return false;

                    sprites.Add(new ImageAutoAtlasSprite(spriteHash, uMin, vMin, uMax, vMax, texIndex));
                }

                atlas = new ImageAutoAtlas(textures, sprites);
                return true;
            }
            catch
            {
                atlas = null;
                return false;
            }
        }

        public string ToRitobinText(HashResolverService hashResolver)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Image Auto Atlas ({MagicHeader}, {TextureHashes.Count} texture pages)");
            sb.AppendLine();
            sb.AppendLine("textures: list[string] = {");
            var textureNames = new Dictionary<uint, string>();
            for (int i = 0; i < TextureHashes.Count; i++)
            {
                ulong texHash = TextureHashes[i];
                string texName = hashResolver?.ResolveHash(texHash);
                if (string.IsNullOrEmpty(texName)) texName = $"0x{texHash:x16}";
                textureNames[(uint)i] = texName;
                sb.AppendLine($"  \"{texName}\"");
            }
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("sprites: map[hash, struct] = {");

            foreach (var sprite in Sprites)
            {
                string spriteName = hashResolver?.ResolveHash(sprite.SpriteHash);
                string spriteKey = string.IsNullOrEmpty(spriteName) ? $"0x{sprite.SpriteHash:x16}" : $"\"{spriteName}\"";
                string targetTex = textureNames.TryGetValue(sprite.TextureIndex, out string resolvedTex)
                    ? resolvedTex
                    : $"texture_{sprite.TextureIndex}";

                sb.AppendLine($"  {spriteKey}: ImageAutoAtlasSprite = {{");
                sb.AppendLine($"    texture: string = \"{targetTex}\"");
                sb.AppendLine($"    uvMin: vec2 = [{sprite.UMin.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)}, {sprite.VMin.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)}]");
                sb.AppendLine($"    uvMax: vec2 = [{sprite.UMax.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)}, {sprite.VMax.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)}]");
                sb.AppendLine("  }");
            }

            sb.AppendLine("}");
            return sb.ToString();
        }
    }
}
