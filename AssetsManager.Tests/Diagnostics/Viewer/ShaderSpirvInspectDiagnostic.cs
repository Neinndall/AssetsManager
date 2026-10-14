using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetsManager.Shaders;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// `shader-spirv-inspect <file.dxbc> [opcode...]`: prints the reflected resources of a DXBC stage and the
    /// SPIR-V instructions with the given opcodes (with the names of their operands), to design translator patches.
    /// </summary>
    internal static class ShaderSpirvInspectDiagnostic
    {
        public static void Run(string[] args)
        {
            if (args.Length == 0 || !File.Exists(args[0]))
            {
                Console.WriteLine("Usage: shader-spirv-inspect <file.dxbc> [opcode...]");
                return;
            }

            byte[] dxbc = File.ReadAllBytes(args[0]);
            ShaderReflectionData reflection = DxbcReflection.Reflect(dxbc);
            foreach (ShaderResourceData resource in reflection.Resources)
                Console.WriteLine($"[Spirv] resource {resource.Kind} bind={resource.Bind} name={resource.Name} {resource}");

            uint[] words = GameShaderTranslator.CompileSpirv(dxbc);
            var names = new Dictionary<uint, string>();
            var instructions = new List<(uint Op, uint[] Words)>();
            for (int at = 5; at < words.Length;)
            {
                int count = (int)(words[at] >> 16);
                uint op = words[at] & 0xFFFF;
                uint[] inst = words.AsSpan(at, count).ToArray();
                instructions.Add((op, inst));
                if (op == 5 && count >= 3)
                    names[inst[1]] = ReadString(inst.AsSpan(2));
                at += count;
            }

            var wanted = new HashSet<uint>(args.Skip(1).Select(uint.Parse));
            Console.WriteLine($"[Spirv] words={words.Length} bound={words[3]} instructions={instructions.Count}");
            foreach ((uint op, uint[] inst) in instructions.Where(item => wanted.Count == 0 ? item.Op is 17 or 10 or 71 : wanted.Contains(item.Op)))
                Console.WriteLine($"[Spirv] op={op} " + string.Join(" ", inst.Skip(1).Select(word => names.TryGetValue(word, out string name) ? $"{word}({name})" : word.ToString())));
        }

        private static string ReadString(ReadOnlySpan<uint> words)
        {
            byte[] bytes = new byte[words.Length * 4];
            for (int index = 0; index < words.Length; index++)
                BitConverter.GetBytes(words[index]).CopyTo(bytes, index * 4);
            int end = Array.IndexOf(bytes, (byte)0);
            return Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end);
        }
    }
}
