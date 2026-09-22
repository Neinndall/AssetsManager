using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Vfx.Authoring
{
    internal enum VfxEmitterTransformProperty
    {
        Translation,
        Rotation
    }

    internal enum VfxEmitterCurveFamily
    {
        Scalar,
        Vector2,
        Vector3,
        Vector4
    }

    internal enum VfxEmitterForceKind
    {
        Acceleration,
        Attraction,
        Noise,
        Drag,
        Orbital
    }

    internal enum VfxEmitterForceProperty
    {
        Acceleration,
        LocalSpace,
        Position,
        Radius,
        Frequency,
        VelocityDelta,
        AxisFraction,
        Strength,
        Direction
    }

    /// <summary>
    /// Performs narrow, source-preserving edits on one authored VFX emitter and validates the
    /// resulting BIN before replacing the source file. Runtime preview code stays model-based;
    /// this service is the single persistence boundary for VFX Studio authoring.
    /// </summary>
    internal static class VfxEmitterAuthoringService
    {
        private static readonly uint ComplexEmitterList = Fnv1a.HashLower("complexEmitterDefinitionData");
        private static readonly uint SimpleEmitterList = Fnv1a.HashLower("simpleEmitterDefinitionData");
        private static readonly uint EmitterClass = Fnv1a.HashLower("VfxEmitterDefinitionData");
        private static readonly uint TranslationOverride = Fnv1a.HashLower("translationOverride");
        private static readonly uint RotationOverride = Fnv1a.HashLower("rotationOverride");
        private static readonly uint ConstantValue = Fnv1a.HashLower("constantValue");
        private static readonly uint Dynamics = Fnv1a.HashLower("dynamics");
        private static readonly uint Times = Fnv1a.HashLower("times");
        private static readonly uint Values = Fnv1a.HashLower("values");
        private static readonly uint FieldCollection = Fnv1a.HashLower("fieldCollectionDefinition");
        private static readonly uint FieldCollectionClass = Fnv1a.HashLower("VfxFieldCollectionDefinitionData");
        private static readonly uint ValueFloatClass = Fnv1a.HashLower("ValueFloat");
        private static readonly uint ValueVector2Class = Fnv1a.HashLower("ValueVector2");
        private static readonly uint ValueVector3Class = Fnv1a.HashLower("ValueVector3");
        private static readonly uint ValueColorClass = Fnv1a.HashLower("ValueColor");
        private static readonly uint ValueColorRgbClass = Fnv1a.HashLower("ValueColorRgb");
        private static readonly uint AnimatedFloatClass = Fnv1a.HashLower("VfxAnimatedFloat");
        private static readonly uint AnimatedVector2Class = Fnv1a.HashLower("VfxAnimatedVector2f");
        private static readonly uint AnimatedVector3Class = Fnv1a.HashLower("VfxAnimatedVector3f");
        private static readonly uint AnimatedVector4Class = Fnv1a.HashLower("VfxAnimatedColor");
        private const uint AnimatedColorRgbClass = 0x8152c1ec;

        internal static bool TryWriteTransform(
            string binPath,
            uint systemPathHash,
            int sourceOrder,
            VfxEmitterTransformProperty property,
            Vector3 value,
            out VfxSystemDefinition updatedSystem,
            out string error)
            => TryWriteTransforms(
                binPath,
                systemPathHash,
                sourceOrder,
                property == VfxEmitterTransformProperty.Translation ? value : null,
                property == VfxEmitterTransformProperty.Rotation ? value : null,
                out updatedSystem,
                out error);

        internal static bool TryWriteTransforms(
            string binPath,
            uint systemPathHash,
            int sourceOrder,
            Vector3? translation,
            Vector3? rotation,
            out VfxSystemDefinition updatedSystem,
            out string error)
        {
            updatedSystem = null;
            error = null;
            if (!translation.HasValue && !rotation.HasValue)
            {
                error = "No emitter transform change was supplied.";
                return false;
            }
            if ((translation.HasValue && !IsFinite(translation.Value)) ||
                (rotation.HasValue && !IsFinite(rotation.Value)))
            {
                error = "Emitter transforms must contain finite values.";
                return false;
            }

            return TryEditEmitter(
                binPath,
                systemPathHash,
                sourceOrder,
                emitter =>
                {
                    if (translation.HasValue)
                        SetVector3(emitter, TranslationOverride, translation.Value);
                    if (rotation.HasValue)
                        SetVector3(emitter, RotationOverride, rotation.Value);
                    return null;
                },
                out updatedSystem,
                out error);
        }

        internal static bool TryActivateCurve(
            string binPath,
            uint systemPathHash,
            int sourceOrder,
            uint propertyHash,
            VfxEmitterCurveFamily family,
            Vector4 visibleValue,
            out VfxSystemDefinition updatedSystem,
            out string error)
        {
            updatedSystem = null;
            error = null;
            int width = CurveWidth(family);
            if (!IsFinite(visibleValue, width))
            {
                error = "Curve values must contain finite values.";
                return false;
            }

            return TryEditEmitter(
                binPath,
                systemPathHash,
                sourceOrder,
                emitter =>
                {
                    BinTreeStruct value;
                    if (emitter.Properties.TryGetValue(propertyHash, out BinTreeProperty authored) &&
                        authored is BinTreeStruct authoredValue)
                    {
                        if (!ValueClassMatches(authoredValue.ClassHash, family))
                            return "The authored Value* class does not match the requested curve family.";
                        value = authoredValue;
                    }
                    else
                    {
                        uint valueClass = ValueClass(family);
                        value = new BinTreeStruct(
                            propertyHash,
                            valueClass,
                            new[] { CurveLeaf(ConstantValue, family, visibleValue, valueClass) });
                        emitter.Properties[propertyHash] = value;
                    }
                    if (value.Properties.TryGetValue(Dynamics, out BinTreeProperty currentDynamics) &&
                        currentDynamics is BinTreeStruct)
                    {
                        return null;
                    }

                    BinTreeProperty first = CurveLeaf(0, family, visibleValue, value.ClassHash);
                    BinTreeProperty second = CurveLeaf(0, family, visibleValue, value.ClassHash);
                    var dynamics = new BinTreeStruct(
                        Dynamics,
                        DynamicsClass(family, value.ClassHash),
                        new BinTreeProperty[]
                        {
                            new BinTreeContainer(Times, BinPropertyType.F32, new BinTreeProperty[]
                            {
                                new BinTreeF32(0, 0f),
                                new BinTreeF32(0, 1f)
                            }),
                            new BinTreeContainer(Values, first.Type, new[] { first, second })
                        });
                    value.Properties[Dynamics] = dynamics;
                    return null;
                },
                out updatedSystem,
                out error);
        }

        internal static bool TryWriteCurveKey(
            string binPath,
            uint systemPathHash,
            int sourceOrder,
            uint propertyHash,
            VfxEmitterCurveFamily family,
            int keyIndex,
            float time,
            Vector4 value,
            out VfxSystemDefinition updatedSystem,
            out string error)
        {
            updatedSystem = null;
            error = null;
            if (keyIndex < 0 || !float.IsFinite(time) || !IsFinite(value, CurveWidth(family)))
            {
                error = "Curve key time and values must be finite.";
                return false;
            }

            return TryEditEmitter(
                binPath,
                systemPathHash,
                sourceOrder,
                emitter => WriteCurveKey(emitter, propertyHash, family, keyIndex, time, value),
                out updatedSystem,
                out error);
        }

        internal static bool TryInsertCurveKey(
            string binPath,
            uint systemPathHash,
            int sourceOrder,
            uint propertyHash,
            VfxEmitterCurveFamily family,
            int keyIndex,
            float time,
            Vector4 value,
            out VfxSystemDefinition updatedSystem,
            out string error)
        {
            updatedSystem = null;
            error = null;
            if (keyIndex < 0 || !float.IsFinite(time) || !IsFinite(value, CurveWidth(family)))
            {
                error = "Curve key time and values must be finite.";
                return false;
            }

            return TryEditEmitter(
                binPath,
                systemPathHash,
                sourceOrder,
                emitter => InsertCurveKey(emitter, propertyHash, family, keyIndex, time, value),
                out updatedSystem,
                out error);
        }

        internal static bool TryRemoveCurveKeys(
            string binPath,
            uint systemPathHash,
            int sourceOrder,
            uint propertyHash,
            IReadOnlyCollection<int> keyIndices,
            out VfxSystemDefinition updatedSystem,
            out string error)
        {
            updatedSystem = null;
            error = null;
            int[] indices = keyIndices?
                .Where(index => index >= 0)
                .Distinct()
                .OrderByDescending(index => index)
                .ToArray() ?? Array.Empty<int>();
            if (indices.Length == 0)
            {
                error = "No curve keys were selected.";
                return false;
            }

            return TryEditEmitter(
                binPath,
                systemPathHash,
                sourceOrder,
                emitter => RemoveCurveKeys(emitter, propertyHash, indices),
                out updatedSystem,
                out error);
        }

        internal static bool TryAddForce(
            string binPath,
            uint systemPathHash,
            int sourceOrder,
            VfxEmitterForceKind kind,
            out VfxSystemDefinition updatedSystem,
            out string error)
        {
            return TryEditEmitter(
                binPath,
                systemPathHash,
                sourceOrder,
                emitter =>
                {
                    BinTreeStruct collection = EnsureFieldCollection(emitter);
                    if (collection == null)
                        return "The authored field collection has an unexpected BIN shape.";
                    ForceShape shape = Force(kind);
                    if (!collection.Properties.TryGetValue(shape.ListHash, out BinTreeProperty listProperty))
                    {
                        listProperty = new BinTreeContainer(
                            shape.ListHash,
                            BinPropertyType.Struct,
                            Array.Empty<BinTreeProperty>());
                        collection.Properties[shape.ListHash] = listProperty;
                    }
                    if (listProperty is not BinTreeContainer list || list.ElementType != BinPropertyType.Struct)
                        return "The authored force list has an unexpected BIN shape.";

                    list.Add(new BinTreeStruct(0, shape.ClassHash, Array.Empty<BinTreeProperty>()));
                    return null;
                },
                out updatedSystem,
                out error);
        }

        internal static bool TryRemoveForce(
            string binPath,
            uint systemPathHash,
            int sourceOrder,
            VfxEmitterForceKind kind,
            int forceIndex,
            out VfxSystemDefinition updatedSystem,
            out string error)
        {
            if (forceIndex < 0)
            {
                updatedSystem = null;
                error = "The force index is invalid.";
                return false;
            }

            return TryEditEmitter(
                binPath,
                systemPathHash,
                sourceOrder,
                emitter =>
                {
                    ForceShape shape = Force(kind);
                    if (!TryFindForce(emitter, shape, forceIndex, out BinTreeStruct target, out string findError))
                        return findError;
                    var collection = (BinTreeStruct)emitter.Properties[FieldCollection];
                    var list = (BinTreeContainer)collection.Properties[shape.ListHash];
                    if (!list.Remove(target)) return "The selected force could not be removed.";
                    return null;
                },
                out updatedSystem,
                out error);
        }

        internal static bool TryWriteForceScalar(
            string binPath,
            uint systemPathHash,
            int sourceOrder,
            VfxEmitterForceKind kind,
            int forceIndex,
            VfxEmitterForceProperty property,
            float value,
            out VfxSystemDefinition updatedSystem,
            out string error)
        {
            if (!float.IsFinite(value))
            {
                updatedSystem = null;
                error = "Force values must be finite.";
                return false;
            }
            return TryWriteForceValue(
                binPath,
                systemPathHash,
                sourceOrder,
                kind,
                forceIndex,
                property,
                ForceValueShape.Scalar,
                new Vector4(value, 0f, 0f, 0f),
                false,
                out updatedSystem,
                out error);
        }

        internal static bool TryWriteForceVector(
            string binPath,
            uint systemPathHash,
            int sourceOrder,
            VfxEmitterForceKind kind,
            int forceIndex,
            VfxEmitterForceProperty property,
            Vector3 value,
            out VfxSystemDefinition updatedSystem,
            out string error)
        {
            if (!IsFinite(value))
            {
                updatedSystem = null;
                error = "Force values must be finite.";
                return false;
            }
            return TryWriteForceValue(
                binPath,
                systemPathHash,
                sourceOrder,
                kind,
                forceIndex,
                property,
                ForceValueShape.Vector3,
                new Vector4(value, 0f),
                false,
                out updatedSystem,
                out error);
        }

        internal static bool TryWriteForceBool(
            string binPath,
            uint systemPathHash,
            int sourceOrder,
            VfxEmitterForceKind kind,
            int forceIndex,
            VfxEmitterForceProperty property,
            bool value,
            out VfxSystemDefinition updatedSystem,
            out string error)
            => TryWriteForceValue(
                binPath,
                systemPathHash,
                sourceOrder,
                kind,
                forceIndex,
                property,
                ForceValueShape.Bool,
                Vector4.Zero,
                value,
                out updatedSystem,
                out error);

        internal static bool TryWriteForceCurveKey(
            string binPath,
            uint systemPathHash,
            int sourceOrder,
            VfxEmitterForceKind kind,
            int forceIndex,
            VfxEmitterForceProperty property,
            int keyIndex,
            float time,
            Vector4 value,
            out VfxSystemDefinition updatedSystem,
            out string error)
            => TryEditForceCurve(
                binPath,
                systemPathHash,
                sourceOrder,
                kind,
                forceIndex,
                property,
                (force, propertyHash, family) => WriteCurveKey(force, propertyHash, family, keyIndex, time, value),
                out updatedSystem,
                out error);

        internal static bool TryInsertForceCurveKey(
            string binPath,
            uint systemPathHash,
            int sourceOrder,
            VfxEmitterForceKind kind,
            int forceIndex,
            VfxEmitterForceProperty property,
            int keyIndex,
            float time,
            Vector4 value,
            out VfxSystemDefinition updatedSystem,
            out string error)
            => TryEditForceCurve(
                binPath,
                systemPathHash,
                sourceOrder,
                kind,
                forceIndex,
                property,
                (force, propertyHash, family) => InsertCurveKey(force, propertyHash, family, keyIndex, time, value),
                out updatedSystem,
                out error);

        internal static bool TryRemoveForceCurveKeys(
            string binPath,
            uint systemPathHash,
            int sourceOrder,
            VfxEmitterForceKind kind,
            int forceIndex,
            VfxEmitterForceProperty property,
            IReadOnlyCollection<int> keyIndices,
            out VfxSystemDefinition updatedSystem,
            out string error)
        {
            int[] indices = keyIndices?
                .Where(index => index >= 0)
                .Distinct()
                .OrderByDescending(index => index)
                .ToArray() ?? Array.Empty<int>();
            if (indices.Length == 0)
            {
                updatedSystem = null;
                error = "No curve keys were selected.";
                return false;
            }

            return TryEditForceCurve(
                binPath,
                systemPathHash,
                sourceOrder,
                kind,
                forceIndex,
                property,
                (force, propertyHash, _) => RemoveCurveKeys(force, propertyHash, indices),
                out updatedSystem,
                out error);
        }

        private static bool TryEditForceCurve(
            string binPath,
            uint systemPathHash,
            int sourceOrder,
            VfxEmitterForceKind kind,
            int forceIndex,
            VfxEmitterForceProperty property,
            Func<BinTreeStruct, uint, VfxEmitterCurveFamily, string> edit,
            out VfxSystemDefinition updatedSystem,
            out string error)
        {
            if (forceIndex < 0)
            {
                updatedSystem = null;
                error = "The force index is invalid.";
                return false;
            }

            return TryEditEmitter(
                binPath,
                systemPathHash,
                sourceOrder,
                emitter =>
                {
                    ForceShape forceShape = Force(kind);
                    ForcePropertyShape propertyShape;
                    try
                    {
                        propertyShape = ForceProperty(kind, property);
                    }
                    catch (ArgumentException ex)
                    {
                        return ex.Message;
                    }
                    if (!propertyShape.Animated || propertyShape.Shape == ForceValueShape.Bool)
                        return "The selected force property is not curve-authored.";
                    if (!TryFindForce(emitter, forceShape, forceIndex, out BinTreeStruct force, out string findError))
                        return findError;

                    VfxEmitterCurveFamily family = propertyShape.Shape == ForceValueShape.Scalar
                        ? VfxEmitterCurveFamily.Scalar
                        : VfxEmitterCurveFamily.Vector3;
                    return edit(force, propertyShape.Hash, family);
                },
                out updatedSystem,
                out error);
        }

        private static bool TryWriteForceValue(
            string binPath,
            uint systemPathHash,
            int sourceOrder,
            VfxEmitterForceKind kind,
            int forceIndex,
            VfxEmitterForceProperty property,
            ForceValueShape requestedShape,
            Vector4 numericValue,
            bool boolValue,
            out VfxSystemDefinition updatedSystem,
            out string error)
        {
            if (forceIndex < 0)
            {
                updatedSystem = null;
                error = "The force index is invalid.";
                return false;
            }

            return TryEditEmitter(
                binPath,
                systemPathHash,
                sourceOrder,
                emitter =>
                {
                    ForceShape forceShape = Force(kind);
                    ForcePropertyShape propertyShape = ForceProperty(kind, property);
                    if (propertyShape.Shape != requestedShape)
                        return "The selected force property does not match the supplied value shape.";
                    if (!TryFindForce(emitter, forceShape, forceIndex, out BinTreeStruct force, out string findError))
                        return findError;

                    if (propertyShape.Animated)
                    {
                        uint valueClass = propertyShape.Shape == ForceValueShape.Scalar
                            ? ValueFloatClass
                            : ValueVector3Class;
                        if (!force.Properties.TryGetValue(propertyShape.Hash, out BinTreeProperty existing))
                        {
                            force.Properties[propertyShape.Hash] = new BinTreeStruct(
                                propertyShape.Hash,
                                valueClass,
                                new[] { ForceLeaf(ConstantValue, propertyShape.Shape, numericValue, boolValue) });
                            return null;
                        }
                        if (existing is not BinTreeStruct value)
                            return "The authored force property has an unexpected BIN shape.";
                        value.Properties[ConstantValue] = ForceLeaf(
                            ConstantValue,
                            propertyShape.Shape,
                            numericValue,
                            boolValue);
                        return null;
                    }

                    BinTreeProperty leaf = ForceLeaf(
                        propertyShape.Hash,
                        propertyShape.Shape,
                        numericValue,
                        boolValue);
                    if (force.Properties.TryGetValue(propertyShape.Hash, out BinTreeProperty direct) &&
                        direct.Type != leaf.Type)
                    {
                        return "The authored force property has an unexpected BIN type.";
                    }
                    force.Properties[propertyShape.Hash] = leaf;
                    return null;
                },
                out updatedSystem,
                out error);
        }

        private static bool TryEditEmitter(
            string binPath,
            uint systemPathHash,
            int sourceOrder,
            Func<BinTreeStruct, string> edit,
            out VfxSystemDefinition updatedSystem,
            out string error)
        {
            updatedSystem = null;
            error = null;
            if (string.IsNullOrWhiteSpace(binPath) || !File.Exists(binPath))
            {
                error = "The source BIN is unavailable.";
                return false;
            }
            if (sourceOrder < 0)
            {
                error = "The emitter index is invalid.";
                return false;
            }

            string fullPath = Path.GetFullPath(binPath);
            string temporaryPath = fullPath + ".vfxstudio." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                BinTree tree;
                using (FileStream input = File.OpenRead(fullPath))
                    tree = new BinTree(input);

                if (!tree.Objects.TryGetValue(systemPathHash, out BinTreeObject system))
                {
                    error = $"VFX system 0x{systemPathHash:X8} was not found in its source BIN.";
                    return false;
                }

                BinTreeStruct emitter = FindEmitter(system, sourceOrder);
                if (emitter == null)
                {
                    error = $"Emitter [{sourceOrder}] was not found in VFX system 0x{systemPathHash:X8}.";
                    return false;
                }

                error = edit(emitter);
                if (!string.IsNullOrEmpty(error)) return false;

                using (var output = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None))
                {
                    tree.Write(output);
                    output.Flush(flushToDisk: true);
                }

                // Re-open the exact bytes that would replace the user's file. This catches writer,
                // container and property-shape mistakes before the original BIN is touched.
                BinTree verified;
                using (FileStream validation = File.OpenRead(temporaryPath))
                    verified = new BinTree(validation);
                if (!VfxSystemParser.ExtractAll(verified).TryGetValue(systemPathHash, out updatedSystem))
                {
                    error = "The edited BIN no longer contains the selected VFX system.";
                    updatedSystem = null;
                    return false;
                }

                File.Move(temporaryPath, fullPath, overwrite: true);
                temporaryPath = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                updatedSystem = null;
                return false;
            }
            finally
            {
                if (!string.IsNullOrEmpty(temporaryPath))
                {
                    try
                    {
                        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                    }
                    catch
                    {
                        // Best-effort cleanup; never mask the authoring result with temp-file cleanup.
                    }
                }
            }
        }

        private static string WriteCurveKey(
            BinTreeStruct holder,
            uint propertyHash,
            VfxEmitterCurveFamily family,
            int keyIndex,
            float time,
            Vector4 value)
        {
            if (keyIndex < 0 || !float.IsFinite(time) || !IsFinite(value, CurveWidth(family)))
                return "Curve key time and values must be finite.";

            string validation = TryGetCurveContainers(
                holder,
                propertyHash,
                out _,
                out BinTreeContainer times,
                out BinTreeContainer values);
            if (validation != null) return validation;
            if (keyIndex >= times.Elements.Count || keyIndex >= values.Elements.Count)
                return $"Curve key [{keyIndex}] does not exist.";

            uint valueClass = CurveValueClass(holder, propertyHash);
            BinTreeProperty keyValue = CurveLeaf(0, family, value, valueClass);
            if (values.ElementType != keyValue.Type)
                return $"Curve value type {values.ElementType} does not match {keyValue.Type}.";

            var nextTimes = times.Elements.ToArray();
            var nextValues = values.Elements.ToArray();
            nextTimes[keyIndex] = new BinTreeF32(0, time);
            nextValues[keyIndex] = keyValue;
            ReplaceCurveContainers(holder, propertyHash, times, values, nextTimes, nextValues);
            return null;
        }

        private static string InsertCurveKey(
            BinTreeStruct holder,
            uint propertyHash,
            VfxEmitterCurveFamily family,
            int keyIndex,
            float time,
            Vector4 value)
        {
            if (keyIndex < 0 || !float.IsFinite(time) || !IsFinite(value, CurveWidth(family)))
                return "Curve key time and values must be finite.";

            string validation = TryGetCurveContainers(
                holder,
                propertyHash,
                out _,
                out BinTreeContainer times,
                out BinTreeContainer values);
            if (validation != null) return validation;
            if (keyIndex > times.Elements.Count || keyIndex > values.Elements.Count)
                return $"Curve insertion index [{keyIndex}] is out of range.";

            uint valueClass = CurveValueClass(holder, propertyHash);
            BinTreeProperty keyValue = CurveLeaf(0, family, value, valueClass);
            if (values.ElementType != keyValue.Type)
                return $"Curve value type {values.ElementType} does not match {keyValue.Type}.";

            var nextTimes = times.Elements.ToList();
            var nextValues = values.Elements.ToList();
            nextTimes.Insert(keyIndex, new BinTreeF32(0, time));
            nextValues.Insert(keyIndex, keyValue);
            ReplaceCurveContainers(holder, propertyHash, times, values, nextTimes, nextValues);
            return null;
        }

        private static string RemoveCurveKeys(
            BinTreeStruct holder,
            uint propertyHash,
            IReadOnlyList<int> indices)
        {
            string validation = TryGetCurveContainers(
                holder,
                propertyHash,
                out _,
                out BinTreeContainer times,
                out BinTreeContainer values);
            if (validation != null) return validation;
            if (indices.Any(index => index < 0 || index >= times.Elements.Count || index >= values.Elements.Count))
                return "One or more selected curve keys do not exist.";

            var nextTimes = times.Elements.ToList();
            var nextValues = values.Elements.ToList();
            foreach (int index in indices.OrderByDescending(index => index))
            {
                nextTimes.RemoveAt(index);
                nextValues.RemoveAt(index);
            }
            ReplaceCurveContainers(holder, propertyHash, times, values, nextTimes, nextValues);
            return null;
        }

        private static string TryGetCurveContainers(
            BinTreeStruct emitter,
            uint propertyHash,
            out BinTreeStruct dynamics,
            out BinTreeContainer times,
            out BinTreeContainer values)
        {
            dynamics = null;
            times = null;
            values = null;
            if (!emitter.Properties.TryGetValue(propertyHash, out BinTreeProperty property) ||
                property is not BinTreeStruct value)
            {
                return "The selected property is not an authored Value* definition.";
            }
            if (!value.Properties.TryGetValue(Dynamics, out BinTreeProperty dynamicsProperty) ||
                dynamicsProperty is not BinTreeStruct authoredDynamics)
            {
                return "The selected property does not have an active dynamics curve.";
            }
            if (!authoredDynamics.Properties.TryGetValue(Times, out BinTreeProperty timesProperty) ||
                timesProperty is not BinTreeContainer timeList ||
                timeList.ElementType != BinPropertyType.F32)
            {
                return "The curve times list has an unexpected BIN shape.";
            }
            if (!authoredDynamics.Properties.TryGetValue(Values, out BinTreeProperty valuesProperty) ||
                valuesProperty is not BinTreeContainer valueList)
            {
                return "The curve values list has an unexpected BIN shape.";
            }
            if (timeList.Elements.Count != valueList.Elements.Count)
            {
                return "The curve time/value lists are not aligned.";
            }

            dynamics = authoredDynamics;
            times = timeList;
            values = valueList;
            return null;
        }

        private static void ReplaceCurveContainers(
            BinTreeStruct emitter,
            uint propertyHash,
            BinTreeContainer oldTimes,
            BinTreeContainer oldValues,
            IEnumerable<BinTreeProperty> nextTimes,
            IEnumerable<BinTreeProperty> nextValues)
        {
            _ = TryGetCurveContainers(
                emitter,
                propertyHash,
                out BinTreeStruct dynamics,
                out _,
                out _);
            dynamics.Properties[Times] = new BinTreeContainer(Times, oldTimes.ElementType, nextTimes);
            dynamics.Properties[Values] = new BinTreeContainer(Values, oldValues.ElementType, nextValues);
        }

        private static BinTreeProperty CurveLeaf(
            uint nameHash,
            VfxEmitterCurveFamily family,
            Vector4 value,
            uint valueClassHash)
            => family switch
            {
                VfxEmitterCurveFamily.Scalar => new BinTreeF32(nameHash, value.X),
                VfxEmitterCurveFamily.Vector2 => new BinTreeVector2(nameHash, new Vector2(value.X, value.Y)),
                VfxEmitterCurveFamily.Vector3 => new BinTreeVector3(nameHash, new Vector3(value.X, value.Y, value.Z)),
                VfxEmitterCurveFamily.Vector4 when valueClassHash == ValueColorRgbClass =>
                    new BinTreeVector3(nameHash, new Vector3(value.X, value.Y, value.Z)),
                VfxEmitterCurveFamily.Vector4 => new BinTreeVector4(nameHash, value),
                _ => throw new ArgumentOutOfRangeException(nameof(family))
            };

        private static uint CurveValueClass(BinTreeStruct holder, uint propertyHash)
            => holder.Properties.TryGetValue(propertyHash, out BinTreeProperty property) &&
               property is BinTreeStruct value
                ? value.ClassHash
                : 0;

        private static uint DynamicsClass(VfxEmitterCurveFamily family, uint valueClassHash)
            => family switch
            {
                VfxEmitterCurveFamily.Scalar => AnimatedFloatClass,
                VfxEmitterCurveFamily.Vector2 => AnimatedVector2Class,
                VfxEmitterCurveFamily.Vector3 => AnimatedVector3Class,
                VfxEmitterCurveFamily.Vector4 when valueClassHash == ValueColorRgbClass => AnimatedColorRgbClass,
                VfxEmitterCurveFamily.Vector4 => AnimatedVector4Class,
                _ => throw new ArgumentOutOfRangeException(nameof(family))
            };

        private static uint ValueClass(VfxEmitterCurveFamily family)
            => family switch
            {
                VfxEmitterCurveFamily.Scalar => ValueFloatClass,
                VfxEmitterCurveFamily.Vector2 => ValueVector2Class,
                VfxEmitterCurveFamily.Vector3 => ValueVector3Class,
                VfxEmitterCurveFamily.Vector4 => ValueColorClass,
                _ => throw new ArgumentOutOfRangeException(nameof(family))
            };

        private static bool ValueClassMatches(uint classHash, VfxEmitterCurveFamily family)
            => classHash == ValueClass(family) ||
               (family == VfxEmitterCurveFamily.Vector4 && classHash == ValueColorRgbClass);

        private static int CurveWidth(VfxEmitterCurveFamily family)
            => family switch
            {
                VfxEmitterCurveFamily.Scalar => 1,
                VfxEmitterCurveFamily.Vector2 => 2,
                VfxEmitterCurveFamily.Vector3 => 3,
                VfxEmitterCurveFamily.Vector4 => 4,
                _ => throw new ArgumentOutOfRangeException(nameof(family))
            };

        private enum ForceValueShape
        {
            Scalar,
            Vector3,
            Bool
        }

        private readonly record struct ForceShape(uint ListHash, uint ClassHash);
        private readonly record struct ForcePropertyShape(uint Hash, ForceValueShape Shape, bool Animated);

        private static ForceShape Force(VfxEmitterForceKind kind)
            => kind switch
            {
                VfxEmitterForceKind.Acceleration => new(
                    Fnv1a.HashLower("fieldAccelerationDefinitions"),
                    Fnv1a.HashLower("VfxFieldAccelerationDefinitionData")),
                VfxEmitterForceKind.Attraction => new(
                    Fnv1a.HashLower("fieldAttractionDefinitions"),
                    Fnv1a.HashLower("VfxFieldAttractionDefinitionData")),
                VfxEmitterForceKind.Noise => new(
                    Fnv1a.HashLower("fieldNoiseDefinitions"),
                    Fnv1a.HashLower("VfxFieldNoiseDefinitionData")),
                VfxEmitterForceKind.Drag => new(
                    Fnv1a.HashLower("fieldDragDefinitions"),
                    Fnv1a.HashLower("VfxFieldDragDefinitionData")),
                VfxEmitterForceKind.Orbital => new(
                    Fnv1a.HashLower("fieldOrbitalDefinitions"),
                    Fnv1a.HashLower("VfxFieldOrbitalDefinitionData")),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };

        private static ForcePropertyShape ForceProperty(
            VfxEmitterForceKind kind,
            VfxEmitterForceProperty property)
        {
            return (kind, property) switch
            {
                (VfxEmitterForceKind.Acceleration, VfxEmitterForceProperty.Acceleration) =>
                    new(Fnv1a.HashLower("acceleration"), ForceValueShape.Vector3, Animated: true),
                (VfxEmitterForceKind.Acceleration, VfxEmitterForceProperty.LocalSpace) =>
                    new(Fnv1a.HashLower("isLocalSpace"), ForceValueShape.Bool, Animated: false),

                (VfxEmitterForceKind.Attraction, VfxEmitterForceProperty.Position) =>
                    new(Fnv1a.HashLower("Position"), ForceValueShape.Vector3, Animated: true),
                (VfxEmitterForceKind.Attraction, VfxEmitterForceProperty.Radius) =>
                    new(Fnv1a.HashLower("radius"), ForceValueShape.Scalar, Animated: true),
                (VfxEmitterForceKind.Attraction, VfxEmitterForceProperty.Acceleration) =>
                    new(Fnv1a.HashLower("acceleration"), ForceValueShape.Scalar, Animated: true),

                (VfxEmitterForceKind.Noise, VfxEmitterForceProperty.Position) =>
                    new(Fnv1a.HashLower("Position"), ForceValueShape.Vector3, Animated: true),
                (VfxEmitterForceKind.Noise, VfxEmitterForceProperty.Radius) =>
                    new(Fnv1a.HashLower("radius"), ForceValueShape.Scalar, Animated: true),
                (VfxEmitterForceKind.Noise, VfxEmitterForceProperty.Frequency) =>
                    new(Fnv1a.HashLower("frequency"), ForceValueShape.Scalar, Animated: true),
                (VfxEmitterForceKind.Noise, VfxEmitterForceProperty.VelocityDelta) =>
                    new(Fnv1a.HashLower("velocityDelta"), ForceValueShape.Scalar, Animated: true),
                (VfxEmitterForceKind.Noise, VfxEmitterForceProperty.AxisFraction) =>
                    new(Fnv1a.HashLower("axisFraction"), ForceValueShape.Vector3, Animated: false),

                (VfxEmitterForceKind.Drag, VfxEmitterForceProperty.Position) =>
                    new(Fnv1a.HashLower("Position"), ForceValueShape.Vector3, Animated: true),
                (VfxEmitterForceKind.Drag, VfxEmitterForceProperty.Radius) =>
                    new(Fnv1a.HashLower("radius"), ForceValueShape.Scalar, Animated: true),
                (VfxEmitterForceKind.Drag, VfxEmitterForceProperty.Strength) =>
                    new(Fnv1a.HashLower("strength"), ForceValueShape.Scalar, Animated: true),

                (VfxEmitterForceKind.Orbital, VfxEmitterForceProperty.Direction) =>
                    new(Fnv1a.HashLower("direction"), ForceValueShape.Vector3, Animated: true),
                (VfxEmitterForceKind.Orbital, VfxEmitterForceProperty.LocalSpace) =>
                    new(Fnv1a.HashLower("isLocalSpace"), ForceValueShape.Bool, Animated: false),
                _ => throw new ArgumentException($"{property} is not valid for {kind}.", nameof(property))
            };
        }

        private static bool TryFindForce(
            BinTreeStruct emitter,
            ForceShape shape,
            int forceIndex,
            out BinTreeStruct force,
            out string error)
        {
            force = null;
            error = null;
            if (!emitter.Properties.TryGetValue(FieldCollection, out BinTreeProperty fieldProperty) ||
                fieldProperty is not BinTreeStruct collection)
            {
                error = "The emitter does not have an authored field collection.";
                return false;
            }
            if (!collection.Properties.TryGetValue(shape.ListHash, out BinTreeProperty listProperty) ||
                listProperty is not BinTreeContainer list ||
                list.ElementType != BinPropertyType.Struct)
            {
                error = "The selected force list is unavailable.";
                return false;
            }

            if (list.Elements.Any(element =>
                    element is not BinTreeStruct candidate || candidate.ClassHash != shape.ClassHash))
            {
                error = "The selected force list contains an unsupported authored class.";
                return false;
            }
            if (forceIndex >= list.Elements.Count)
            {
                error = $"Force [{forceIndex}] was not found.";
                return false;
            }

            force = (BinTreeStruct)list.Elements[forceIndex];
            return true;
        }

        private static void SetVector3(BinTreeStruct holder, uint propertyHash, Vector3 value)
        {
            if (holder.Properties.TryGetValue(propertyHash, out BinTreeProperty existing) &&
                existing is BinTreeVector3 vector)
            {
                vector.Value = value;
                return;
            }

            holder.Properties[propertyHash] = new BinTreeVector3(propertyHash, value);
        }

        private static BinTreeProperty ForceLeaf(
            uint nameHash,
            ForceValueShape shape,
            Vector4 numericValue,
            bool boolValue)
            => shape switch
            {
                ForceValueShape.Scalar => new BinTreeF32(nameHash, numericValue.X),
                ForceValueShape.Vector3 => new BinTreeVector3(
                    nameHash,
                    new Vector3(numericValue.X, numericValue.Y, numericValue.Z)),
                ForceValueShape.Bool => new BinTreeBool(nameHash, boolValue),
                _ => throw new ArgumentOutOfRangeException(nameof(shape))
            };

        private static BinTreeStruct EnsureFieldCollection(BinTreeStruct emitter)
        {
            if (emitter.Properties.TryGetValue(FieldCollection, out BinTreeProperty existing))
                return existing is BinTreeStruct collection && collection.ClassHash == FieldCollectionClass
                    ? collection
                    : null;

            var created = new BinTreeStruct(
                FieldCollection,
                FieldCollectionClass,
                Array.Empty<BinTreeProperty>());
            emitter.Properties[FieldCollection] = created;
            return created;
        }

        private static BinTreeStruct FindEmitter(BinTreeObject system, int sourceOrder)
        {
            int parsedIndex = 0;
            foreach (uint listHash in new[] { ComplexEmitterList, SimpleEmitterList })
            {
                if (!system.Properties.TryGetValue(listHash, out BinTreeProperty property) ||
                    property is not BinTreeContainer container)
                {
                    continue;
                }

                foreach (BinTreeProperty element in container.Elements)
                {
                    if (element is not BinTreeStruct emitter || emitter.ClassHash != EmitterClass)
                        continue;
                    if (parsedIndex == sourceOrder) return emitter;
                    parsedIndex++;
                }
            }

            return null;
        }

        private static bool IsFinite(Vector3 value)
            => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

        private static bool IsFinite(Vector4 value, int width)
        {
            if (width >= 1 && !float.IsFinite(value.X)) return false;
            if (width >= 2 && !float.IsFinite(value.Y)) return false;
            if (width >= 3 && !float.IsFinite(value.Z)) return false;
            if (width >= 4 && !float.IsFinite(value.W)) return false;
            return true;
        }
    }
}
