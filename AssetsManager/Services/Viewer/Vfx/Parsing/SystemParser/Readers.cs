using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using static AssetsManager.Services.Viewer.Vfx.Parsing.VfxParsingSchema;
using static AssetsManager.Services.Viewer.Vfx.Parsing.VfxValueParser;

namespace AssetsManager.Services.Viewer.Vfx.Parsing
{
    internal static partial class VfxSystemParser
    {
        private static bool HasValue(IReadOnlyDictionary<uint, BinTreeProperty> properties, uint fieldHash)
        {
            if (!properties.TryGetValue(fieldHash, out BinTreeProperty property)) return false;
            return property switch
            {
                BinTreeOptional optional => optional.Value is not null,
                BinTreeStruct structure => structure.ClassHash != 0,
                _ => true
            };
        }

        private static bool HasElements(IReadOnlyDictionary<uint, BinTreeProperty> properties, uint fieldHash)
            => properties.TryGetValue(fieldHash, out BinTreeProperty property) && property switch
            {
                BinTreeContainer container => container.Elements.Count > 0,
                BinTreeMap map => map.Count > 0,
                _ => HasValue(properties, fieldHash)
            };

        private static VfxFlexShapeDefinition ReadFlexShape(
            IReadOnlyDictionary<uint, BinTreeProperty> emitterProperties)
        {
            if (Get(emitterProperties, F_flexShapeDefinition) is not BinTreeStruct flex) return null;
            return new VfxFlexShapeDefinition(
                GetF32(flex.Properties, F_scaleBirthScaleByBoundObjectSize) ?? 0f,
                GetF32(flex.Properties, F_scaleEmitOffsetByBoundObjectSize) ?? 0f);
        }

        private static VfxLingerDefinition ReadLinger(
            IReadOnlyDictionary<uint, BinTreeProperty> emitterProperties)
        {
            if (Get(emitterProperties, F_linger) is not BinTreeStruct linger) return null;
            var p = linger.Properties;
            VfxCurve3? rotation = GetBool(p, F_useLingerRotation)
                ? ReadCurve3(p, F_lingerRotation) ?? VfxCurve3.Const(Vector3.Zero)
                : null;
            VfxCurve3? scale = GetBool(p, F_useLingerScale)
                ? ReadCurve3(p, F_lingerScale, Vector3.One) ?? VfxCurve3.Const(Vector3.One)
                : null;
            VfxCurve4? color = GetBool(p, F_useLingerColor)
                ? ReadCurve4(p, F_lingerColor) ?? VfxCurve4.Const(Vector4.One)
                : null;
            VfxCurve3? acceleration = GetBool(p, F_useLingerAcceleration)
                ? ReadCurve3(p, F_lingerAcceleration) ?? VfxCurve3.Const(Vector3.Zero)
                : null;
            VfxCurve3? velocity = GetBool(p, F_useLingerVelocity)
                ? ReadCurve3(p, F_lingerVelocity) ?? VfxCurve3.Const(Vector3.Zero)
                : null;
            VfxCurve3? drag = GetBool(p, F_useLingerDrag)
                ? ReadCurve3(p, F_lingerDrag) ?? VfxCurve3.Const(Vector3.Zero)
                : null;
            return new VfxLingerDefinition(rotation, scale, color, acceleration, velocity, drag);
        }

        private static VfxPaletteDefinition ReadPalette(
            IReadOnlyDictionary<uint, BinTreeProperty> emitterProperties)
        {
            if (Get(emitterProperties, F_paletteDefinition) is not BinTreeStruct palette) return null;
            Vector4 luma = new(0.299f, 0.587f, 0.114f, 0f);
            VfxCurve4? sourceMixColor = ReadCurve4(palette.Properties, F_paletteSourceMixColor, luma)
                ?? ReadCurve4(palette.Properties, F_palleteSourceMixColor, luma);
            return new VfxPaletteDefinition(
                GetI32(palette.Properties, F_paletteCount) ?? 1,
                ReadCurve3(palette.Properties, F_paletteSelector) ?? VfxCurve3.Const(Vector3.Zero),
                ReadAsset(palette.Properties, F_paletteTexture, ".tex"),
                sourceMixColor?.Sample(0f) ?? luma,
                ReadCurveF(palette.Properties, F_paletteScrollU) ?? VfxCurveF.Zero,
                ReadCurveF(palette.Properties, F_paletteScrollV) ?? VfxCurveF.Zero,
                NormalizeEnumByte(GetU8(palette.Properties, F_paletteAddressMode), 3, 1));
        }

