using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AssetsManager.Services.Hashes;

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

        public uint Version { get; }
        public IReadOnlyList<ulong> TextureHashes { get; }
        public IReadOnlyList<ImageAutoAtlasSprite> Sprites { get; }

        public ImageAutoAtlas(uint version, IReadOnlyList<ulong> textureHashes, IReadOnlyList<ImageAutoAtlasSprite> sprites)
        {
            Version = version;
            TextureHashes = textureHashes ?? Array.Empty<ulong>();
            Sprites = sprites ?? Array.Empty<ImageAutoAtlasSprite>();
        }

        public static bool IsImaa(byte[] data) =>
            data != null && data.Length >= 4 && data[0] == 0x49 && data[1] == 0x4D && data[2] == 0x41 && data[3] == 0x41;

        public static bool IsImaa(ReadOnlySpan<byte> data) =>
            data.Length >= 4 && data[0] == 0x49 && data[1] == 0x4D && data[2] == 0x41 && data[3] == 0x41;

        public static bool TryRead(byte[] data, out ImageAutoAtlas atlas)
        {
            atlas = null;
            if (!IsImaa(data) || data.Length < 24)
                return false;

            try
            {
                using var ms = new MemoryStream(data, writable: false);
                using var reader = new BinaryReader(ms);

                string magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
                if (magic != MagicHeader) return false;

                uint version = reader.ReadUInt32();
                var textures = new List<ulong>();

                if (version == 2)
                {
                    ulong tex0 = reader.ReadUInt64();
                    ulong tex1 = reader.ReadUInt64();
                    if (tex0 != 0) textures.Add(tex0);
                    if (tex1 != 0) textures.Add(tex1);
                }
                else
                {
                    uint textureCount = reader.ReadUInt32();
                    for (int i = 0; i < textureCount; i++)
                    {
                        if (ms.Position + 8 > ms.Length) break;
                        textures.Add(reader.ReadUInt64());
                    }
                }

                if (ms.Position + 4 > ms.Length) return false;
                uint spriteCount = reader.ReadUInt32();
                var sprites = new List<ImageAutoAtlasSprite>((int)Math.Min(spriteCount, 100_000));

                for (int i = 0; i < spriteCount; i++)
                {
                    if (ms.Position + 28 > ms.Length) break;
                    ulong spriteHash = reader.ReadUInt64();
                    float uMin = reader.ReadSingle();
                    float vMin = reader.ReadSingle();
                    float uMax = reader.ReadSingle();
                    float vMax = reader.ReadSingle();
                    uint texIndex = reader.ReadUInt32();

                    sprites.Add(new ImageAutoAtlasSprite(spriteHash, uMin, vMin, uMax, vMax, texIndex));
                }

                atlas = new ImageAutoAtlas(version, textures, sprites);
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
            sb.AppendLine($"# Image Auto Atlas ({MagicHeader} v{Version})");
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
