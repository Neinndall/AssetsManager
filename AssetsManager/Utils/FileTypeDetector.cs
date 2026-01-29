using System;
using System.Text;

namespace AssetsManager.Utils
{
    public static class FileTypeDetector
    {
        // File Signatures
        private static readonly byte[] DDS_SIGNATURE = { 0x44, 0x44, 0x53, 0x20 }; // "DDS "
        private static readonly byte[] PNG_SIGNATURE = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        private static readonly byte[] JPG_SIGNATURE1 = { 0xFF, 0xD8, 0xFF, 0xDB };
        private static readonly byte[] JPG_SIGNATURE2 = { 0xFF, 0xD8, 0xFF, 0xE1 };
        private static readonly byte[] GIF87A_SIGNATURE = Encoding.ASCII.GetBytes("GIF87a");
        private static readonly byte[] GIF89A_SIGNATURE = Encoding.ASCII.GetBytes("GIF89a");
        private static readonly byte[] OGG_SIGNATURE = { 0x4F, 0x67, 0x67, 0x53 }; // "OggS"
        private static readonly byte[] TTF_SIGNATURE1 = { 0x00, 0x01, 0x00, 0x00 };
        private static readonly byte[] TTF_SIGNATURE2 = Encoding.ASCII.GetBytes("true");
        private static readonly byte[] OTTO_SIGNATURE = Encoding.ASCII.GetBytes("OTTO\0");
        private static readonly byte[] WEBM_SIGNATURE = { 0x1A, 0x45, 0xDF, 0xA3 }; // EBML header
        private static readonly byte[] DDS_SIGNATURE_RAW = Encoding.ASCII.GetBytes("DDS ");
        private static readonly byte[] SVG_SIGNATURE = Encoding.ASCII.GetBytes("<svg");
        private static readonly byte[] BIN_PROP_SIGNATURE = Encoding.ASCII.GetBytes("PROP");
        private static readonly byte[] BIN_PTCH_SIGNATURE = Encoding.ASCII.GetBytes("PTCH");
        private static readonly byte[] BNK_SIGNATURE = Encoding.ASCII.GetBytes("BKHD");
        private static readonly byte[] SCB_SIGNATURE = Encoding.ASCII.GetBytes("r3d2Mesh");
        private static readonly byte[] ANM_R3D2ANMD_SIGNATURE = Encoding.ASCII.GetBytes("r3d2anmd");
        private static readonly byte[] ANM_R3D2CANM_SIGNATURE = Encoding.ASCII.GetBytes("r3d2canm");
        private static readonly byte[] SKL_SIGNATURE = Encoding.ASCII.GetBytes("r3d2sklt");
        private static readonly byte[] WPK_SIGNATURE = Encoding.ASCII.GetBytes("r3d2");
        private static readonly byte[] SKN_SIGNATURE = { 0x33, 0x22, 0x11, 0x00 };
        private static readonly byte[] PRELOAD_SIGNATURE = Encoding.ASCII.GetBytes("PreLoadBuildingBlocks = {");
        private static readonly byte[] LUA_SIGNATURE1 = { 0x1B, 0x4C, 0x75, 0x61, 0x51, 0x00, 0x01, 0x04, 0x04 };
        private static readonly byte[] LUA_SIGNATURE2 = { 0x1B, 0x4C, 0x75, 0x61, 0x51, 0x00, 0x01, 0x04, 0x08 };
        private static readonly byte[] TROYBIN_SIGNATURE = { 0x02, 0x3D, 0x00, 0x28 };
        private static readonly byte[] SCO_SIGNATURE = Encoding.ASCII.GetBytes("[ObjectBegin]");
        private static readonly byte[] MAPGEO_SIGNATURE = Encoding.ASCII.GetBytes("OEGM");
        private static readonly byte[] TEX_SIGNATURE = Encoding.ASCII.GetBytes("TEX\0");

        public static string GuessExtension(Span<byte> data)
        {
            if (data.Length == 0)
                return string.Empty;

            // Fiel a wad.py _magic_numbers_ext
            if (StartsWith(data, JPG_SIGNATURE1) || StartsWith(data, JPG_SIGNATURE2)) return "jpg";
            if (data.Length > 10 && (Encoding.ASCII.GetString(data.Slice(6, 4)) == "JFIF" || Encoding.ASCII.GetString(data.Slice(6, 4)) == "Exif")) return "jpg";
            if (StartsWith(data, PNG_SIGNATURE)) return "png";
            if (StartsWith(data, GIF87A_SIGNATURE) || StartsWith(data, GIF89A_SIGNATURE)) return "gif";
            if (StartsWith(data, OGG_SIGNATURE)) return "ogg";
            if (StartsWith(data, TTF_SIGNATURE1) || StartsWith(data, TTF_SIGNATURE2)) return "ttf";
            if (StartsWith(data, WEBM_SIGNATURE)) return "webm";
            if (StartsWith(data, OTTO_SIGNATURE)) return "otf";
            if (StartsWith(data, Encoding.ASCII.GetBytes("\"use strict\";"))) return "min.js";
            if (StartsWith(data, Encoding.ASCII.GetBytes("<template "))) return "template.html";
            if (StartsWith(data, Encoding.ASCII.GetBytes("<!-- Elements -->"))) return "template.html";
            if (StartsWith(data, DDS_SIGNATURE_RAW)) return "dds";
            if (StartsWith(data, SVG_SIGNATURE)) return "svg";
            if (StartsWith(data, BIN_PROP_SIGNATURE) || StartsWith(data, BIN_PTCH_SIGNATURE)) return "bin";
            if (StartsWith(data, BNK_SIGNATURE)) return "bnk";
            if (StartsWith(data, SCB_SIGNATURE)) return "scb";
            if (StartsWith(data, ANM_R3D2ANMD_SIGNATURE) || StartsWith(data, ANM_R3D2CANM_SIGNATURE)) return "anm";
            if (StartsWith(data, SKL_SIGNATURE)) return "skl";
            if (StartsWith(data, WPK_SIGNATURE)) return "wpk";
            if (StartsWith(data, SKN_SIGNATURE)) return "skn";
            if (StartsWith(data, PRELOAD_SIGNATURE)) return "preload";
            if (StartsWith(data, LUA_SIGNATURE1)) return "luabin";
            if (StartsWith(data, LUA_SIGNATURE2)) return "luabin64";
            if (StartsWith(data, TROYBIN_SIGNATURE)) return "troybin";
            if (StartsWith(data, SCO_SIGNATURE)) return "sco";
            if (StartsWith(data, MAPGEO_SIGNATURE)) return "mapgeo";
            if (StartsWith(data, TEX_SIGNATURE)) return "tex";

            // Fallback para JSON
            try
            {
                string jsonCand = Encoding.UTF8.GetString(data.Slice(0, Math.Min(data.Length, 1000))).Trim();
                if ((jsonCand.StartsWith("{") && jsonCand.EndsWith("}")) || (jsonCand.StartsWith("[") && jsonCand.EndsWith("]")))
                    return "json";
            }
            catch { }

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
    }
}