        private static IReadOnlyList<string> ReadStringContainer(BinTreeProperty property)
        {
            if (property is not BinTreeContainer container || container.Elements.Count == 0)
                return Array.Empty<string>();
            return container.Elements
                .OfType<BinTreeString>()
                .Select(static value => value.Value)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
        }

        private static IReadOnlyList<string> ReadAssetContainer(BinTreeProperty property, string extension)
        {
            if (property is not BinTreeContainer container || container.Elements.Count == 0)
                return Array.Empty<string>();
            return container.Elements
                .Select(value => ReadAsset(value, extension))
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
        }

        private static uint ReadCustomMaterialPathHash(IReadOnlyDictionary<uint, BinTreeProperty> emitterProperties)
        {
            if (Get(emitterProperties, F_customMaterial) is not BinTreeStruct custom) return 0u;
            return AsU32(Get(custom.Properties, F_customMaterialLink)) ?? 0u;
        }

        private static VfxEmissionSurfaceDefinition ReadEmissionSurface(
            IReadOnlyDictionary<uint, BinTreeProperty> emitterProperties)
        {
            if (Get(emitterProperties, F_emissionSurfaceDefinition) is not BinTreeStruct outer) return null;

            BinTreeStruct held = outer;
            BinTreeProperty nested = Get(outer.Properties, F_emissionSurface);
            if (nested is BinTreeStruct nestedSurface)
            {
                if (nestedSurface.ClassHash != EmissionSkeletonClass && nestedSurface.ClassHash != EmissionMeshClass)
                    return null;
                held = nestedSurface;
            }
            else if (nested is not null)
            {
                return null;
            }

            IReadOnlyDictionary<uint, BinTreeProperty> properties = held.Properties;
            return new VfxEmissionSurfaceDefinition(
                held.ClassHash == EmissionSkeletonClass ? VfxEmissionSurfaceKind.Skeleton : VfxEmissionSurfaceKind.Mesh,
                ReadAsset(properties, F_emissionMesh, ".skn"),
                ReadAsset(properties, F_emissionSkeleton, ".skl"),
                ReadHashContainer(Get(properties, F_emissionSubmeshes)),
                ReadHashContainer(Get(properties, F_emissionJointMask)),
                GetF32(properties, F_emissionMeshScale) ?? 1f,
                Math.Clamp(GetI32(properties, F_emissionMaxJointWeights) ?? GetU8(properties, F_emissionMaxJointWeights) ?? 4, 1, 4),
                held.ClassHash == EmissionSkeletonClass || GetBool(properties, F_emissionUseSurfaceNormal, defaultValue: true));
        }

        private static IReadOnlyList<uint> ReadHashContainer(BinTreeProperty property)
        {
            if (property is not BinTreeContainer container || container.Elements.Count == 0)
                return Array.Empty<uint>();
            return container.Elements
                .Select(AsU32)
                .Where(static value => value is > 0)
                .Select(static value => value.Value)
                .ToArray();
        }

