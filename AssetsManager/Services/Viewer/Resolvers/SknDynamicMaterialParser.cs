using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Resolvers
{
    internal static class SknDynamicMaterialParser
    {
        private static uint Hash(string name) => Fnv1a.HashLower(name);

        internal static IReadOnlyList<GameMaterialTextureSwap> Read(
            IReadOnlyDictionary<uint, BinTreeProperty> properties, Func<ulong, string> resolvePath)
        {
            var result = new List<GameMaterialTextureSwap>();
            if (!properties.TryGetValue(Hash("dynamicMaterial"), out var dynamicProperty) ||
                dynamicProperty is not BinTreeStruct dynamicMaterial ||
                !dynamicMaterial.Properties.TryGetValue(Hash("textures"), out var textureProperty) ||
                textureProperty is not BinTreeContainer textures)
                return result;

            foreach (var entry in textures.Elements)
            {
                if (entry is not BinTreeEmbedded swap ||
                    !swap.Properties.TryGetValue(Hash("name"), out var nameProperty) ||
                    nameProperty is not BinTreeString name || string.IsNullOrWhiteSpace(name.Value) ||
                    (swap.Properties.TryGetValue(Hash("Enabled"), out var enabled) &&
                     enabled is BinTreeBool { Value: false }) ||
                    !swap.Properties.TryGetValue(Hash("options"), out var optionsProperty) ||
                    optionsProperty is not BinTreeContainer options)
                    continue;

                var parsed = new List<GameMaterialTextureSwapOption>();
                foreach (var item in options.Elements)
                {
                    if (item is not BinTreeEmbedded option ||
                        !option.Properties.TryGetValue(Hash("TextureName"), out var texture))
                        continue;
                    string path = texture switch
                    {
                        BinTreeString text => text.Value,
                        BinTreeWadChunkLink link when link.Value != 0 =>
                            resolvePath?.Invoke(link.Value) is string resolved && !string.IsNullOrWhiteSpace(resolved)
                                ? resolved : $"{link.Value:x16}",
                        _ => null
                    };
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    option.Properties.TryGetValue(Hash("driver"), out var driver);
                    parsed.Add(new GameMaterialTextureSwapOption(PathUtils.ToVirtualPath(path), ReadCondition(driver, 0)));
                }
                if (parsed.Count > 0) result.Add(new GameMaterialTextureSwap(name.Value, parsed));
            }
            return result;
        }

        /// <summary>The stack count a buff turned on in the preview reports; authored ranges top out well below it.</summary>
        internal const float FullStacks = 100f;

        internal static IReadOnlyList<GameMaterialDynamicParameter> ReadParameters(
            IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            var result = new List<GameMaterialDynamicParameter>();
            if (!properties.TryGetValue(Hash("dynamicMaterial"), out var property) ||
                property is not BinTreeStruct dynamicMaterial ||
                !dynamicMaterial.Properties.TryGetValue(Hash("parameters"), out var parameterProperty) ||
                parameterProperty is not BinTreeContainer parameters)
                return result;
            foreach (var entry in parameters.Elements)
            {
                if (entry is not BinTreeStruct parameter ||
                    !parameter.Properties.TryGetValue(Hash("name"), out var nameProperty) ||
                    nameProperty is not BinTreeString name || string.IsNullOrWhiteSpace(name.Value) ||
                    parameter.Properties.TryGetValue(Hash("Enabled"), out var enabled) && enabled is BinTreeBool { Value: false } ||
                    !parameter.Properties.TryGetValue(Hash("driver"), out var driver))
                    continue;
                var conditions = new List<GameMaterialBoolCondition>();
                var evaluator = ReadValueDriver(driver, 0, conditions);
                if (evaluator != null)
                    result.Add(new(name.Value, evaluator,
                        conditions.SelectMany(condition => condition.Buffs()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
            }
            return result;
        }

        // Every condition the driver reads lands in `conditions`, so the caller can list the buffs it depends on.
        private static Func<GameMaterialState, Vector4?> ReadValueDriver(
            BinTreeProperty property,
            int depth,
            ICollection<GameMaterialBoolCondition> conditions)
        {
            if (depth >= 32 || property is not BinTreeStruct driver) return null;
            var fields = driver.Properties;
            if (driver.ClassHash == Hash("Float4LiteralMaterialDriver"))
            {
                Vector4 value = Vector(fields, "value", new(1, 0, 0, 0));
                return _ => value;
            }
            if (driver.ClassHash == Hash("FloatLiteralMaterialDriver"))
            {
                // mValue defaults to 0: Aatrox Skin33's body rests at Bloom_Intensity 0, not its static 10.
                Vector4 value = new(Scalar(fields, "mValue", 0f));
                return _ => value;
            }
            if (driver.ClassHash == Hash("LerpMaterialDriver") || driver.ClassHash == Hash("LerpVec4LogicDriver"))
            {
                bool vector = driver.ClassHash == Hash("LerpVec4LogicDriver");
                fields.TryGetValue(Hash(vector ? "BoolDriver" : "mBoolDriver"), out var conditionProperty);
                var condition = ReadCondition(conditionProperty, depth + 1);
                conditions.Add(condition);
                // A float driver fills every component, so it can drive a colour (TintColor 0 hides a form).
                Vector4 on = vector ? Vector(fields, "OnValue", Vector4.One) :
                    new(Scalar(fields, "mOnValue", 1));
                Vector4 off = vector ? Vector(fields, "OffValue", new(0, 0, 0, 1)) :
                    new(Scalar(fields, "mOffValue", 0));
                var fade = new LerpFade(
                    Scalar(fields, vector ? "TurnOnTimeSec" : "mTurnOnTimeSec", 1f),
                    Scalar(fields, vector ? "TurnOffTimeSec" : "mTurnOffTimeSec", 1f));
                return state => condition.Evaluate(state) is bool active
                    ? Vector4.Lerp(off, on, fade.Advance(active, state.Time))
                    : null;
            }
            // BlendingSwitch is a Switch that fades between its values; the preview switches instantly.
            if (driver.ClassHash == Hash("SwitchMaterialDriver") || driver.ClassHash == Hash("BlendingSwitchMaterialDriver"))
            {
                var options = new List<(GameMaterialBoolCondition Condition, Func<GameMaterialState, Vector4?> Value)>();
                if (fields.TryGetValue(Hash("mElements"), out var elementsProperty) && elementsProperty is BinTreeContainer elements)
                    foreach (var entry in elements.Elements)
                        if (entry is BinTreeStruct element)
                        {
                            element.Properties.TryGetValue(Hash("mCondition"), out var condition);
                            element.Properties.TryGetValue(Hash("mValue"), out var value);
                            GameMaterialBoolCondition read = ReadCondition(condition, depth + 1);
                            conditions.Add(read);
                            options.Add((read, ReadValueDriver(value, depth + 1, conditions)));
                        }
                fields.TryGetValue(Hash("mDefaultValue"), out var defaultProperty);
                var fallback = ReadValueDriver(defaultProperty, depth + 1, conditions);
                return state =>
                {
                    foreach (var option in options)
                    {
                        bool? active = option.Condition.Evaluate(state);
                        if (!active.HasValue) return null;
                        if (active.Value) return option.Value?.Invoke(state);
                    }
                    return fallback?.Invoke(state);
                };
            }
            if (driver.ClassHash == Hash("MaxMaterialDriver") || driver.ClassHash == Hash("MinMaterialDriver"))
            {
                bool max = driver.ClassHash == Hash("MaxMaterialDriver");
                var children = new List<Func<GameMaterialState, Vector4?>>();
                if (fields.TryGetValue(Hash("mDrivers"), out var driversProperty) && driversProperty is BinTreeContainer drivers)
                    foreach (var child in drivers.Elements)
                        children.Add(ReadValueDriver(child, depth + 1, conditions));
                if (children.Count == 0 || children.Contains(null))
                    return null;
                return state =>
                {
                    Vector4? result = null;
                    foreach (var child in children)
                    {
                        if (child(state) is not Vector4 value) return null;
                        result = result is Vector4 current ? max ? Vector4.Max(current, value) : Vector4.Min(current, value) : value;
                    }
                    return result;
                };
            }
            if (driver.ClassHash == Hash("FloatGraphMaterialDriver"))
            {
                // The inner driver picks the point on the authored curve (a 0..1 progress for lerp drivers).
                fields.TryGetValue(Hash("driver"), out var innerProperty);
                var inner = ReadValueDriver(innerProperty, depth + 1, conditions);
                if (inner == null ||
                    !fields.TryGetValue(Hash("graph"), out var graphProperty) || graphProperty is not BinTreeStruct graph ||
                    !TryReadFloats(graph.Properties, "times", out float[] times) ||
                    !TryReadFloats(graph.Properties, "values", out float[] values) ||
                    times.Length == 0 || times.Length != values.Length)
                    return null;
                return state => inner(state) is Vector4 at ? new Vector4(SampleCurve(times, values, at.X)) : null;
            }
            if (driver.ClassHash == Hash("ColorGraphMaterialDriver"))
            {
                fields.TryGetValue(Hash("driver"), out var innerProperty);
                var inner = ReadValueDriver(innerProperty, depth + 1, conditions);
                if (inner == null ||
                    !fields.TryGetValue(Hash("colors"), out var graphProperty) || graphProperty is not BinTreeStruct graph ||
                    !TryReadFloats(graph.Properties, "times", out float[] times) ||
                    !TryReadVectors(graph.Properties, "values", out Vector4[] colors) ||
                    times.Length == 0 || times.Length != colors.Length)
                    return null;
                return state => inner(state) is Vector4 at ? SampleColor(times, colors, at.X) : null;
            }
            if (driver.ClassHash == Hash("SpecificColorMaterialDriver"))
            {
                Vector4 color = Vector(fields, "mColor", new(1, 0, 0, 1));
                return _ => color;
            }
            if (driver.ClassHash == Hash("ColorChooserMaterialDriver"))
            {
                fields.TryGetValue(Hash("mBoolDriver"), out var conditionProperty);
                var condition = ReadCondition(conditionProperty, depth + 1);
                conditions.Add(condition);
                Vector4 on = Vector(fields, "mColorOn", new(1, 0, 0, 1));
                Vector4 off = Vector(fields, "mColorOff", new(0, 0, 1, 1));
                return state => condition.Evaluate(state) is bool active ? active ? on : off : null;
            }
            if (driver.ClassHash == Hash("RemapFloatMaterialDriver"))
            {
                fields.TryGetValue(Hash("mDriver"), out var innerProperty);
                var inner = ReadValueDriver(innerProperty, depth + 1, conditions);
                if (inner == null)
                    return null;
                float min = Scalar(fields, "mMinValue", 0f), max = Scalar(fields, "mMaxValue", 1f);
                float outMin = Scalar(fields, "mOutputMinValue", 0f), outMax = Scalar(fields, "mOutputMaxValue", 1f);
                return state => inner(state) is Vector4 at ? new Vector4(Remap(at.X, min, max, outMin, outMax)) : null;
            }
            if (driver.ClassHash == Hash("RemapVec4MaterialDriver"))
            {
                fields.TryGetValue(Hash("driver"), out var innerProperty);
                var inner = ReadValueDriver(innerProperty, depth + 1, conditions);
                if (inner == null)
                    return null;
                Vector4 min = Vector(fields, "MinValue", Vector4.Zero), max = Vector(fields, "MaxValue", Vector4.One);
                Vector4 outMin = Vector(fields, "OutputMinValue", Vector4.Zero), outMax = Vector(fields, "OutputMaxValue", Vector4.One);
                return state => inner(state) is Vector4 at
                    ? new Vector4(
                        Remap(at.X, min.X, max.X, outMin.X, outMax.X),
                        Remap(at.Y, min.Y, max.Y, outMin.Y, outMax.Y),
                        Remap(at.Z, min.Z, max.Z, outMin.Z, outMax.Z),
                        Remap(at.W, min.W, max.W, outMin.W, outMax.W))
                    : null;
            }
            if (driver.ClassHash == Hash("BuffCounterDynamicMaterialFloatDriver"))
            {
                // A buff the preview turns on counts as fully stacked (Irelia's passive: stepValue remaps 0..3 stacks to
                // -3..0); drivers remap or compare the count, so an ample one reaches their maximum.
                var buff = new GameMaterialBoolCondition(GameMaterialBoolKind.Buff, Name: ReadBuffKey(driver));
                conditions.Add(buff);
                return state => new Vector4(buff.Evaluate(state) == true ? FullStacks : 0f);
            }
            // The resting preview: no death or ability animation has progressed, health is full and the character stands still.
            if (driver.ClassHash == Hash("AnimationFractionDynamicMaterialFloatDriver") ||
                driver.ClassHash == Hash("VelocityDynamicMaterialFloatDriver"))
                return _ => Vector4.Zero;
            if (driver.ClassHash == Hash("HealthDynamicMaterialFloatDriver"))
                return _ => Vector4.One;
            if (driver.ClassHash == Hash("TimeMaterialDriver"))
            {
                // Seconds of preview time, wrapped to LoopDuration and, by default, reported as a fraction of it.
                float? loop = fields.TryGetValue(Hash("LoopDuration"), out var loopProperty) &&
                              loopProperty is BinTreeOptional { Value: BinTreeF32 loopValue } && loopValue.Value > 0f
                    ? loopValue.Value
                    : null;
                bool fraction = !fields.TryGetValue(Hash("LoopTimeAsFraction"), out var fractionProperty) ||
                                fractionProperty is not BinTreeBool { Value: false };
                return state =>
                {
                    if (loop is not float duration)
                        return new Vector4(state.Time);
                    float wrapped = state.Time % duration;
                    return new Vector4(fraction ? wrapped / duration : wrapped);
                };
            }
            if (driver.ClassHash == Hash("SineMaterialDriver"))
            {
                // Bias + Scale * sin(2 pi Frequency input), the frequency in cycles per unit of its input (hertz for a
                // time driver): time drivers loop every 360 s, a whole number of cycles for every authored frequency
                // (2, 3, 5, 0.75, 1.5, 3.4, 5.3...), so the loop is seamless only in hertz.
                fields.TryGetValue(Hash("mDriver"), out var innerProperty);
                var inner = ReadValueDriver(innerProperty, depth + 1, conditions);
                if (inner == null)
                    return null;
                float frequency = Scalar(fields, "mFrequency", 1f), scale = Scalar(fields, "mScale", 1f), bias = Scalar(fields, "mBias", 0f);
                return state => inner(state) is Vector4 at ? new Vector4(bias + scale * MathF.Sin(MathF.Tau * frequency * at.X)) : null;
            }
            // A bool driver read as a float is 1 or 0, as the game compares buffs against 1.
            GameMaterialBoolCondition asCondition = ReadCondition(property, depth);
            if (asCondition.Kind != GameMaterialBoolKind.Unsupported)
            {
                conditions.Add(asCondition);
                return state => asCondition.Evaluate(state) is bool active ? new Vector4(active ? 1f : 0f) : null;
            }
            return null;
        }

        /// <summary>
        /// The 0..1 progress of a lerp driver toward its on value, easing exponentially with the authored turn-on and
        /// turn-off times: a fast on/off condition settles at a steady level instead of flickering, as Aatrox Skin33's
        /// combat glow (0 > sin at 5 Hz, on in 1 s, off in 0.5 s) holds a steady third of its peak in game.
        /// Its first value, a step back or a gap over half a second settle at once, so a still preview and repeated
        /// evaluations stay exact.
        /// </summary>
        private sealed class LerpFade
        {
            private const float MaximumStep = 0.5f;
            private readonly float _turnOn;
            private readonly float _turnOff;
            private float _progress = -1f;
            private float _time;

            internal LerpFade(float turnOn, float turnOff)
            {
                _turnOn = turnOn;
                _turnOff = turnOff;
            }

            internal float Advance(bool on, float time)
            {
                float target = on ? 1f : 0f;
                float step = time - _time;
                if (_progress < 0f || step < 0f || step > MaximumStep)
                    _progress = target;
                else if (step > 0f)
                {
                    float duration = on ? _turnOn : _turnOff;
                    float weight = duration > 0f ? MathF.Min(1f, step / duration) : 1f;
                    _progress += (target - _progress) * weight;
                }
                _time = time;
                return _progress;
            }
        }

        private static bool TryReadVectors(IReadOnlyDictionary<uint, BinTreeProperty> fields, string name, out Vector4[] result)
        {
            result = null;
            if (!fields.TryGetValue(Hash(name), out var property) || property is not BinTreeContainer container)
                return false;
            var values = new List<Vector4>();
            foreach (var element in container.Elements)
            {
                if (element is not BinTreeVector4 vector) return false;
                values.Add(vector.Value);
            }
            result = values.ToArray();
            return true;
        }

        private static Vector4 SampleColor(float[] times, Vector4[] colors, float at) => new(
            SampleCurve(times, colors.Select(color => color.X).ToArray(), at),
            SampleCurve(times, colors.Select(color => color.Y).ToArray(), at),
            SampleCurve(times, colors.Select(color => color.Z).ToArray(), at),
            SampleCurve(times, colors.Select(color => color.W).ToArray(), at));

        /// <summary>Maps <paramref name="value"/> from [min, max] onto [outMin, outMax], clamped to that range.</summary>
        internal static float Remap(float value, float min, float max, float outMin, float outMax)
        {
            float t = max != min ? Math.Clamp((value - min) / (max - min), 0f, 1f) : 0f;
            return outMin + (outMax - outMin) * t;
        }

        private static bool TryReadFloats(IReadOnlyDictionary<uint, BinTreeProperty> fields, string name, out float[] result)
        {
            result = null;
            if (!fields.TryGetValue(Hash(name), out var property) || property is not BinTreeContainer container)
                return false;
            var values = new List<float>();
            foreach (var element in container.Elements)
            {
                if (element is not BinTreeF32 scalar) return false;
                values.Add(scalar.Value);
            }
            result = values.ToArray();
            return true;
        }

        /// <summary>Piecewise-linear curve through (times, values), held flat before the first and after the last key.</summary>
        internal static float SampleCurve(IReadOnlyList<float> times, IReadOnlyList<float> values, float at)
        {
            if (at <= times[0]) return values[0];
            for (int key = 1; key < times.Count; key++)
            {
                if (at > times[key]) continue;
                float span = times[key] - times[key - 1];
                float t = span > 0f ? (at - times[key - 1]) / span : 1f;
                return values[key - 1] + (values[key] - values[key - 1]) * t;
            }
            return values[^1];
        }

        private static Vector4 Vector(IReadOnlyDictionary<uint, BinTreeProperty> fields, string name, Vector4 fallback) =>
            fields.TryGetValue(Hash(name), out var value) && value is BinTreeVector4 vector ? vector.Value : fallback;

        private static float Scalar(IReadOnlyDictionary<uint, BinTreeProperty> fields, string name, float fallback) =>
            fields.TryGetValue(Hash(name), out var value) && value is BinTreeF32 scalar ? scalar.Value : fallback;

        /// <summary>A logic bool driver outside a material, such as a persistent effect condition's owner condition.</summary>
        internal static GameMaterialBoolCondition ReadBoolDriver(BinTreeProperty property) => ReadCondition(property, 0);

        private static GameMaterialBoolCondition ReadCondition(BinTreeProperty property, int depth)
        {
            if (depth >= 32 || property is not BinTreeStruct driver)
                return new(GameMaterialBoolKind.Unsupported);
            if (driver.ClassHash == Hash("HasGearDynamicMaterialBoolDriver"))
            {
                if (driver.Properties.TryGetValue(Hash("mGearIndex"), out var index) && index is not BinTreeU8)
                    return new(GameMaterialBoolKind.Unsupported);
                int gear = index is BinTreeU8 value ? value.Value : 0;
                return new(GameMaterialBoolKind.Gear, gear);
            }
            if (driver.ClassHash == Hash("HasBuffDynamicMaterialBoolDriver"))
                return new(GameMaterialBoolKind.Buff, Name: ReadBuffKey(driver));
            if (driver.ClassHash == Hash("IsDeadDynamicMaterialBoolDriver"))
                return new(GameMaterialBoolKind.Dead);
            if (driver.ClassHash == Hash("IsAnimationPlayingDynamicMaterialBoolDriver"))
            {
                var animations = new List<uint>();
                if (driver.Properties.TryGetValue(Hash("mAnimationNames"), out var names) && names is BinTreeContainer clips)
                    foreach (var clip in clips.Elements)
                        if (clip is BinTreeHash hash)
                            animations.Add(hash.Value);
                return new(GameMaterialBoolKind.Animation, Animations: animations);
            }
            if (driver.ClassHash == Hash("IsCastingBoolDriver") ||
                driver.ClassHash == Hash("IsAttackingBoolDriver") ||
                driver.ClassHash == Hash("IsMovingBoolDriver") ||
                driver.ClassHash == Hash("IsInGrassDynamicMaterialBoolDriver") ||
                driver.ClassHash == Hash("IsEnemyDynamicMaterialBoolDriver") ||
                driver.ClassHash == Hash("HasBuffWithAttributeBoolDriver") ||
                driver.ClassHash == Hash("HasBuffOfTypeBoolDriver") ||
                driver.ClassHash == Hash("FixedDurationTriggeredBoolDriver"))
                return new(GameMaterialBoolKind.Inactive);
            if (driver.ClassHash == Hash("FloatComparisonMaterialDriver"))
            {
                var operands = new List<GameMaterialBoolCondition>();
                driver.Properties.TryGetValue(Hash("mValueA"), out var left);
                driver.Properties.TryGetValue(Hash("mValueB"), out var right);
                var leftValue = ReadValueDriver(left, depth + 1, operands);
                var rightValue = ReadValueDriver(right, depth + 1, operands);
                uint op = driver.Properties.TryGetValue(Hash("mOperator"), out var opProperty) && opProperty is BinTreeU32 opValue ? opValue.Value : 0;
                // The operands' own conditions ride along as children so their buffs are offered as game states.
                return leftValue == null || rightValue == null
                    ? new(GameMaterialBoolKind.Unsupported)
                    : new(GameMaterialBoolKind.Compare, Children: operands, Left: leftValue, Right: rightValue, Operator: op);
            }
            if (driver.ClassHash == Hash("OneTrueMaterialDriver") &&
                driver.Properties.TryGetValue(Hash("mDrivers"), out var anyChildren) && anyChildren is BinTreeContainer anyList)
            {
                var conditions = new List<GameMaterialBoolCondition>();
                foreach (var child in anyList.Elements) conditions.Add(ReadCondition(child, depth + 1));
                return new(GameMaterialBoolKind.Any, Children: conditions);
            }
            if (driver.ClassHash == Hash("AllTrueMaterialDriver") &&
                driver.Properties.TryGetValue(Hash("mDrivers"), out var children) && children is BinTreeContainer list)
            {
                var conditions = new List<GameMaterialBoolCondition>();
                foreach (var child in list.Elements) conditions.Add(ReadCondition(child, depth + 1));
                return new(GameMaterialBoolKind.All, Children: conditions);
            }
            if (driver.ClassHash == Hash("DelayedBoolMaterialDriver") &&
                driver.Properties.TryGetValue(Hash("mBoolDriver"), out var delayed))
                return ReadCondition(delayed, depth + 1);
            if (driver.ClassHash == Hash("NotMaterialDriver") &&
                driver.Properties.TryGetValue(Hash("mDriver"), out var inner))
                return new(GameMaterialBoolKind.Not, Children: new[] { ReadCondition(inner, depth + 1) });
            return new(GameMaterialBoolKind.Unsupported);
        }

        private static string ReadBuffKey(BinTreeStruct driver)
        {
            if (driver.Properties.TryGetValue(Hash("Spell"), out var spell) && spell is BinTreeHash { Value: > 0 } hash)
                return GameMaterialState.SpellBuffKey(hash.Value);

            return driver.Properties.TryGetValue(Hash("mScriptName"), out var script) && script is BinTreeString name
                ? name.Value : null;
        }
    }
}
