using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;

namespace AssetsManager.Services.Viewer.Vfx
{
    /// <summary>
    /// Parses VfxSystemDefinitionData objects out of a companion .bin and exposes
    /// them keyed by object path-hash.
    /// </summary>
    public static class VfxSystemResolver
    {
        private static class HashAlgorithms
        {
            public static uint Fnv1a(string text)
            {
                if (text == null) return 0;
                uint hash = 2166136261;
                foreach (char c in text.ToLowerInvariant())
                {
                    hash ^= c;
                    hash *= 16777619;
                }
                return hash;
            }
        }

        // class hashes
        private static readonly uint SystemClass  = HashAlgorithms.Fnv1a("VfxSystemDefinitionData");
        private static readonly uint EmitterClass = HashAlgorithms.Fnv1a("VfxEmitterDefinitionData");

        // system fields
        private static readonly uint F_particleName = HashAlgorithms.Fnv1a("particleName");
        private static readonly uint F_particlePath = HashAlgorithms.Fnv1a("particlePath");
        private static readonly uint F_soundPersistent = HashAlgorithms.Fnv1a("soundPersistentDefault");
        private static readonly uint F_soundOnCreate = HashAlgorithms.Fnv1a("soundOnCreateDefault");
        private static readonly uint F_visibilityRadius = HashAlgorithms.Fnv1a("visibilityRadius");

        // emitter fields
        private static readonly uint F_emitterName   = HashAlgorithms.Fnv1a("emitterName");
        private static readonly uint F_rate          = HashAlgorithms.Fnv1a("rate");
        private static readonly uint F_particleLife  = HashAlgorithms.Fnv1a("particleLifetime");
        private static readonly uint F_lifetime      = HashAlgorithms.Fnv1a("lifetime");
        private static readonly uint F_particleLinger= HashAlgorithms.Fnv1a("particleLinger");
        private static readonly uint F_timeBefore    = HashAlgorithms.Fnv1a("timeBeforeFirstEmission");
        private static readonly uint F_isSingle      = HashAlgorithms.Fnv1a("isSingleParticle");
        private static readonly uint F_disabled      = HashAlgorithms.Fnv1a("disabled");
        private static readonly uint F_blendMode     = HashAlgorithms.Fnv1a("blendMode");
        private static readonly uint F_birthScale0   = HashAlgorithms.Fnv1a("birthScale0");
        private static readonly uint F_scale0        = HashAlgorithms.Fnv1a("scale0");
        private static readonly uint F_birthColor    = HashAlgorithms.Fnv1a("birthColor");
        private static readonly uint F_color         = HashAlgorithms.Fnv1a("color");
        private static readonly uint F_particleColorTex = HashAlgorithms.Fnv1a("particleColorTexture");
        private static readonly uint F_colorLookUpX  = HashAlgorithms.Fnv1a("colorLookUpTypeX");
        private static readonly uint F_colorLookUpY  = HashAlgorithms.Fnv1a("colorLookUpTypeY");
        private static readonly uint F_birthVelocity = HashAlgorithms.Fnv1a("birthVelocity");
        private static readonly uint F_birthAccel    = HashAlgorithms.Fnv1a("birthAcceleration");
        private static readonly uint F_accel         = HashAlgorithms.Fnv1a("acceleration");
        private static readonly uint F_birthOrbital  = HashAlgorithms.Fnv1a("birthOrbitalVelocity");
        private static readonly uint F_worldAccel    = HashAlgorithms.Fnv1a("worldAcceleration");
        private static readonly uint F_birthDrag     = HashAlgorithms.Fnv1a("birthDrag");
        private static readonly uint F_drag          = HashAlgorithms.Fnv1a("drag");
        private static readonly uint F_birthRotation = HashAlgorithms.Fnv1a("birthRotation0");
        private static readonly uint F_birthRotVel0  = HashAlgorithms.Fnv1a("birthRotationalVelocity0");
        private static readonly uint F_emitterPos    = HashAlgorithms.Fnv1a("emitterPosition");
        private const uint F_spawnShape              = 0x3bf0b4ed; // SpawnShape
        private static readonly uint F_emitOffset    = HashAlgorithms.Fnv1a("emitOffset");
        private static readonly uint F_emitRotAxes   = HashAlgorithms.Fnv1a("emitRotationAxes");
        private static readonly uint F_emitRotAngles = HashAlgorithms.Fnv1a("emitRotationAngles");
        private static readonly uint F_direction     = HashAlgorithms.Fnv1a("isDirectionOriented");
        private static readonly uint F_texture       = HashAlgorithms.Fnv1a("texture");
        private static readonly uint F_textureMult   = HashAlgorithms.Fnv1a("textureMult");
        private static readonly uint F_texDiv        = HashAlgorithms.Fnv1a("texDiv");
        private static readonly uint F_texDivMult    = HashAlgorithms.Fnv1a("texDivMult");
        private static readonly uint F_numFrames     = HashAlgorithms.Fnv1a("numFrames");
        private static readonly uint F_randomStart   = HashAlgorithms.Fnv1a("isRandomStartFrame");
        private static readonly uint F_birthFrameRate= HashAlgorithms.Fnv1a("birthFrameRate");
        private static readonly uint F_frameRate     = HashAlgorithms.Fnv1a("frameRate");
        private static readonly uint F_birthUvScrollMult = HashAlgorithms.Fnv1a("birthUvScrollRateMult");
        private static readonly uint F_primitive     = HashAlgorithms.Fnv1a("primitive");
        private static readonly uint F_startFrame    = HashAlgorithms.Fnv1a("startFrame");
        private static readonly uint F_legacySimple  = HashAlgorithms.Fnv1a("LegacySimple");
        private static readonly uint F_legacyBirthScale = HashAlgorithms.Fnv1a("birthScale");
        private static readonly uint F_legacyScale = HashAlgorithms.Fnv1a("scale");
        private static readonly uint F_legacyBirthRotation = HashAlgorithms.Fnv1a("birthRotation");
        private static readonly uint F_legacyBirthRotVel = HashAlgorithms.Fnv1a("birthRotationalVelocity");
        private static readonly uint F_shape = HashAlgorithms.Fnv1a("shape");
        private static readonly uint F_distortionDefinition = HashAlgorithms.Fnv1a("distortionDefinition");
        private static readonly uint F_distortion = HashAlgorithms.Fnv1a("distortion");
        private static readonly uint F_distortionMode = HashAlgorithms.Fnv1a("distortionMode");
        private static readonly uint F_normalMapTexture = HashAlgorithms.Fnv1a("normalMapTexture");

