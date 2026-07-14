using System;
using System.Text;

namespace AssetsManager.Utils
{
    public static class FileTypeDetector
    {
        // File Signatures
        private static readonly byte[] DDS_SIGNATURE = { 0x44, 0x44, 0x53, 0x20 }; // "DDS "
        private static readonly byte[] PNG_SIGNATURE = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        private static readonly byte[] JPG_SIGNATURE = { 0xFF, 0xD8, 0xFF };
        private static readonly byte[] WEBP_SIGNATURE = { 0x52, 0x49, 0x46, 0x46 }; // RIFF
        private static readonly byte[] WEBP_VP8X_SIGNATURE = { 0x57, 0x45, 0x42, 0x50 }; // WEBP
        private static readonly byte[] OGG_SIGNATURE = { 0x4F, 0x67, 0x67, 0x53 }; // "OggS"
        private static readonly byte[] WEBM_SIGNATURE = { 0x1A, 0x45, 0xDF, 0xA3 }; // EBML header
        private static readonly byte[] WEM_SIGNATURE = { 0x52, 0x49, 0x46, 0x46 }; // RIFF
        private static readonly byte[] WEM_WAVE_SIGNATURE = { 0x57, 0x41, 0x56, 0x45 }; // WAVE
        private static readonly byte[] BNK_SIGNATURE = { 0x42, 0x4B, 0x48, 0x44 }; // "BKHD"
        private static readonly byte[] SKL_SIGNATURE = { 0x72, 0x33, 0x64, 0x32, 0x73, 0x6B, 0x6C, 0x74 }; // "r3d2sklt"
        private static readonly byte[] SKN_SIGNATURE = { 0x33, 0x22, 0x11, 0x00 };
        private static readonly byte[] R3D2MESH_SIGNATURE = { 0x72, 0x33, 0x64, 0x32, 0x4D, 0x65, 0x73, 0x68 }; // "r3d2Mesh"
        private static readonly byte[] ANM_R3D2ANMD_SIGNATURE = { 0x72, 0x33, 0x64, 0x32, 0x61, 0x6E, 0x6D, 0x64 }; // "r3d2anmd"
        private static readonly byte[] ANM_R3D2CANM_SIGNATURE = { 0x72, 0x33, 0x64, 0x32, 0x63, 0x61, 0x6E, 0x6D }; // "r3d2canm"
        private static readonly byte[] MAPGEO_SIGNATURE = { 0x4F, 0x45, 0x47, 0x4D }; // "OEGM"

        private static readonly byte[] SCO_SIGNATURE = { 0x5B, 0x4F, 0x62, 0x6A }; // "[Obj"
        private static readonly byte[] LUAQ_SIGNATURE = { 0x4C, 0x75, 0x61, 0x51 }; // "LuaQ"
        private static readonly byte[] PRELOAD_SIGNATURE = { 0x50, 0x72, 0x65, 0x4C, 0x6F, 0x61, 0x64 }; // "PreLoad"
        private static readonly byte[] RST_SIGNATURE = { 0x52, 0x53, 0x54 }; // "RST"
        private static readonly byte[] BIN_PROP_SIGNATURE = { 0x50, 0x52, 0x4F, 0x50 }; // "PROP"
        private static readonly byte[] BIN_PTCH_SIGNATURE = { 0x50, 0x54, 0x43, 0x48 }; // "PTCH"
        private static readonly byte[] ICO_SIGNATURE = { 0x00, 0x00, 0x01, 0x00 };
        private static readonly byte[] TEX_SIGNATURE = { 0x54, 0x45, 0x58, 0x00 }; // "TEX\0"
        private static readonly byte[] WASM_SIGNATURE = { 0x00, 0x61, 0x73, 0x6d }; // "\0asm"

        private static readonly byte[] UNITYFS_SIGNATURE = { 0x55, 0x6E, 0x69, 0x74, 0x79, 0x46, 0x53 }; // "UnityFS"
        private static readonly byte[] UNITYWEB_SIGNATURE = { 0x55, 0x6E, 0x69, 0x74, 0x79, 0x57, 0x65, 0x62 }; // "UnityWeb"
        private static readonly byte[] UNITYRAW_SIGNATURE = { 0x55, 0x6E, 0x69, 0x74, 0x79, 0x52, 0x61, 0x77 }; // "UnityRaw"
        
