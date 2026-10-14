using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;

namespace AssetsManager.Services.Viewer.Vfx.Parsing
{
    // Shimmer currently previews meshes at rest; animated graphs must retain the consumer fallback.
    internal static class VfxShimmerDriverEvaluator
    {
        private sealed record Port(uint Field, int Width, bool List = false);
        private sealed record Driver(int Width, string Operation, Port[] Inputs, uint Slot = 0, float[] Defaults = null);
        private static readonly IReadOnlyDictionary<uint, Driver> Registry = CreateRegistry();

        internal static bool IsDriver(BinTreeProperty value, int width) =>
            value is BinTreeStruct node && Registry.TryGetValue(node.ClassHash, out Driver driver) && driver.Width == width;

        internal static bool IsGraphRoot(BinTreeProperty value) =>
            value is BinTreeStruct node && Registry.TryGetValue(node.ClassHash, out Driver driver) && driver.Operation == "property";

        internal static Vector3? Vector3Value(BinTreeProperty value)
        {
            float[] result = Evaluate(value, 3);
            return result is null ? null : new Vector3(result[0], result[1], result[2]);
        }

        internal static Vector4? Vector4Value(BinTreeProperty value)
        {
            float[] result = Evaluate(value, 4);
            return result is null ? null : new Vector4(result[0], result[1], result[2], result[3]);
        }

        internal static float[] Evaluate(BinTreeProperty value, int width, int depth = 0)
        {
            if (width < 1 || width > 4) throw new ArgumentOutOfRangeException(nameof(width));
            if (depth > 24) return null;
            if (value is BinTreeOptional optional) value = optional.Value;
            if (value is not BinTreeStruct node)
                return depth == 0 ? Fit(Components(value), width) : new float[width];
            if (!Registry.TryGetValue(node.ClassHash, out Driver driver) || driver.Width != width)
                return new float[width];

            BinTreeProperty Field(uint hash) => node.Properties.GetValueOrDefault(hash);
            if (driver.Operation == "constant")
                return Fit(Components(Field(driver.Slot)) ?? driver.Defaults, width);
            if (driver.Operation == "curve")
            {
                var held = Field(driver.Slot) as BinTreeStruct;
                var dynamics = held?.Properties.GetValueOrDefault(Hash("dynamics")) as BinTreeStruct;
                if (dynamics?.Properties.GetValueOrDefault(Hash("times")) is BinTreeContainer times &&
                    dynamics.Properties.GetValueOrDefault(Hash("values")) is BinTreeContainer values)
                {
                    for (int i = 0; i < Math.Min(times.Elements.Count, values.Elements.Count); i++)
                        if (Components(times.Elements[i]) is { Length: 1 } && Components(values.Elements[i]) is not null)
                            return null;
                }
                return Fit(Components(held?.Properties.GetValueOrDefault(Hash("constantValue"))) ?? driver.Defaults, width);
            }
            if (driver.Operation == "dynamic") return null;
            if (driver.Operation == "easing")
                return Components(Field(Hash("duration"))) is { Length: > 0 } duration && duration[0] <= 0 ? new float[width] : null;

            var args = new List<float[]>();
            foreach (Port port in driver.Inputs)
            {
                IEnumerable<BinTreeProperty> inputs = port.List
                    ? (Field(port.Field) as BinTreeContainer)?.Elements ?? Array.Empty<BinTreeProperty>()
                    : new[] { Field(port.Field) };
                foreach (BinTreeProperty input in inputs)
                {
                    float[] folded = Evaluate(input, port.Width, depth + 1);
                    if (folded is null) return null;
                    args.Add(folded);
                }
            }
            if (driver.Operation == "property") return args[0];
            float[] result = new float[width];
            float[] first = args.Count > 0 ? args[0] : result;
            float[] Stored(string name, int size, float fallback = 0) =>
                Fit(Components(Field(Hash(name))) ?? Enumerable.Repeat(fallback, size).ToArray(), size);
            switch (driver.Operation)
            {
                case "add": case "multiply": case "min": case "max":
                    if (args.Count == 0) return result;
                    Array.Copy(first, result, width);
                    foreach (float[] arg in args.Skip(1))
                        for (int c = 0; c < width; c++)
                            result[c] = driver.Operation switch {
                                "add" => result[c] + arg[c], "multiply" => result[c] * arg[c],
                                "min" => MathF.Min(result[c], arg[c]), _ => MathF.Max(result[c], arg[c]) };
                    break;
                case "abs":
                    for (int c = 0; c < width; c++) result[c] = MathF.Abs(first[c]);
                    break;
                case "normalize": case "length":
                    double magnitude = Math.Sqrt(first.Sum(v => (double)v * v));
                    if (driver.Operation == "length") result[0] = (float)magnitude;
                    else if (magnitude != 0)
                        for (int c = 0; c < width; c++) result[c] = (float)(first[c] / magnitude);
                    break;
                case "clamp":
                    float[] low = Stored("Low", width), high = Stored("High", width, 1);
                    if (Enumerable.Range(0, width).Any(c => low[c] > high[c])) return result;
                    for (int c = 0; c < width; c++) result[c] = Math.Clamp(first[c], low[c], high[c]);
                    break;
                case "lerp":
                    for (int c = 0; c < width; c++) result[c] = (float)(first[c] + ((double)args[1][c] - first[c]) * args[2][0]);
                    break;
                case "scale": case "divide":
                    for (int c = 0; c < width; c++)
                    {
                        float operand = args[1][args[1].Length == 1 ? 0 : c];
                        result[c] = driver.Operation == "scale" ? first[c] * operand : operand == 0 ? 0 : first[c] / operand;
                    }
                    break;
                case "broadcast":
                    Array.Fill(result, first[0]);
                    break;
                case "compose": case "extend":
                    var parts = args.SelectMany(a => a);
                    if (driver.Operation == "extend")
                        parts = parts.Concat(Fit(Components(Field(0xb1ea6248)), width - first.Length));
                    return Fit(parts.ToArray(), width);
                case "sine":
                    float[] remap = Components(Field(Hash("Remap"))) is { } r ? Fit(r, 2) : new float[] { 0, 1 };
                    result[0] = args[1][0] == 0 ? 0 : (float)(remap[0] + (Math.Sin(2 * Math.PI * first[0] / args[1][0]) + 1) / 2 * (remap[1] - remap[0]));
                    break;
            }
            return result;
        }