        // Value* / dynamics inner fields
        private static readonly uint F_constantValue = HashAlgorithms.Fnv1a("constantValue");
        private static readonly uint F_dynamics      = HashAlgorithms.Fnv1a("dynamics");
        private static readonly uint F_times         = HashAlgorithms.Fnv1a("times");
        private static readonly uint F_values        = HashAlgorithms.Fnv1a("values");
        private static readonly uint F_probTables    = HashAlgorithms.Fnv1a("probabilityTables");
        private static readonly uint F_keyTimes      = HashAlgorithms.Fnv1a("keyTimes");
        private static readonly uint F_keyValues     = HashAlgorithms.Fnv1a("keyValues");
        private static readonly uint F_meshDef       = 0x0d89732d; // VfxPrimitiveMesh's VfxMeshDefinitionData field
        private static readonly uint F_simpleMesh    = HashAlgorithms.Fnv1a("mSimpleMeshName");
        private static readonly uint F_meshName      = HashAlgorithms.Fnv1a("mMeshName");
        private static readonly uint F_birthUvScroll = HashAlgorithms.Fnv1a("birthUvScrollRate");
        private static readonly uint F_meshSkeleton  = 0x90595a15; // VfxMeshDefinitionData skeleton field
        private static readonly uint F_meshAnim      = HashAlgorithms.Fnv1a("mAnimationName");

        // primitive class hashes we treat as "mesh"
        private static readonly uint PrimMesh = HashAlgorithms.Fnv1a("VfxPrimitiveMesh");
        private static readonly uint PrimArbitraryQuad = HashAlgorithms.Fnv1a("VfxPrimitiveArbitraryQuad");

        private static readonly uint ResolverClass = HashAlgorithms.Fnv1a("ResourceResolver");
        private static readonly uint F_resourceMap = HashAlgorithms.Fnv1a("resourceMap");
        private static readonly uint F_mResourceMap = HashAlgorithms.Fnv1a("mResourceMap");

        /// <summary>Extract effect-key hash to VfxSystemDefinitionData object hash mapping.</summary>
        public static IReadOnlyDictionary<uint, uint> ExtractResourceMap(byte[] bin)
        {
            var map = new Dictionary<uint, uint>();
            BinTree tree;
            try { tree = SafeBinTree.Parse(bin); }
            catch { return map; }
            foreach (var o in tree.Objects.Values)
            {
                if (o.ClassHash != ResolverClass) continue;
                if (!o.Properties.TryGetValue(F_resourceMap, out var prop)
                    && !o.Properties.TryGetValue(F_mResourceMap, out prop)) continue;
                if (prop is not System.Collections.IEnumerable entries || prop is BinTreeString) continue;
                foreach (var kv in entries)
                {
                    var kvType = kv.GetType();
                    var key = kvType.GetProperty("Key")?.GetValue(kv);
                    var val = kvType.GetProperty("Value")?.GetValue(kv);
                    uint kh = key switch { BinTreeHash h => h.Value, BinTreeU32 u => u.Value, _ => 0u };
                    uint vh = val switch { BinTreeObjectLink ol => ol.Value, BinTreeHash h => h.Value, BinTreeU32 u => u.Value, _ => 0u };
                    if (kh != 0 && vh != 0) map[kh] = vh;
                }
            }
            return map;
        }