        private static VfxFieldCollectionDefinition ReadFields(
            IReadOnlyDictionary<uint, BinTreeProperty> emitterProperties)
        {
            if (Get(emitterProperties, F_fieldCollection) is not BinTreeStruct fieldData) return null;
            var acceleration = ReadStructContainer(fieldData.Properties, F_fieldAccelerationDefinitions)
                .Select(value => new VfxAccelerationField(
                    ReadCurve3(value.Properties, F_accel) ?? VfxCurve3.Const(Vector3.Zero),
                    GetBool(value.Properties, F_isLocalSpace, defaultValue: true))).ToArray();
            var attraction = ReadStructContainer(fieldData.Properties, F_fieldAttractionDefinitions)
                .Select(value => new VfxAttractionField(
                    ReadCurveF(value.Properties, F_accel) ?? VfxCurveF.Zero,
                    ReadCurve3(value.Properties, F_position) ?? VfxCurve3.Const(Vector3.Zero),
                    ReadCurveF(value.Properties, F_radius) ?? VfxCurveF.Zero)).ToArray();
            var drag = ReadStructContainer(fieldData.Properties, F_fieldDragDefinitions)
                .Select(value => new VfxDragField(
                    ReadCurveF(value.Properties, F_strength) ?? VfxCurveF.Zero,
                    ReadCurve3(value.Properties, F_position) ?? VfxCurve3.Const(Vector3.Zero),
                    ReadCurveF(value.Properties, F_radius) ?? VfxCurveF.Zero)).ToArray();
            var orbital = ReadStructContainer(fieldData.Properties, F_fieldOrbitalDefinitions)
                .Select(value => new VfxOrbitalField(
                    ReadCurve3(value.Properties, F_directionField, Vector3.UnitY) ?? VfxCurve3.Const(Vector3.UnitY),
                    GetBool(value.Properties, F_isLocalSpace, defaultValue: true))).ToArray();
            var noise = ReadStructContainer(fieldData.Properties, F_fieldNoiseDefinitions)
                .Select(value => new VfxNoiseField(
                    ReadCurveF(value.Properties, F_frequency) ?? VfxCurveF.Zero,
                    ReadCurveF(value.Properties, F_velocityDelta) ?? VfxCurveF.Zero,
                    ReadCurve3(value.Properties, F_position) ?? VfxCurve3.Const(Vector3.Zero),
                    ReadCurveF(value.Properties, F_radius) ?? VfxCurveF.Zero,
                    AsVec3(Get(value.Properties, F_axisFraction)) ?? Vector3.Zero)).ToArray();
            if (acceleration.Length == 0 && attraction.Length == 0 && drag.Length == 0 && orbital.Length == 0 && noise.Length == 0)
                return null;
            return new VfxFieldCollectionDefinition(acceleration, attraction, drag, orbital, noise);
        }

        private static IEnumerable<BinTreeStruct> ReadStructContainer(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            uint fieldHash)
            => Get(properties, fieldHash) is BinTreeContainer container
                ? container.Elements.OfType<BinTreeStruct>()
                : Enumerable.Empty<BinTreeStruct>();

        private static VfxChildParticleSetDefinition ReadChildParticleSet(
            IReadOnlyDictionary<uint, BinTreeProperty> emitterProperties)
        {
            if (Get(emitterProperties, F_childParticleSet) is not BinTreeStruct childData) return null;
            var children = new List<VfxChildSystemReference>();
            if (Get(childData.Properties, F_childrenIdentifiers) is BinTreeContainer identifiers)
            {
                foreach (BinTreeProperty item in identifiers.Elements)
                {
                    if (item is not BinTreeStruct identifier)
                    {
                        // LTK preserves unresolved child slots so childrenProbability keeps its authored indices.
                        children.Add(null);
                        continue;
                    }

                    string name = GetString(identifier.Properties, F_effectName) ?? string.Empty;
                    uint systemHash = AsU32(Get(identifier.Properties, F_effect)) ?? 0u;
                    uint effectKey = AsU32(Get(identifier.Properties, F_effectKey)) ?? 0u;
                    children.Add(!string.IsNullOrEmpty(name) || systemHash != 0 || effectKey != 0
                        ? new VfxChildSystemReference(name, systemHash, effectKey)
                        : null);
                }
            }

            // LTK keeps boneToSpawnAt positional and parallel to childrenIdentifiers.
            // Preserve empty string slots: dropping one would shift every child after it.
            IReadOnlyList<string> bones = Get(childData.Properties, F_boneToSpawnAt) is BinTreeContainer boneList
                ? boneList.Elements.OfType<BinTreeString>()
                    .Select(static value => value.Value)
                    .ToArray()
                : Array.Empty<string>();

            VfxCurve3 relativeOffset = VfxCurve3.Const(Vector3.Zero);
            int inheritanceMode = 0;
            if (Get(childData.Properties, F_parentInheritance) is BinTreeStruct inheritance)
            {
                relativeOffset = ReadCurve3(inheritance.Properties, F_relativeOffset) ?? relativeOffset;
                inheritanceMode = GetU8(inheritance.Properties, F_inheritanceMode) ?? 0;
            }

            return new VfxChildParticleSetDefinition(
                children,
                GetBool(childData.Properties, F_childEmitOnDeath),
                // childrenProbability is a zero-based child index, not a weight. Its schema
                // default is zero, so an omitted curve must select the first child.
                ReadCurveF(childData.Properties, F_childrenProbability) ?? VfxCurveF.Zero,
                relativeOffset,
                inheritanceMode,
                bones);
        }

