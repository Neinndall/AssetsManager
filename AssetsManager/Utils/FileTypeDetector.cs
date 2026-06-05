using System;
using System.Text;

namespace AssetsManager.Utils
{
    public static class FileTypeDetector
    {
        private static readonly byte[] SKN_SIGNATURE = { 0x33, 0x22, 0x11, 0x00 };
        private static readonly byte[] R3D2MESH_SIGNATURE = Encoding.ASCII.GetBytes("r3d2Mesh");
        private static readonly byte[] ANM_R3D2ANMD_SIGNATURE = Encoding.ASCII.GetBytes("r3d2anmd");
        private static readonly byte[] ANM_R3D2CANM_SIGNATURE = Encoding.ASCII.GetBytes("r3d2canm");
        private static readonly byte[] MAPGEO_SIGNATURE = Encoding.ASCII.GetBytes("RGM\x03");
        private static readonly byte[] SCO_SIGNATURE = Encoding.ASCII.GetBytes("[ObjectBegin]");
        private static readonly byte[] LUAQ_SIGNATURE = Encoding.ASCII.GetBytes("LuaQ");
        private static readonly byte[] PRELOAD_SIGNATURE = Encoding.ASCII.GetBytes("PRELOAD");
        private static readonly byte[] RST_SIGNATURE = Encoding.ASCII.GetBytes("RST\x02"); // RST\x03, RST\x04 etc. are also possible. Checked inside parser

        private static readonly byte[] BIN_PROP_SIGNATURE = Encoding.ASCII.GetBytes("PROP");
        private static readonly byte[] BIN_PTCH_SIGNATURE = Encoding.ASCII.GetBytes("PTCH");
        private static readonly byte[] ICO_SIGNATURE = { 0x00, 0x00, 0x01, 0x00 };

        private static readonly byte[] WEBP_SIGNATURE = Encoding.ASCII.GetBytes("RIFF");
        private static readonly byte[] WEBP_VP8X_SIGNATURE = Encoding.ASCII.GetBytes("WEBPVP8X");

        private static readonly byte[] WEM_SIGNATURE = Encoding.ASCII.GetBytes("RIFF");
        private static readonly byte[] WEM_WAVE_SIGNATURE = Encoding.ASCII.GetBytes("WAVEfmt ");

        public static string GuessExtension(Span<byte> data)
        {
            if (data.IsEmpty) return string.Empty;

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

            if (StartsWith(data, WEBP_SIGNATURE) && Contains(data, WEBP_VP8X_SIGNATURE, 8)) return "webp";
            if (StartsWith(data, WEM_SIGNATURE) && Contains(data, WEM_WAVE_SIGNATURE, 8)) return "wem";

            // Zero-allocation Text-based formats detection
            int textLength = Math.Min(data.Length, 128);
            ReadOnlySpan<byte> textSpan = data.Slice(0, textLength);

            // Trim start
            int start = 0;
            while (start < textSpan.Length && (textSpan[start] == ' ' || textSpan[start] == '\t' || textSpan[start] == '\r' || textSpan[start] == '\n'))
            {
                start++;
            }

            if (start >= textSpan.Length) return string.Empty;
            ReadOnlySpan<byte> trimmed = textSpan.Slice(start);

            if (trimmed[0] == '{' || trimmed[0] == '[') return "json";
            if (StartsWithIgnoreCase(trimmed, "<!DOCTYPE html") || StartsWithIgnoreCase(trimmed, "<html>")) return "html";
            if (StartsWithIgnoreCase(trimmed, "<?xml") || StartsWithIgnoreCase(trimmed, "<svg")) return "svg";
            if (trimmed[0] == '<') return "xml";
            
            if (StartsWithIgnoreCase(trimmed, "function") || 
                StartsWithIgnoreCase(trimmed, "var ") || 
                StartsWithIgnoreCase(trimmed, "let ") || 
                StartsWithIgnoreCase(trimmed, "const ")) 
            {
                return "js";
            }

            if (ContainsText(trimmed, "body {") || ContainsText(trimmed, "div {") || ContainsText(trimmed, "a {")) 
            {
                return "css";
            }

            return string.Empty;
        }

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

        private static bool StartsWithIgnoreCase(ReadOnlySpan<byte> data, string asciiText)
        {
            if (data.Length < asciiText.Length) return false;
            for (int i = 0; i < asciiText.Length; i++)
            {
                char c1 = char.ToLowerInvariant((char)data[i]);
                char c2 = char.ToLowerInvariant(asciiText[i]);
                if (c1 != c2) return false;
            }
            return true;
        }

        private static bool ContainsText(ReadOnlySpan<byte> data, string asciiText)
        {
            if (data.Length < asciiText.Length) return false;
            for (int i = 0; i <= data.Length - asciiText.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < asciiText.Length; j++)
                {
                    if ((char)data[i + j] != asciiText[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return true;
            }
            return false;
        }
    }
}