        /// <summary>Extract linked-bin dependencies.</summary>
        public static IReadOnlyList<string> ExtractDependencies(byte[] bin)
        {
            try { return SafeBinTree.Parse(bin).Dependencies; }
            catch { return Array.Empty<string>(); }
        }

        /// <summary>Parse every VfxSystemDefinitionData in the bin, keyed by object path-hash.</summary>
        public static IReadOnlyDictionary<uint, VfxSystemDefinition> ExtractAll(byte[] materialsBin)
        {
            var map = new Dictionary<uint, VfxSystemDefinition>();
            BinTree bin;
            try { bin = SafeBinTree.Parse(materialsBin); }
            catch { return map; }

            foreach (var o in bin.Objects.Values)
            {
                if (o.ClassHash != SystemClass) continue;
                try
                {
                    var sys = ParseSystem(o);
                    if (sys is not null) map[o.PathHash] = sys;
                }
                catch { /* skip malformed system */ }
            }
            return map;
        }

        private static VfxSystemDefinition ParseSystem(BinTreeObject o)
        {
            string name = GetString(o.Properties, F_particleName) ?? $"0x{o.PathHash:x8}";
            string path = GetString(o.Properties, F_particlePath) ?? "";

            var emitters = new List<VfxEmitterDefinition>();
            foreach (var (_, prop) in o.Properties)
            {
                if (prop is not BinTreeContainer c) continue;
                foreach (var el in c.Elements)
                    if (el is BinTreeStruct s && s.ClassHash == EmitterClass)
                        emitters.Add(ParseEmitter(s));
            }
            string persistentSound = GetString(o.Properties, F_soundPersistent);
            string onCreateSound = GetString(o.Properties, F_soundOnCreate);
            float radius = GetF32(o.Properties, F_visibilityRadius) ?? 0f;
            return new VfxSystemDefinition(o.PathHash, name, path, emitters, persistentSound, onCreateSound, radius);
        }