        private static VfxPrimitiveKind GetPrimitiveKind(uint classHash) => classHash switch
        {
            var value when value == PrimCameraQuad => VfxPrimitiveKind.CameraQuad,
            var value when value == PrimCameraUnitQuad => VfxPrimitiveKind.CameraUnitQuad,
            var value when value == PrimArbitraryQuad => VfxPrimitiveKind.ArbitraryQuad,
            var value when value == PrimMesh => VfxPrimitiveKind.Mesh,
            var value when value == PrimAttachedMesh => VfxPrimitiveKind.AttachedMesh,
            var value when value == PrimCameraTrail => VfxPrimitiveKind.CameraTrail,
            var value when value == PrimArbitraryTrail => VfxPrimitiveKind.ArbitraryTrail,
            var value when value == PrimRay => VfxPrimitiveKind.Ray,
            var value when value == PrimBeam => VfxPrimitiveKind.Beam,
            var value when value == PrimCameraSegmentBeam => VfxPrimitiveKind.CameraSegmentBeam,
            var value when value == PrimPlanarProjection => VfxPrimitiveKind.PlanarProjection,
            _ => VfxPrimitiveKind.Unsupported
        };

        private static VfxSpawnShape ReadSpawnShape(IReadOnlyDictionary<uint, BinTreeProperty> emitterProps)
        {
            if ((Get(emitterProps, F_spawnShape) ?? Get(emitterProps, F_shape)) is not BinTreeStruct shape) return null;

            VfxCurve3 offset = shape.ClassHash switch
            {
                var value when value == ShapeLegacy || value == ShapeOld =>
                    ReadCurve3Property(Get(shape.Properties, F_emitOffset)) ?? VfxCurve3.Const(Vector3.Zero),
                var value when value == ShapePoint =>
                    VfxCurve3.Const(AsVec3(Get(shape.Properties, F_emitOffset)) ?? Vector3.Zero),
                _ => VfxCurve3.Const(Vector3.Zero)
            };
            var axes = ReadVector3Container(Get(shape.Properties, F_emitRotAxes));
            var angles = ReadCurveFContainer(Get(shape.Properties, F_emitRotAngles));
            VfxSpawnShapeKind kind = shape.ClassHash switch
            {
                var value when value == ShapeBox => VfxSpawnShapeKind.Box,
                var value when value == ShapeSphere => VfxSpawnShapeKind.Sphere,
                var value when value == ShapeCylinder => VfxSpawnShapeKind.Cylinder,
                var value when value == ShapeLegacy || value == ShapeOld => VfxSpawnShapeKind.Legacy,
                _ => VfxSpawnShapeKind.Point
            };
            return new VfxSpawnShape(
                kind,
                offset,
                axes,
                angles,
                AsVec3(Get(shape.Properties, F_shapeSize)) ?? Vector3.Zero,
                GetF32(shape.Properties, F_shapeRadius) ?? 0f,
                GetF32(shape.Properties, F_shapeHeight) ?? 0f,
                (byte)(GetU8(shape.Properties, F_shapeFlags) ?? 0),
                ReadCurve3Property(Get(shape.Properties, F_birthTranslation)));
        }