        private static float[] Components(BinTreeProperty value)
        {
            if (value is BinTreeOptional optional) value = optional.Value;
            return value switch {
                BinTreeF32 f => new[] { f.Value },
                _ when VfxValueParser.AsF32(value) is float number => new[] { number },
                BinTreeVector2 v => new[] { v.Value.X, v.Value.Y },
                BinTreeVector3 v => new[] { v.Value.X, v.Value.Y, v.Value.Z },
                BinTreeVector4 v => new[] { v.Value.X, v.Value.Y, v.Value.Z, v.Value.W },
                BinTreeColor v => new[] { v.Value.R, v.Value.G, v.Value.B, v.Value.A },
                _ => null };
        }

        private static float[] Fit(float[] value, int width)
        {
            var result = new float[width];
            if (value is not null) Array.Copy(value, result, Math.Min(width, value.Length));
            return result;
        }

        private static uint Hash(string name) => name.StartsWith("0x", StringComparison.Ordinal)
            ? Convert.ToUInt32(name[2..], 16) : VfxParsingHash.Fnv1a(name);

        private static IReadOnlyDictionary<uint, Driver> CreateRegistry()
        {
            var entries = new Dictionary<uint, Driver>();
            Port P(string field, int width, bool list = false) => new(Hash(field), width, list);
            void Add(string name, int width, string operation, params Port[] inputs) =>
                entries.Add(Hash(name), new Driver(width, operation, inputs));
            void Leaf(string name, int width, string operation, string slot, params float[] defaults) =>
                entries.Add(Hash(name), new Driver(width, operation, Array.Empty<Port>(), Hash(slot), defaults));
            for (int width = 1; width <= 4; width++)
            {
                string type = width == 1 ? "Float" : $"Vector{width}";
                Add($"Vfx{type}DynamicProperty", width, "property", P(type, width));
                foreach (string operation in new[] { "Add", "Multiply", "Min", "Max" })
                    Add($"Vfx{operation}{type}Driver", width, operation.ToLowerInvariant(), P("params", width, true));
                Add($"VfxAbs{type}Driver", width, "abs", P("Param", width));
                Add($"VfxClamp{type}Driver", width, "clamp", P("Param", width));
                Add($"Vfx{type}LerpDriver", width, "lerp", P("From", width), P("To", width), P("Factor", 1));
            }
            Leaf("VfxFloatConstantDriver", 1, "constant", "Float", 0);
            Leaf("VfxVector2ConstantDriver", 2, "constant", "Vector2", 0, 0);
            Leaf("VfxVector3ConstantDriver", 3, "constant", "Vector3", 0, 0, 0);
            Leaf("VfxColorConstantDriver", 4, "constant", "Color", 0, 0, 0, 1);
            Leaf("VfxColorRgbConstantDriver", 3, "constant", "Color", 0, 0, 0);
            Leaf("0x1d04cfa7", 1, "curve", "Float", 0);
            Leaf("0x3eb74cbe", 2, "curve", "Vector2", 1, 1);
            Leaf("0x2d42ea41", 3, "curve", "Vector3", 1, 1, 1);
            Leaf("0x44852d75", 3, "curve", "colors", 0, 0, 0);
            Leaf("0x7cc5a312", 4, "curve", "colors", 1, 1, 1, 1);
            for (int width = 2; width <= 3; width++)
            {
                Add($"VfxNormalizeVector{width}Driver", width, "normalize", P($"Vector{width}Input", width));
                Add($"VfxLengthVector{width}Driver", 1, "length", P($"Vector{width}Input", width));
                Add($"VfxScaleVector{width}Driver", width, "scale", P($"Vector{width}", width), P("ScaleFactor", 1));
            }
            string[] scalarDivide = { "0xd6738324", "0x168d2f0d", "0x95182f0a", "0xff2348d3" };
            string[] vectorDivide = { "0x997d54ab", "0x64707da8", "0xa995ecc5" };
            for (int width = 1; width <= 4; width++)
            {
                string slot = width == 1 ? "value" : $"Vector{width}";
                Add(scalarDivide[width - 1], width, "divide", P(slot, width), P("Divisor", 1));
                if (width > 1) Add(vectorDivide[width - 2], width, "divide", P(slot, width), P("Divisor", width));
            }
            Add("0x399295b9", 2, "compose", P("X", 1), P("Y", 1));
            Add("0x65e1b9a2", 3, "compose", P("X", 1), P("Y", 1), P("Z", 1));
            Add("0x3624c20b", 4, "compose", P("X", 1), P("Y", 1), P("Z", 1), P("W", 1));
            Add("0x791d4f88", 4, "compose", P("xy", 2), P("Zw", 2));
            Add("VfxColorRgbaDriver", 4, "compose", P("Rgb", 3), P("Alpha", 1));
            Add("0x9a2d73f2", 2, "broadcast", P("Float", 1));
            Add("0xdef9bfd5", 3, "broadcast", P("Float", 1));
            Add("0x7c387678", 4, "broadcast", P("Float", 1));
            Add("0xe3a77546", 3, "extend", P("Input", 2));
            Add("0x9c5c4342", 4, "extend", P("Input", 3));
            Add("0x14daebe5", 4, "extend", P("Input", 2));
            Add("VfxFloatSineDriver", 1, "sine", P("Time", 1), P("period", 1));
            Add("VfxFloatEasingDriver", 1, "easing");
            Add("0x414d1503", 1, "dynamic");
            Add("0xc5e53afa", 1, "dynamic");
            return entries;
        }
    }
}