        private static VfxEmitterDefinition ParseEmitter(BinTreeStruct s)
        {
            var p = s.Properties;

            var legacy = Get(p, F_legacySimple) as BinTreeStruct;
            var legacyBirthScale = legacy is null ? null : ReadCurveF(legacy.Properties, F_legacyBirthScale);
            var birthScale = ReadCurve3(p, F_birthScale0)
                ?? (legacyBirthScale is { } lbs ? ScalarSizeCurve(lbs) : VfxCurve3.Const(Vector3.One));
            var scaleOverLife = ReadCurve3(p, F_scale0);
            if (scaleOverLife is null && legacy is not null && ReadCurveF(legacy.Properties, F_legacyScale) is { } legacyScale)
                scaleOverLife = ScalarScaleCurve(legacyScale);
            var birthRotation = ReadCurve3(p, F_birthRotation);
            if (birthRotation is null && legacy is not null && ReadCurveF(legacy.Properties, F_legacyBirthRotation) is { } legacyRotation)
                birthRotation = ScalarRotationCurve(legacyRotation);
            var birthRotationalVelocity = ReadCurve3(p, F_birthRotVel0);
            if (birthRotationalVelocity is null && legacy is not null && ReadCurveF(legacy.Properties, F_legacyBirthRotVel) is { } legacyRotVel)
                birthRotationalVelocity = ScalarRotationCurve(legacyRotVel);
            var birthColor = ReadCurve4(p, F_birthColor) ?? VfxCurve4.Const(Vector4.One);

            bool isMesh = p.TryGetValue(F_primitive, out var prim) && prim is BinTreeStruct ps && ps.ClassHash == PrimMesh;
            bool isArbitraryQuad = prim is BinTreeStruct aq && aq.ClassHash == PrimArbitraryQuad;
            string meshPath = null, meshSkl = null, meshAnm = null;
            if (isMesh && prim is BinTreeStruct ps2 && Get(ps2.Properties, F_meshDef) is BinTreeStruct md)
            {
                meshPath = GetString(md.Properties, F_simpleMesh) ?? GetString(md.Properties, F_meshName);
                meshSkl = GetString(md.Properties, F_meshSkeleton);
                meshAnm = GetString(md.Properties, F_meshAnim);
            }

            string textureMultPath = null;
            Vector2 textureMultTexDiv = Vector2.One, textureMultUvScroll = Vector2.Zero;
            if (Get(p, F_textureMult) is BinTreeStruct textureMult)
            {
                textureMultPath = GetString(textureMult.Properties, F_textureMult);
                textureMultTexDiv = ReadValueVec2(Get(textureMult.Properties, F_texDivMult)) ?? Vector2.One;
                textureMultUvScroll = ReadValueVec2(Get(textureMult.Properties, F_birthUvScrollMult)) ?? Vector2.Zero;
            }

            VfxDistortionDefinition distortion = null;
            if (Get(p, F_distortionDefinition) is BinTreeStruct distortionData)
            {
                var dp = distortionData.Properties;
                distortion = new VfxDistortionDefinition(
                    GetF32(dp, F_distortion) ?? 0f,
                    GetU8(dp, F_distortionMode) ?? 0,
                    GetString(dp, F_normalMapTexture));
            }

            return new VfxEmitterDefinition(
                Name: GetString(p, F_emitterName) ?? "(emitter)",
                Rate: ReadCurveF(p, F_rate) ?? VfxCurveF.Const(10f),
                ParticleLifetime: ReadCurveF(p, F_particleLife) ?? VfxCurveF.Const(1f),
                EmitterLifetime: GetOptionalF32(p, F_lifetime),
                ParticleLinger: GetOptionalF32(p, F_particleLinger) ?? 0f,
                TimeBeforeFirstEmission: GetF32(p, F_timeBefore) ?? 0f,
                IsSingleParticle: GetBool(p, F_isSingle),
                Disabled: GetBool(p, F_disabled),
                BlendMode: GetU8(p, F_blendMode) ?? 1,
                BirthScale: birthScale,
                ScaleOverLife: scaleOverLife,
                BirthColor: birthColor,
                ColorOverLife: ReadCurve4(p, F_color),
                BirthVelocity: ReadCurve3(p, F_birthVelocity),
                Acceleration: ReadCurve3(p, F_worldAccel),
                BirthRotationalVelocity: birthRotationalVelocity,
                EmitterPosition: (ReadCurve3(p, F_emitterPos) ?? VfxCurve3.Const(Vector3.Zero)).Constant,
                TexturePath: GetString(p, F_texture),
                TexDiv: GetVec2(p, F_texDiv) ?? Vector2.One,
                NumFrames: GetU16(p, F_numFrames) ?? 1,
                RandomStartFrame: GetBool(p, F_randomStart),
                IsMeshPrimitive: isMesh,
                MeshPath: meshPath,
                UvScrollRate: ReadValueVec2(Get(p, F_birthUvScroll)) ?? Vector2.Zero,
                MeshSkeletonPath: meshSkl,
                MeshAnimationPath: meshAnm,
                SpawnShape: ReadSpawnShape(p),
                BirthAcceleration: ReadCurve3(p, F_birthAccel) ?? ReadCurve3(p, F_accel),
                BirthOrbitalVelocity: ReadCurve3(p, F_birthOrbital),
                BirthDrag: ReadCurve3(p, F_birthDrag),
                DragOverLife: ReadCurve3(p, F_drag),
                BirthRotation: birthRotation,
                IsDirectionOriented: GetBool(p, F_direction),
                IsArbitraryQuad: isArbitraryQuad,
                BirthFrameRate: ReadCurveF(p, F_birthFrameRate),
                FrameRate: GetF32(p, F_frameRate),
                TextureMultPath: textureMultPath,
                TextureMultTexDiv: textureMultTexDiv,
                TextureMultUvScrollRate: textureMultUvScroll,
                StartFrame: GetU16(p, F_startFrame) ?? 0,
                UseTextureAspect: legacy is not null,
                Distortion: distortion,
                ParticleColorTexturePath: GetString(p, F_particleColorTex),
                ColorLookUpTypeX: GetU8(p, F_colorLookUpX),
                ColorLookUpTypeY: GetU8(p, F_colorLookUpY));
        }

        private static VfxSpawnShape ReadSpawnShape(IReadOnlyDictionary<uint, BinTreeProperty> emitterProps)
        {
            if ((Get(emitterProps, F_spawnShape) ?? Get(emitterProps, F_shape)) is not BinTreeStruct shape) return null;

            var offset = ReadCurve3Property(Get(shape.Properties, F_emitOffset)) ?? VfxCurve3.Const(Vector3.Zero);
            var axes = ReadVector3Container(Get(shape.Properties, F_emitRotAxes));
            var angles = ReadCurveFContainer(Get(shape.Properties, F_emitRotAngles));
            return new VfxSpawnShape(offset, axes, angles);
        }

        private static VfxCurve3 ScalarSizeCurve(VfxCurveF curve) => new(
            new Vector3(curve.Constant, curve.Constant, 0f), curve.Times,
            curve.Values?.Select(static v => new Vector3(v, v, 0f)).ToArray());

        private static VfxCurve3 ScalarScaleCurve(VfxCurveF curve) => new(
            new Vector3(curve.Constant, curve.Constant, 1f), curve.Times,
            curve.Values?.Select(static v => new Vector3(v, v, 1f)).ToArray());

        private static VfxCurve3 ScalarRotationCurve(VfxCurveF curve) => new(
            new Vector3(curve.Constant, 0f, 0f), curve.Times,
            curve.Values?.Select(static v => new Vector3(v, 0f, 0f)).ToArray());

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
                if (ReadCurveFProperty(el) is { } value) values.Add(value);
            return values;
        }