        private static VfxCurve3 ScalarSizeCurve(VfxCurveF curve) => new(
            new Vector3(curve.Constant, curve.Constant, 0f), curve.Times,
            curve.Values?.Select(static v => new Vector3(v, v, 0f)).ToArray());

        private static VfxCurve3 ScalarScaleCurve(VfxCurveF curve) => new(
            new Vector3(curve.Constant, curve.Constant, curve.Constant), curve.Times,
            curve.Values?.Select(static v => new Vector3(v, v, v)).ToArray());

        private static VfxCurve3 ScalarRotationCurve(VfxCurveF curve)
        {
            VfxProbTable[] probability = null;
            if (curve.Prob is { Length: > 0 } && !curve.Prob[0].IsEmpty)
            {
                probability = new VfxProbTable[3];
                probability[2] = curve.Prob[0];
            }

            return new VfxCurve3(
                new Vector3(0f, 0f, curve.Constant),
                curve.Times,
                curve.Values?.Select(static v => new Vector3(0f, 0f, v)).ToArray(),
                probability);
        }

        private static IReadOnlyList<Vector3> ReadVector3Container(BinTreeProperty prop)
        {
            if (prop is not BinTreeContainer c || c.Elements.Count == 0) return Array.Empty<Vector3>();
            var values = new List<Vector3>(c.Elements.Count);
            foreach (var el in c.Elements)
                if (AsVec3(el) is { } value) values.Add(value);
            return values;
        }

        private static IReadOnlyList<VfxCurveF> ReadCurveFContainer(BinTreeProperty prop)
        {
            if (prop is not BinTreeContainer c || c.Elements.Count == 0) return Array.Empty<VfxCurveF>();
            var values = new List<VfxCurveF>(c.Elements.Count);
            foreach (var el in c.Elements)
            {
                // LTK's curves() preserves one slot per container element and substitutes
                // DEFAULT.zero when an item cannot be read as a ValueFloat.
                values.Add(ReadCurveFProperty(el) ?? VfxCurveF.Zero);
            }
            return values;
        }

        private static readonly string[] ShimmerMeshExtensions = { ".gmesh", ".tmesh", ".scb" };
        private static readonly string[] ShimmerTextureExtensions = { ".dds", ".tex" };

        private static readonly uint[] ShimmerColorFieldHashes =
        {
            VfxParsingHash.Fnv1a("Color"),
            VfxParsingHash.Fnv1a("InitialColor"),
            VfxParsingHash.Fnv1a("color"),
            VfxParsingHash.Fnv1a("initialColor")
        };

        private static readonly uint[] ShimmerScaleFieldHashes =
        {
            VfxParsingHash.Fnv1a("Scale"),
            VfxParsingHash.Fnv1a("InitialScale"),
            VfxParsingHash.Fnv1a("scale"),
            VfxParsingHash.Fnv1a("initialScale")
        };

        private static readonly uint[] ShimmerPositionFieldHashes =
        {
            VfxParsingHash.Fnv1a("Position"),
            VfxParsingHash.Fnv1a("InitialPosition"),
            VfxParsingHash.Fnv1a("Offset"),
            VfxParsingHash.Fnv1a("Translation"),
            VfxParsingHash.Fnv1a("ShapeCenter"),
            VfxParsingHash.Fnv1a("position"),
            VfxParsingHash.Fnv1a("offset")
        };

        private static readonly uint[] ShimmerRotationFieldHashes =
        {
            VfxParsingHash.Fnv1a("Rotation"),
            VfxParsingHash.Fnv1a("InitialRotation"),
            VfxParsingHash.Fnv1a("rotation"),
            VfxParsingHash.Fnv1a("initialRotation")
        };
    }
}
