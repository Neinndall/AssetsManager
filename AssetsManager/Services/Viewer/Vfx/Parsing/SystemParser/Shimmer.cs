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
        private static VfxEmitterDefinition ParseShimmerEmitter(BinTreeStruct s, int index)
        {
            var p = s.Properties;
            string name = GetString(p, F_emitterName) ?? $"[{index}]";
            bool disabled = GetBool(p, F_disabled);

            var components = p.GetValueOrDefault(VfxParsingHash.Fnv1a("VfxComponents")) as BinTreeStruct;
            BinTreeProperty geometry = components?.Properties.GetValueOrDefault(VfxParsingHash.Fnv1a("GeometryComponent"));
            BinTreeProperty render = components?.Properties.GetValueOrDefault(VfxParsingHash.Fnv1a("RenderComponent"));
            BinTreeProperty physics = components?.Properties.GetValueOrDefault(VfxParsingHash.Fnv1a("PhysicsComponent"));
            string meshPath = FindFirstAsset(components is null ? s : geometry, ShimmerMeshExtensions);
            if (string.IsNullOrWhiteSpace(meshPath))
                return null;

            string texturePath = components is null ? FindFirstAsset(s, ShimmerTextureExtensions)
                : FindFirstAsset(geometry, ShimmerTextureExtensions) ?? FindFirstAsset(render, ShimmerTextureExtensions);

            float[] DriverValue(uint[] hashes, int width)
            {
                if (components is null) return FindNamedDriver(s, hashes, width, out _);
                float[] value = FindNamedDriver(render, hashes, width, out bool matched);
                return matched ? value : FindNamedDriver(physics, hashes, width, out _);
            }

            float[] colorValue = DriverValue(ShimmerColorFieldHashes, 4);
            Vector4 color = colorValue is null ? Vector4.One : new Vector4(colorValue[0], colorValue[1], colorValue[2], colorValue[3]);
            float[] scaleValue = DriverValue(ShimmerScaleFieldHashes, 3);
            Vector3 scale = scaleValue is null ? Vector3.One : new Vector3(scaleValue[0], scaleValue[1], scaleValue[2]);
            float[] offsetValue = DriverValue(ShimmerPositionFieldHashes, 3);
            Vector3 offset = offsetValue is null ? Vector3.Zero : new Vector3(offsetValue[0], offsetValue[1], offsetValue[2]);
            float[] rotationValue = DriverValue(ShimmerRotationFieldHashes, 3);
            Vector3 rotation = rotationValue is null ? Vector3.Zero : new Vector3(rotationValue[0], rotationValue[1], rotationValue[2]);
            Vector3 rotationRad = rotation * (MathF.PI / 180f);

            return new VfxEmitterDefinition(
                Name: name,
                Rate: VfxCurveF.Const(1f),
                ParticleLifetime: VfxCurveF.Const(1000f),
                EmitterLifetime: null,
                ParticleLinger: 0f,
                TimeBeforeFirstEmission: 0f,
                IsSingleParticle: true,
                Disabled: disabled,
                BlendMode: 0,
                BirthScale: VfxCurve3.Const(scale),
                ScaleOverLife: null,
                BirthColor: VfxCurve4.Const(color),
                ColorOverLife: null,
                BirthVelocity: null,
                Acceleration: null,
                BirthRotationalVelocity: null,
                EmitterPosition: VfxCurve3.Const(offset),
                TexturePath: texturePath ?? string.Empty,
                TexDiv: Vector2.One,
                NumFrames: 1,
                RandomStartFrame: false,
                IsMeshPrimitive: true,
                MeshPath: meshPath,
                PrimitiveKind: VfxPrimitiveKind.Mesh,
                BirthRotation: VfxCurve3.Const(rotationRad));
        }

        private static string FindFirstAsset(BinTreeProperty prop, string[] extensions, int depth = 0)
        {
            if (prop is null || depth > 24) return null;
            if (prop is BinTreeOptional opt) prop = opt.Value;
            if (prop is null) return null;

            if (prop is BinTreeString str && !string.IsNullOrWhiteSpace(str.Value))
            {
                string lower = str.Value.ToLowerInvariant();
                foreach (var ext in extensions)
                {
                    if (lower.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                        return str.Value;
                }
            }
            else if (prop is BinTreeWadChunkLink link && link.Value != 0)
            {
                return $"{link.Value:x16}{extensions[0]}";
            }

            if (prop is BinTreeStruct s)
            {
                foreach (var child in s.Properties.Values)
                {
                    var found = FindFirstAsset(child, extensions, depth + 1);
                    if (found != null) return found;
                }
            }
            else if (prop is BinTreeContainer c)
            {
                foreach (var child in c.Elements)
                {
                    var found = FindFirstAsset(child, extensions, depth + 1);
                    if (found != null) return found;
                }
            }
            else if (prop is BinTreeMap m)
            {
                foreach (var pair in m)
                {
                    var found = FindFirstAsset(pair.Value, extensions, depth + 1);
                    if (found != null) return found;
                }
            }

            return null;
        }

        private static float[] FindNamedDriver(BinTreeProperty root, uint[] targetHashes, int width, out bool matched, int depth = 0)
        {
            matched = false;
            if (root is null || depth > 24) return null;
            if (root is BinTreeOptional optional) root = optional.Value;
            if (root is BinTreeStruct node)
            {
                foreach (var field in node.Properties)
                {
                    BinTreeProperty held = field.Value is BinTreeOptional opt ? opt.Value : field.Value;
                    bool direct = width == 3 ? held is BinTreeVector3 : held is BinTreeVector4 or BinTreeColor;
                    if (targetHashes.Contains(field.Key) && (direct || VfxShimmerDriverEvaluator.IsDriver(held, width)))
                    {
                        matched = true;
                        return VfxShimmerDriverEvaluator.Evaluate(held, width);
                    }
                    // Graph inputs are not independent component properties.
                    if (VfxShimmerDriverEvaluator.IsGraphRoot(held)) continue;
                    float[] value = FindNamedDriver(held, targetHashes, width, out matched, depth + 1);
                    if (matched) return value;
                }
            }
            else if (root is BinTreeContainer container)
            {
                foreach (BinTreeProperty held in container.Elements)
                {
                    float[] value = FindNamedDriver(held, targetHashes, width, out matched, depth + 1);
                    if (matched) return value;
                }
            }
            return null;
        }
    }
}