        private static VfxCurveF? ReadCurveF(IReadOnlyDictionary<uint, BinTreeProperty> p, uint field)
        {
            return p.TryGetValue(field, out var prop) ? ReadCurveFProperty(prop) : null;
        }

        private static VfxCurveF? ReadCurveFProperty(BinTreeProperty prop)
        {
            if (prop is BinTreeStruct v)
            {
                float c = AsF32(Get(v.Properties, F_constantValue)) ?? 0f;
                var (times, vals) = ReadDynamics(v.Properties, AsF32);
                return new VfxCurveF(c, times, vals, ReadNestedProbTables(v.Properties));
            }
            return AsF32(prop) is { } scalar ? VfxCurveF.Const(scalar) : null;
        }

        private static VfxCurve3? ReadCurve3(IReadOnlyDictionary<uint, BinTreeProperty> p, uint field)
        {
            return p.TryGetValue(field, out var prop) ? ReadCurve3Property(prop) : null;
        }

        private static VfxCurve3? ReadCurve3Property(BinTreeProperty prop)
        {
            if (prop is BinTreeStruct v)
            {
                var c = AsVec3(Get(v.Properties, F_constantValue)) ?? Vector3.Zero;
                var (times, vals) = ReadDynamics(v.Properties, AsVec3);
                return new VfxCurve3(c, times, vals, ReadNestedProbTables(v.Properties));
            }
            return AsVec3(prop) is { } vector ? VfxCurve3.Const(vector) : null;
        }

        private static VfxCurve4? ReadCurve4(IReadOnlyDictionary<uint, BinTreeProperty> p, uint field)
        {
            if (p.TryGetValue(field, out var prop) && prop is BinTreeStruct v)
            {
                var c = AsVec4(Get(v.Properties, F_constantValue)) ?? Vector4.One;
                var (times, vals) = ReadDynamics(v.Properties, AsVec4);
                return new VfxCurve4(c, times, vals, ReadNestedProbTables(v.Properties));
            }
            return null;
        }

        private static VfxProbTable[] ReadProbTables(IReadOnlyDictionary<uint, BinTreeProperty> valueProps)
        {
            if (Get(valueProps, F_probTables) is not BinTreeContainer pc || pc.Elements.Count == 0) return null;
            var tables = new VfxProbTable[pc.Elements.Count];
            bool any = false;
            for (int tableIndex = 0; tableIndex < pc.Elements.Count; tableIndex++)
            {
                var el = pc.Elements[tableIndex];
                if (el is not BinTreeStruct s) continue;
                if (Get(s.Properties, F_keyTimes) is not BinTreeContainer tc ||
                    Get(s.Properties, F_keyValues) is not BinTreeContainer vc) continue;
                int n = Math.Min(tc.Elements.Count, vc.Elements.Count);
                if (n == 0) continue;
                var times = new float[n]; var vals = new float[n];
                for (int i = 0; i < n; i++)
                {
                    times[i] = AsF32(tc.Elements[i]) ?? 0f;
                    vals[i] = AsF32(vc.Elements[i]) ?? 0f;
                }
                tables[tableIndex] = new VfxProbTable(times, vals);
                any = true;
            }
            return any ? tables : null;
        }

        private static VfxProbTable[] ReadNestedProbTables(IReadOnlyDictionary<uint, BinTreeProperty> valueProps)
        {
            if (Get(valueProps, F_dynamics) is BinTreeStruct dynamics &&
                ReadProbTables(dynamics.Properties) is { } nested)
                return nested;
            return ReadProbTables(valueProps);
        }

        private static (float[], T[]) ReadDynamics<T>(IReadOnlyDictionary<uint, BinTreeProperty> valueProps, Func<BinTreeProperty, T?> conv)
            where T : struct
        {
            if (Get(valueProps, F_dynamics) is not BinTreeStruct dyn) return (null, null);
            if (Get(dyn.Properties, F_times) is not BinTreeContainer tc) return (null, null);
            if (Get(dyn.Properties, F_values) is not BinTreeContainer vc) return (null, null);

            int n = Math.Min(tc.Elements.Count, vc.Elements.Count);
            if (n == 0) return (null, null);
            var times = new float[n];
            var vals = new T[n];
            for (int i = 0; i < n; i++)
            {
                times[i] = AsF32(tc.Elements[i]) ?? 0f;
                vals[i] = conv(vc.Elements[i]) ?? default;
            }
            return (times, vals);
        }