        public static string GuessExtension(Span<byte> data)
        {
            if (data.Length == 0)
                return string.Empty;

            if (StartsWith(data, DDS_SIGNATURE)) return "dds";
            if (StartsWith(data, TEX_SIGNATURE)) return "tex";
            if (StartsWith(data, WASM_SIGNATURE)) return "wasm";
            if (StartsWith(data, UNITYFS_SIGNATURE) || StartsWith(data, UNITYWEB_SIGNATURE) || StartsWith(data, UNITYRAW_SIGNATURE)) return "assetbundle";
            if (StartsWith(data, PNG_SIGNATURE)) return "png";
            if (StartsWith(data, JPG_SIGNATURE)) return "jpg";
            if (StartsWith(data, OGG_SIGNATURE)) return "ogg";
            if (StartsWith(data, WEBM_SIGNATURE)) return "webm";
            if (StartsWith(data, BNK_SIGNATURE)) return "bnk";
            if (StartsWith(data, SKL_SIGNATURE)) return "skl";
            if (StartsWith(data, SKN_SIGNATURE)) return "skn";
            if (StartsWith(data, R3D2MESH_SIGNATURE)) return "scb";
            if (StartsWith(data, ANM_R3D2ANMD_SIGNATURE) || StartsWith(data, ANM_R3D2CANM_SIGNATURE)) return "anm";
            if (StartsWith(data, MAPGEO_SIGNATURE)) return "mapgeo";
            if (StartsWith(data, SCO_SIGNATURE)) return "sco";
            if (Contains(data, LUAQ_SIGNATURE, 1)) return "luaobj";
            if (StartsWith(data, PRELOAD_SIGNATURE)) return "preload";
            if (StartsWith(data, RST_SIGNATURE)) return "stringtable";

            if (StartsWith(data, BIN_PROP_SIGNATURE) || StartsWith(data, BIN_PTCH_SIGNATURE)) return "bin";
            if (StartsWith(data, ICO_SIGNATURE)) return "ico";

            // Riot UIAutoAtlas: no magic bytes, structural validation required.
            if (IsRiotBinaryAtlas(data)) return "atlas";

            if (StartsWith(data, WEBP_SIGNATURE) && Contains(data, WEBP_VP8X_SIGNATURE, 8)) return "webp";
            if (StartsWith(data, WEM_SIGNATURE) && Contains(data, WEM_WAVE_SIGNATURE, 8)) return "wem";

            // Text-based formats
            string potentialText = Encoding.UTF8.GetString(data.Slice(0, Math.Min(data.Length, 100))).TrimStart();
            if (potentialText.StartsWith("{") || potentialText.StartsWith("[")) return "json";
            if (potentialText.StartsWith("<!DOCTYPE html>", StringComparison.OrdinalIgnoreCase) || potentialText.StartsWith("<html>", StringComparison.OrdinalIgnoreCase)) return "html";
            if (potentialText.StartsWith("<?xml") || potentialText.StartsWith("<svg")) return "svg";
            if (potentialText.StartsWith("<")) return "xml";
            if (potentialText.StartsWith("function") || potentialText.StartsWith("var ") || potentialText.StartsWith("let ") || potentialText.StartsWith("const ")) return "js";
            if (potentialText.Contains("body {") || potentialText.Contains("div {") || potentialText.Contains("a {")) return "css";

            return string.Empty;
        }

        private static bool IsRiotBinaryAtlas(Span<byte> data)
        {
            // Riot UIAutoAtlas layout (little-endian):
            // uint textureCount
            // repeat textureCount times:
            //   uint texturePathLength + UTF-8 texture path
            //   uint spriteCount
            //   repeat spriteCount times:
            //     uint spriteNameLength + UTF-8 sprite name
            //     5 x float32 metadata (20 bytes)
            //
            // No fixed magic. The WAD loader provides a truncated buffer (256 bytes),
            // so validate a minimum structural footprint: first texture path (must
            // reference a known image extension) + sprite count + one complete sprite.
            int offset = 0;
            if (!TryReadUInt32(data, ref offset, out uint textureCount) || textureCount == 0 || textureCount > 256)
                return false;

            if (!TryReadLengthPrefixedUtf8(data, ref offset, out string texturePath))
                return false;

            if (!(texturePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ||
                  texturePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                  texturePath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)))
                return false;

            if (!TryReadUInt32(data, ref offset, out uint spriteCount) || spriteCount == 0 || spriteCount > 1_000_000)
                return false;

            // Validate at least one complete sprite entry
            if (!TryReadLengthPrefixedUtf8(data, ref offset, out string spriteName) || string.IsNullOrWhiteSpace(spriteName))
                return false;

            const int MetadataSize = 5 * sizeof(float);
            if (data.Length - offset < MetadataSize)
                return false;

            return true;
        }

        #region Binary Readers

        private static bool TryReadUInt32(Span<byte> data, ref int offset, out uint value)
        {
            value = 0;
            if (offset < 0 || data.Length - offset < 4)
                return false;

            value = (uint)(data[offset] |
                           (data[offset + 1] << 8) |
                           (data[offset + 2] << 16) |
                           (data[offset + 3] << 24));
            offset += 4;
            return true;
        }

        private static bool TryReadLengthPrefixedUtf8(Span<byte> data, ref int offset, out string value)
        {
            value = string.Empty;
            if (!TryReadUInt32(data, ref offset, out uint byteLength) || byteLength == 0 || byteLength > 1_048_576)
                return false;

            if (data.Length - offset < (int)byteLength)
                return false;

            Span<byte> bytes = data.Slice(offset, (int)byteLength);
            offset += (int)byteLength;

            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == 0 || bytes[i] < 0x09)
                    return false;
            }

            try
            {
                value = Encoding.UTF8.GetString(bytes);
                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        private static bool StartsWith(Span<byte> data, byte[] signature)
        {
            if (data.Length < signature.Length)
                return false;
            return data.Slice(0, signature.Length).SequenceEqual(signature);
        }

        private static bool Contains(Span<byte> data, byte[] signature, int offset)
        {
            if (data.Length < offset + signature.Length)
                return false;
            return data.Slice(offset, signature.Length).SequenceEqual(signature);
        }
    }
}