        private static BinTreeProperty Get(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
            => p.TryGetValue(hash, out var v) ? v : null;

        private static string GetString(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
            => Get(p, hash) is BinTreeString s ? s.Value : null;

        private static float? GetF32(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash) => AsF32(Get(p, hash));

        private static float? GetOptionalF32(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
            => Get(p, hash) is BinTreeOptional o ? AsF32(o.Value) : AsF32(Get(p, hash));

        private static int? GetU8(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
            => Get(p, hash) is BinTreeU8 u ? u.Value : null;

        private static int? GetU16(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
            => Get(p, hash) is BinTreeU16 u ? u.Value : null;

        private static Vector2? GetVec2(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
            => Get(p, hash) is BinTreeVector2 v ? v.Value : null;

        private static Vector2? ReadValueVec2(BinTreeProperty p) => p switch
        {
            BinTreeStruct value => AsVec2(Get(value.Properties, F_constantValue)),
            _ => AsVec2(p),
        };

        private static bool GetBool(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash) => Get(p, hash) switch
        {
            BinTreeBool b => b.Value,
            BinTreeBitBool bb => bb.Value,
            _ => false
        };

        private static float? AsF32(BinTreeProperty p) => p switch
        {
            BinTreeF32 f => f.Value,
            BinTreeU8 u => u.Value,
            BinTreeU16 u => u.Value,
            _ => null
        };

        private static Vector3? AsVec3(BinTreeProperty p) => p switch
        {
            BinTreeVector3 v => v.Value,
            BinTreeVector2 v => new Vector3(v.Value, 0f),
            BinTreeF32 f => new Vector3(f.Value),
            _ => null
        };

        private static Vector2? AsVec2(BinTreeProperty p) => p switch
        {
            BinTreeVector2 v => v.Value,
            BinTreeVector3 v => new Vector2(v.Value.X, v.Value.Y),
            BinTreeF32 f => new Vector2(f.Value),
            _ => null
        };

        private static Vector4? AsVec4(BinTreeProperty p) => p switch
        {
            BinTreeVector4 v => v.Value,
            BinTreeColor c => c.Value,
            BinTreeVector3 v => new Vector4(v.Value, 1f),
            _ => null
        };

        private static uint? AsU32(BinTreeProperty p) => p switch
        {
            BinTreeU32 u => u.Value,
            BinTreeHash h => h.Value,
            BinTreeObjectLink ol => ol.Value,
            _ => null
        };

        private static void MergeClip(Dictionary<string, VfxAnimationClip> map, string key, VfxAnimationClip clip)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!map.TryGetValue(key, out var existing))
            {
                map[key] = clip;
                return;
            }

            // Accumulate events from both entries so the anm->VFX link is never lost when the
            // same clip is declared in multiple bins (only one of them carries the events).
            foreach (var pe in clip.ParticleEvents)
                if (existing.ParticleEvents.All(e => e.EffectHash != pe.EffectHash || e.EffectName != pe.EffectName))
                    existing.ParticleEvents.Add(pe);
            foreach (var se in clip.SoundEvents)
                if (existing.SoundEvents.All(e => e.SoundHash != se.SoundHash || e.SoundName != se.SoundName))
                    existing.SoundEvents.Add(se);

            // Prefer the animation path from whichever entry actually resolved one.
            if (string.IsNullOrEmpty(existing.AnimationName) && !string.IsNullOrEmpty(clip.AnimationName))
                existing.AnimationName = clip.AnimationName;
        }

        public static IReadOnlyDictionary<string, VfxAnimationClip> ExtractAnimationClips(byte[] materialsBin)
        {
            var map = new Dictionary<string, VfxAnimationClip>(StringComparer.OrdinalIgnoreCase);
            BinTree bin;
            try { bin = SafeBinTree.Parse(materialsBin); }
            catch { return map; }

            uint f_clipDataMap = HashAlgorithms.Fnv1a("mClipDataMap");
            uint class_atomic = HashAlgorithms.Fnv1a("atomicClipData");
            uint f_animRes = HashAlgorithms.Fnv1a("mAnimationResourceData");
            uint f_animPath = HashAlgorithms.Fnv1a("mAnimationFilePath");
            uint f_eventMap = HashAlgorithms.Fnv1a("mEventDataMap");
            uint class_particleEvent = HashAlgorithms.Fnv1a("ParticleEventData");
            uint f_effectKey = HashAlgorithms.Fnv1a("mEffectKey");
            uint f_particleName = HashAlgorithms.Fnv1a("mParticleName");
            uint f_boneName = HashAlgorithms.Fnv1a("mBoneName");
            uint f_startFrame = HashAlgorithms.Fnv1a("mStartFrame");
            uint f_pairList = HashAlgorithms.Fnv1a("mParticleEventDataPairList");

            uint class_soundEvent = HashAlgorithms.Fnv1a("SoundEventData");
            uint f_soundName = HashAlgorithms.Fnv1a("mSoundName");

            // 1. Try parsing modern atomicClipData structure
            bool foundModern = false;
            foreach (var o in bin.Objects.Values)
            {
                if (o.Properties.TryGetValue(f_clipDataMap, out var mapProp) && mapProp is BinTreeMap clipMap)
                {
                    foundModern = true;
                    foreach (var pair in clipMap)
                    {
                        if (pair.Value is BinTreeStruct s && s.ClassHash == class_atomic)
                        {
                            string clipName = pair.Key switch
                            {
                                BinTreeString str => str.Value,
                                BinTreeHash h => h.Value.ToString("x8"),
                                BinTreeU32 u => u.Value.ToString("x8"),
                                _ => "unknown"
                            };

                            string anmPath = "";
                            if (s.Properties.TryGetValue(f_animRes, out var res) && res is BinTreeStruct rs
                                && rs.Properties.TryGetValue(f_animPath, out var ap) && ap is BinTreeString aps)
                            {
                                anmPath = aps.Value;
                            }

                            if (string.IsNullOrEmpty(anmPath)) continue;
                            string animFile = Path.GetFileName(anmPath.Replace('\\', '/'));

                            var clip = new VfxAnimationClip
                            {
                                Name = clipName,
                                AnimationName = anmPath
                            };

                            if (s.Properties.TryGetValue(f_eventMap, out var em) && em is System.Collections.IEnumerable eventsEnumerable)
                            {
                                foreach (var ekv in eventsEnumerable)
                                {
                                    var ekvType = ekv.GetType();
                                    if (ekvType.GetProperty("Value")?.GetValue(ekv) is not BinTreeStruct es) continue;

                                    if (es.ClassHash == class_particleEvent)
                                    {
                                        var (effectName, effectHash) = GetStringOrHash(es, f_effectKey);
                                        if (string.IsNullOrEmpty(effectName) && effectHash == 0)
                                        {
                                            (effectName, effectHash) = GetStringOrHash(es, f_particleName);
                                        }

                                        string boneName = "";
                                        uint boneHash = 0;
                                        var (bName, bHash) = GetStringOrHash(es, f_boneName);
                                        boneName = bName;
                                        boneHash = bHash;
                                        
                                        if (string.IsNullOrEmpty(boneName) && boneHash == 0 && es.Properties.TryGetValue(f_pairList, out var pl) && pl is BinTreeContainer pairs)
                                        {
                                            foreach (var pe in pairs.Elements)
                                            {
                                                if (pe is BinTreeStruct ps2)
                                                {
                                                    var (pBoneName, pBoneHash) = GetStringOrHash(ps2, f_boneName);
                                                    if (!string.IsNullOrEmpty(pBoneName) || pBoneHash != 0)
                                                    {
                                                        boneName = pBoneName;
                                                        boneHash = pBoneHash;
                                                        break;
                                                    }
                                                }
                                            }
                                        }

                                        float startFrame = 0f;
                                        if (es.Properties.TryGetValue(f_startFrame, out var sf) && sf is BinTreeF32 f)
                                        {
                                            startFrame = f.Value;
                                        }

                                        clip.ParticleEvents.Add(new VfxAnimationEvent
                                        {
                                            EffectName = effectName,
                                            EffectHash = effectHash,
                                            BoneName = boneName,
                                            BoneHash = boneHash,
                                            StartFrame = startFrame
                                        });
                                    }
                                    else if (es.ClassHash == class_soundEvent)
                                    {
                                        var (soundName, soundHash) = GetStringOrHash(es, f_soundName);
                                        float startFrame = 0f;
                                        if (es.Properties.TryGetValue(f_startFrame, out var sf) && sf is BinTreeF32 f)
                                        {
                                            startFrame = f.Value;
                                        }

                                        clip.SoundEvents.Add(new VfxSoundEvent
                                        {
                                            SoundName = soundName,
                                            SoundHash = soundHash,
                                            StartFrame = startFrame
                                        });
                                    }
                                }
                            }
                            
                            // Merge into existing clip entries instead of overwriting: the same
                            // anm is often declared in several bins (skins, animations, fused), and
                            // only one of them carries the mEventDataMap particle/sound events that
                            // link the animation to its VFX. Keep the richest entry (most events).
                            MergeClip(map, animFile, clip);
                            MergeClip(map, clipName, clip);
                            string nameWithoutExt = Path.GetFileNameWithoutExtension(animFile);
                            MergeClip(map, nameWithoutExt, clip);
                        }
                    }
                }
            }

            if (foundModern) return map;

            // 2. Legacy fallback recursive scanning
            uint f_animationName = HashAlgorithms.Fnv1a("mAnimationName"); // 0x948259d6
            uint f_particleEventDataList = HashAlgorithms.Fnv1a("mParticleEventDataList"); // 0xb1b6c70f
            uint f_effectName = HashAlgorithms.Fnv1a("mEffectName"); // 0x21c83c21
            uint f_effectKey2 = HashAlgorithms.Fnv1a("mEffectKey"); // 0x51c6c57f
            uint f_boneName2 = HashAlgorithms.Fnv1a("mBoneName"); // 0x0d73ab6d
            uint f_startFrame2 = HashAlgorithms.Fnv1a("mStartFrame"); // 0x32c1c69f
            uint f_soundEventDataList = HashAlgorithms.Fnv1a("mSoundEventDataList"); // 0x0a6ef062
            uint f_soundName2 = HashAlgorithms.Fnv1a("mSoundName");

            void FindClipsRecursive(BinTreeProperty val, uint parentPathHash)
            {
                if (val is BinTreeStruct s)
                {
                    if (s.Properties.TryGetValue(f_animationName, out var animNameProp))
                    {
                        string animName = (animNameProp is BinTreeString str ? str.Value : null) ?? "";
                        if (!string.IsNullOrEmpty(animName))
                        {
                            string animFile = Path.GetFileName(animName.Replace('\\', '/'));
                            var clip = new VfxAnimationClip
                            {
                                Name = parentPathHash.ToString("x8"),
                                AnimationName = animName
                            };

                            if (s.Properties.TryGetValue(f_particleEventDataList, out var listProp) && listProp is BinTreeContainer list)
                            {
                                foreach (var el in list.Elements)
                                {
                                    if (el is BinTreeStruct eventStruct)
                                    {
                                        var (effectName, effectKey) = GetStringOrHash(eventStruct, f_effectName);
                                        if (string.IsNullOrEmpty(effectName) && effectKey == 0)
                                        {
                                            (effectName, effectKey) = GetStringOrHash(eventStruct, f_effectKey2);
                                        }
                                        
                                        var (boneName, boneHash) = GetStringOrHash(eventStruct, f_boneName2);
                                        float startFrame = AsF32(Get(eventStruct.Properties, f_startFrame2)) ?? 0f;

                                        clip.ParticleEvents.Add(new VfxAnimationEvent
                                        {
                                            EffectName = effectName,
                                            EffectHash = effectKey,
                                            BoneName = boneName,
                                            BoneHash = boneHash,
                                            StartFrame = startFrame
                                        });
                                    }
                                }
                            }

                            if (s.Properties.TryGetValue(f_soundEventDataList, out var sListProp) && sListProp is BinTreeContainer sList)
                            {
                                foreach (var el in sList.Elements)
                                {
                                    if (el is BinTreeStruct soundStruct)
                                    {
                                        var (soundName, soundHash) = GetStringOrHash(soundStruct, f_soundName2);
                                        float startFrame = AsF32(Get(soundStruct.Properties, f_startFrame2)) ?? 0f;

                                        clip.SoundEvents.Add(new VfxSoundEvent
                                        {
                                            SoundName = soundName,
                                            SoundHash = soundHash,
                                            StartFrame = startFrame
                                        });
                                    }
                                }
                            }
                            MergeClip(map, animFile, clip);
                            string nameWithoutExt = Path.GetFileNameWithoutExtension(animFile);
                            MergeClip(map, nameWithoutExt, clip);
                        }
                    }
                    
                    foreach (var prop in s.Properties.Values)
                    {
                        FindClipsRecursive(prop, parentPathHash);
                    }
                }
                else if (val is BinTreeContainer c)
                {
                    foreach (var el in c.Elements)
                    {
                        FindClipsRecursive(el, parentPathHash);
                    }
                }
                else if (val is BinTreeMap m)
                {
                    foreach (var pair in m)
                    {
                        FindClipsRecursive(pair.Value, parentPathHash);
                    }
                }
            }

            foreach (var o in bin.Objects.Values)
            {
                foreach (var prop in o.Properties.Values)
                {
                    FindClipsRecursive(prop, o.PathHash);
                }
            }
            return map;
        }

        private static (string Name, uint Hash) GetStringOrHash(BinTreeStruct s, uint field)
        {
            if (!s.Properties.TryGetValue(field, out var p)) return ("", 0u);
            return p switch
            {
                BinTreeString str => (str.Value, HashAlgorithms.Fnv1a(str.Value)),
                BinTreeHash h => ("", h.Value),
                BinTreeU32 u => ("", u.Value),
                _ => ("", 0u),
            };
        }
    }

    public class VfxAnimationEvent
    {
        public string EffectName { get; set; }
        public uint EffectHash { get; set; }
        public string BoneName { get; set; }
        public uint BoneHash { get; set; }
        public float StartFrame { get; set; }
    }

    public class VfxSoundEvent
    {
        public string SoundName { get; set; }
        public uint SoundHash { get; set; }
        public float StartFrame { get; set; }
        public bool Triggered { get; set; }
    }

    public class VfxAnimationClip
    {
        public string Name { get; set; }
        public string AnimationName { get; set; }
        public List<VfxAnimationEvent> ParticleEvents { get; } = new List<VfxAnimationEvent>();
        public List<VfxSoundEvent> SoundEvents { get; } = new List<VfxSoundEvent>();
    }
}
