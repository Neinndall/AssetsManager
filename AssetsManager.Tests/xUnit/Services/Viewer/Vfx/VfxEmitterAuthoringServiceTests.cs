using System;
using System.IO;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Authoring;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx
{
    public sealed class VfxEmitterAuthoringServiceTests
    {
        private static readonly uint SystemPath = Fnv1a.HashLower("Characters/Test/Particles/TestSystem");
        private static readonly uint SystemClass = Fnv1a.HashLower("VfxSystemDefinitionData");
        private static readonly uint EmitterClass = Fnv1a.HashLower("VfxEmitterDefinitionData");
        private static readonly uint ComplexList = Fnv1a.HashLower("complexEmitterDefinitionData");
        private static readonly uint SimpleList = Fnv1a.HashLower("simpleEmitterDefinitionData");
        private static readonly uint Translation = Fnv1a.HashLower("translationOverride");
        private static readonly uint Rotation = Fnv1a.HashLower("rotationOverride");
        private static readonly uint EmitterName = Fnv1a.HashLower("emitterName");
        private static readonly uint Rate = Fnv1a.HashLower("rate");
        private static readonly uint ParticleLifetime = Fnv1a.HashLower("particleLifetime");
        private static readonly uint BirthColor = Fnv1a.HashLower("birthColor");
        private static readonly uint ConstantValue = Fnv1a.HashLower("constantValue");
        private static readonly uint Dynamics = Fnv1a.HashLower("dynamics");
        private static readonly uint Times = Fnv1a.HashLower("times");
        private static readonly uint Values = Fnv1a.HashLower("values");
        private static readonly uint FieldCollection = Fnv1a.HashLower("fieldCollectionDefinition");
        private static readonly uint AccelerationList = Fnv1a.HashLower("fieldAccelerationDefinitions");
        private static readonly uint AccelerationProperty = Fnv1a.HashLower("acceleration");
        private static readonly uint AccelerationClass = Fnv1a.HashLower("VfxFieldAccelerationDefinitionData");
        private static readonly uint UnexpectedForceClass = Fnv1a.HashLower("UnexpectedVfxFieldDefinitionData");
        private static readonly uint ValueFloatClass = Fnv1a.HashLower("ValueFloat");
        private static readonly uint ValueColorRgbClass = Fnv1a.HashLower("ValueColorRgb");
        private static readonly uint AnimatedVector3Class = Fnv1a.HashLower("VfxAnimatedVector3f");
        private const uint AnimatedColorRgbClass = 0x8152c1ec;

        [Fact]
        public void WriteTransformTargetsParsedSourceOrderAcrossComplexAndSimpleLists()
        {
            string path = TempBinPath();
            try
            {
                WriteFixture(path);
                var expected = new Vector3(10f, -20f, 30f);

                Assert.True(VfxEmitterAuthoringService.TryWriteTransform(
                    path,
                    SystemPath,
                    1,
                    VfxEmitterTransformProperty.Rotation,
                    expected,
                    out var updated,
                    out string error), error);

                Assert.Equal(expected, updated.Emitters[1].RotationOverride);
                Assert.Equal(new Vector3(1f, 2f, 3f), updated.Emitters[0].TranslationOverride);

                using (var stream = File.OpenRead(path))
                {
                    var tree = new BinTree(stream);
                    BinTreeObject system = tree.Objects[SystemPath];
                    var complex = Assert.IsType<BinTreeContainer>(system.Properties[ComplexList]);
                    var simple = Assert.IsType<BinTreeContainer>(system.Properties[SimpleList]);
                    var first = Assert.IsType<BinTreeStruct>(Assert.Single(complex.Elements));
                    var second = Assert.IsType<BinTreeStruct>(Assert.Single(simple.Elements));

                    Assert.Equal(new Vector3(1f, 2f, 3f), Assert.IsType<BinTreeVector3>(first.Properties[Translation]).Value);
                    Assert.False(first.Properties.ContainsKey(Rotation));
                    Assert.Equal(expected, Assert.IsType<BinTreeVector3>(second.Properties[Rotation]).Value);
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void CurveAuthoringActivatesEditsInsertsAndRemovesParallelKeys()
        {
            string path = TempBinPath();
            try
            {
                WriteFixture(path);

                Assert.True(VfxEmitterAuthoringService.TryActivateCurve(
                    path,
                    SystemPath,
                    0,
                    Rate,
                    VfxEmitterCurveFamily.Scalar,
                    new Vector4(2.5f, 0f, 0f, 0f),
                    out var activated,
                    out string activateError), activateError);
                Assert.Equal(new[] { 0f, 1f }, activated.Emitters[0].Rate.Times);
                Assert.Equal(new[] { 2.5f, 2.5f }, activated.Emitters[0].Rate.Values);

                Assert.True(VfxEmitterAuthoringService.TryWriteCurveKey(
                    path,
                    SystemPath,
                    0,
                    Rate,
                    VfxEmitterCurveFamily.Scalar,
                    1,
                    0.75f,
                    new Vector4(7f, 0f, 0f, 0f),
                    out var edited,
                    out string editError), editError);
                Assert.Equal(new[] { 0f, 0.75f }, edited.Emitters[0].Rate.Times);
                Assert.Equal(new[] { 2.5f, 7f }, edited.Emitters[0].Rate.Values);

                Assert.True(VfxEmitterAuthoringService.TryInsertCurveKey(
                    path,
                    SystemPath,
                    0,
                    Rate,
                    VfxEmitterCurveFamily.Scalar,
                    1,
                    0.5f,
                    new Vector4(4f, 0f, 0f, 0f),
                    out var inserted,
                    out string insertError), insertError);
                Assert.Equal(new[] { 0f, 0.5f, 0.75f }, inserted.Emitters[0].Rate.Times);
                Assert.Equal(new[] { 2.5f, 4f, 7f }, inserted.Emitters[0].Rate.Values);

                Assert.True(VfxEmitterAuthoringService.TryRemoveCurveKeys(
                    path,
                    SystemPath,
                    0,
                    Rate,
                    new[] { 0, 2 },
                    out var removed,
                    out string removeError), removeError);
                Assert.Equal(new[] { 0.5f }, removed.Emitters[0].Rate.Times);
                Assert.Equal(new[] { 4f }, removed.Emitters[0].Rate.Values);

                Assert.True(VfxEmitterAuthoringService.TryActivateCurve(
                    path,
                    SystemPath,
                    0,
                    ParticleLifetime,
                    VfxEmitterCurveFamily.Scalar,
                    new Vector4(3f, 0f, 0f, 0f),
                    out var promoted,
                    out string promoteError), promoteError);
                Assert.Equal(3f, promoted.Emitters[0].ParticleLifetime.Constant);
                Assert.Equal(new[] { 0f, 1f }, promoted.Emitters[0].ParticleLifetime.Times);
                Assert.Equal(new[] { 3f, 3f }, promoted.Emitters[0].ParticleLifetime.Values);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void CurveKeyTimePreservesAuthoredListOrderWithoutNeighborClamping()
        {
            string path = TempBinPath();
            try
            {
                WriteFixture(path);
                Assert.True(VfxEmitterAuthoringService.TryActivateCurve(
                    path,
                    SystemPath,
                    0,
                    Rate,
                    VfxEmitterCurveFamily.Scalar,
                    new Vector4(2.5f, 0f, 0f, 0f),
                    out _,
                    out string error), error);

                Assert.True(VfxEmitterAuthoringService.TryWriteCurveKey(
                    path,
                    SystemPath,
                    0,
                    Rate,
                    VfxEmitterCurveFamily.Scalar,
                    0,
                    2f,
                    new Vector4(9f, 0f, 0f, 0f),
                    out var updated,
                    out error), error);

                Assert.Equal(new[] { 2f, 1f }, updated.Emitters[0].Rate.Times);
                Assert.Equal(new[] { 9f, 2.5f }, updated.Emitters[0].Rate.Values);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ValueColorRgbActivationKeepsItsAuthoredDynamicsClass()
        {
            string path = TempBinPath();
            try
            {
                WriteFixture(path);
                SeedValueColorRgb(path);

                Assert.True(VfxEmitterAuthoringService.TryActivateCurve(
                    path,
                    SystemPath,
                    0,
                    BirthColor,
                    VfxEmitterCurveFamily.Vector4,
                    new Vector4(0.2f, 0.4f, 0.6f, 1f),
                    out var updated,
                    out string error), error);
                Assert.Equal(new Vector4(0.2f, 0.4f, 0.6f, 1f), updated.Emitters[0].BirthColor.Constant);
                Assert.Equal(new[] { 0f, 1f }, updated.Emitters[0].BirthColor.Times);

                using (var input = File.OpenRead(path))
                {
                    var tree = new BinTree(input);
                    var system = tree.Objects[SystemPath];
                    var emitters = Assert.IsType<BinTreeContainer>(system.Properties[ComplexList]);
                    var emitter = Assert.IsType<BinTreeStruct>(emitters.Elements[0]);
                    var color = Assert.IsType<BinTreeStruct>(emitter.Properties[BirthColor]);
                    Assert.Equal(ValueColorRgbClass, color.ClassHash);
                    var dynamics = Assert.IsType<BinTreeStruct>(color.Properties[Dynamics]);
                    Assert.Equal(AnimatedColorRgbClass, dynamics.ClassHash);
                    var values = Assert.IsType<BinTreeContainer>(dynamics.Properties[Values]);
                    Assert.Equal(BinPropertyType.Vector3, values.ElementType);
                    Assert.All(values.Elements, value => Assert.IsType<BinTreeVector3>(value));
                }

                Assert.True(VfxEmitterAuthoringService.TryWriteCurveKey(
                    path,
                    SystemPath,
                    0,
                    BirthColor,
                    VfxEmitterCurveFamily.Vector4,
                    1,
                    0.8f,
                    new Vector4(0.7f, 0.5f, 0.3f, 1f),
                    out updated,
                    out error), error);
                Assert.Equal(new Vector4(0.7f, 0.5f, 0.3f, 1f), updated.Emitters[0].BirthColor.Values[1]);

                using var editedInput = File.OpenRead(path);
                var editedTree = new BinTree(editedInput);
                var editedEmitters = Assert.IsType<BinTreeContainer>(editedTree.Objects[SystemPath].Properties[ComplexList]);
                var editedEmitter = Assert.IsType<BinTreeStruct>(editedEmitters.Elements[0]);
                var editedColor = Assert.IsType<BinTreeStruct>(editedEmitter.Properties[BirthColor]);
                var editedDynamics = Assert.IsType<BinTreeStruct>(editedColor.Properties[Dynamics]);
                var editedValues = Assert.IsType<BinTreeContainer>(editedDynamics.Properties[Values]);
                Assert.Equal(BinPropertyType.Vector3, editedValues.ElementType);
                Assert.IsType<BinTreeVector3>(editedValues.Elements[1]);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void AddForceCreatesLeagueFieldCollectionAtAuthoredDefaults()
        {
            string path = TempBinPath();
            try
            {
                WriteFixture(path);

                Assert.True(VfxEmitterAuthoringService.TryAddForce(
                    path,
                    SystemPath,
                    0,
                    VfxEmitterForceKind.Acceleration,
                    out var updated,
                    out string error), error);

                var field = Assert.Single(updated.Emitters[0].Fields.Acceleration);
                Assert.Equal(Vector3.Zero, field.Acceleration.Constant);
                Assert.True(field.LocalSpace);

                Assert.True(VfxEmitterAuthoringService.TryWriteForceVector(
                    path,
                    SystemPath,
                    0,
                    VfxEmitterForceKind.Acceleration,
                    0,
                    VfxEmitterForceProperty.Acceleration,
                    new Vector3(1f, 2f, 3f),
                    out updated,
                    out error), error);
                Assert.Equal(new Vector3(1f, 2f, 3f), Assert.Single(updated.Emitters[0].Fields.Acceleration).Acceleration.Constant);

                Assert.True(VfxEmitterAuthoringService.TryWriteForceBool(
                    path,
                    SystemPath,
                    0,
                    VfxEmitterForceKind.Acceleration,
                    0,
                    VfxEmitterForceProperty.LocalSpace,
                    false,
                    out updated,
                    out error), error);
                Assert.False(Assert.Single(updated.Emitters[0].Fields.Acceleration).LocalSpace);

                Assert.True(VfxEmitterAuthoringService.TryAddForce(
                    path,
                    SystemPath,
                    0,
                    VfxEmitterForceKind.Attraction,
                    out updated,
                    out error), error);
                Assert.True(VfxEmitterAuthoringService.TryWriteForceScalar(
                    path,
                    SystemPath,
                    0,
                    VfxEmitterForceKind.Attraction,
                    0,
                    VfxEmitterForceProperty.Radius,
                    125f,
                    out updated,
                    out error), error);
                Assert.Equal(125f, Assert.Single(updated.Emitters[0].Fields.Attraction).Radius.Constant);

                Assert.True(VfxEmitterAuthoringService.TryRemoveForce(
                    path,
                    SystemPath,
                    0,
                    VfxEmitterForceKind.Acceleration,
                    0,
                    out updated,
                    out error), error);
                Assert.Empty(updated.Emitters[0].Fields.Acceleration);
                Assert.Single(updated.Emitters[0].Fields.Attraction);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void UnsupportedForceClassBlocksIndexedAuthoringWithoutMutatingSource()
        {
            string path = TempBinPath();
            try
            {
                WriteFixture(path);
                Assert.True(VfxEmitterAuthoringService.TryAddForce(
                    path,
                    SystemPath,
                    0,
                    VfxEmitterForceKind.Acceleration,
                    out _,
                    out string error), error);
                SeedUnsupportedAccelerationEntry(path);
                byte[] before = File.ReadAllBytes(path);

                Assert.False(VfxEmitterAuthoringService.TryWriteForceVector(
                    path,
                    SystemPath,
                    0,
                    VfxEmitterForceKind.Acceleration,
                    0,
                    VfxEmitterForceProperty.Acceleration,
                    new Vector3(9f, 8f, 7f),
                    out var updated,
                    out error));
                Assert.Null(updated);
                Assert.Contains("unsupported authored class", error, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(before, File.ReadAllBytes(path));

                Assert.False(VfxEmitterAuthoringService.TryRemoveForce(
                    path,
                    SystemPath,
                    0,
                    VfxEmitterForceKind.Acceleration,
                    1,
                    out updated,
                    out error));
                Assert.Null(updated);
                Assert.Contains("unsupported authored class", error, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(before, File.ReadAllBytes(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ExistingForceCurveSupportsEditInsertAndRemoveWithoutFlatteningForce()
        {
            string path = TempBinPath();
            try
            {
                WriteFixture(path);
                Assert.True(VfxEmitterAuthoringService.TryAddForce(
                    path,
                    SystemPath,
                    0,
                    VfxEmitterForceKind.Acceleration,
                    out _,
                    out string error), error);
                Assert.True(VfxEmitterAuthoringService.TryWriteForceVector(
                    path,
                    SystemPath,
                    0,
                    VfxEmitterForceKind.Acceleration,
                    0,
                    VfxEmitterForceProperty.Acceleration,
                    new Vector3(1f, 2f, 3f),
                    out _,
                    out error), error);
                SeedAccelerationDynamics(path);

                Assert.True(VfxEmitterAuthoringService.TryWriteForceCurveKey(
                    path,
                    SystemPath,
                    0,
                    VfxEmitterForceKind.Acceleration,
                    0,
                    VfxEmitterForceProperty.Acceleration,
                    1,
                    0.75f,
                    new Vector4(4f, 5f, 6f, 0f),
                    out var edited,
                    out error), error);
                var acceleration = Assert.Single(edited.Emitters[0].Fields.Acceleration).Acceleration;
                Assert.Equal(new[] { 0f, 0.75f }, acceleration.Times);
                Assert.Equal(new[] { Vector3.Zero, new Vector3(4f, 5f, 6f) }, acceleration.Values);
                Assert.Equal(new Vector3(1f, 2f, 3f), acceleration.Constant);

                Assert.True(VfxEmitterAuthoringService.TryInsertForceCurveKey(
                    path,
                    SystemPath,
                    0,
                    VfxEmitterForceKind.Acceleration,
                    0,
                    VfxEmitterForceProperty.Acceleration,
                    1,
                    0.5f,
                    new Vector4(2f, 3f, 4f, 0f),
                    out var inserted,
                    out error), error);
                acceleration = Assert.Single(inserted.Emitters[0].Fields.Acceleration).Acceleration;
                Assert.Equal(new[] { 0f, 0.5f, 0.75f }, acceleration.Times);

                Assert.True(VfxEmitterAuthoringService.TryRemoveForceCurveKeys(
                    path,
                    SystemPath,
                    0,
                    VfxEmitterForceKind.Acceleration,
                    0,
                    VfxEmitterForceProperty.Acceleration,
                    new[] { 0, 2 },
                    out var removed,
                    out error), error);
                acceleration = Assert.Single(removed.Emitters[0].Fields.Acceleration).Acceleration;
                Assert.Equal(new[] { 0.5f }, acceleration.Times);
                Assert.Equal(new[] { new Vector3(2f, 3f, 4f) }, acceleration.Values);
                Assert.Equal(new Vector3(1f, 2f, 3f), acceleration.Constant);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void FailedTransformEditLeavesSourceBytesUntouched()
        {
            string path = TempBinPath();
            try
            {
                WriteFixture(path);
                byte[] before = File.ReadAllBytes(path);

                Assert.False(VfxEmitterAuthoringService.TryWriteTransform(
                    path,
                    SystemPath,
                    99,
                    VfxEmitterTransformProperty.Translation,
                    Vector3.One,
                    out var updated,
                    out string error));

                Assert.Null(updated);
                Assert.Contains("Emitter [99]", error);
                Assert.Equal(before, File.ReadAllBytes(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static void SeedValueColorRgb(string path)
        {
            BinTree tree;
            using (FileStream input = File.OpenRead(path))
                tree = new BinTree(input);

            BinTreeObject system = tree.Objects[SystemPath];
            var emitters = Assert.IsType<BinTreeContainer>(system.Properties[ComplexList]);
            var emitter = Assert.IsType<BinTreeStruct>(emitters.Elements[0]);
            emitter.Properties[BirthColor] = new BinTreeStruct(
                BirthColor,
                ValueColorRgbClass,
                new BinTreeProperty[]
                {
                    new BinTreeVector3(ConstantValue, new Vector3(0.2f, 0.4f, 0.6f))
                });
            using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            tree.Write(output);
        }

        private static void SeedUnsupportedAccelerationEntry(string path)
        {
            BinTree tree;
            using (FileStream input = File.OpenRead(path))
                tree = new BinTree(input);

            BinTreeObject system = tree.Objects[SystemPath];
            var emitters = Assert.IsType<BinTreeContainer>(system.Properties[ComplexList]);
            var emitter = Assert.IsType<BinTreeStruct>(emitters.Elements[0]);
            var collection = Assert.IsType<BinTreeStruct>(emitter.Properties[FieldCollection]);
            var forces = Assert.IsType<BinTreeContainer>(collection.Properties[AccelerationList]);
            Assert.IsType<BinTreeStruct>(Assert.Single(forces.Elements));
            Assert.Equal(AccelerationClass, ((BinTreeStruct)forces.Elements[0]).ClassHash);

            var elements = new BinTreeProperty[forces.Elements.Count + 1];
            elements[0] = new BinTreeStruct(0, UnexpectedForceClass, Array.Empty<BinTreeProperty>());
            for (int index = 0; index < forces.Elements.Count; index++)
                elements[index + 1] = forces.Elements[index];
            collection.Properties[AccelerationList] = new BinTreeContainer(
                AccelerationList,
                BinPropertyType.Struct,
                elements);

            using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            tree.Write(output);
        }

        private static void SeedAccelerationDynamics(string path)
        {
            BinTree tree;
            using (FileStream input = File.OpenRead(path))
                tree = new BinTree(input);

            BinTreeObject system = tree.Objects[SystemPath];
            var emitters = Assert.IsType<BinTreeContainer>(system.Properties[ComplexList]);
            var emitter = Assert.IsType<BinTreeStruct>(emitters.Elements[0]);
            var collection = Assert.IsType<BinTreeStruct>(emitter.Properties[FieldCollection]);
            var forces = Assert.IsType<BinTreeContainer>(collection.Properties[AccelerationList]);
            var force = Assert.IsType<BinTreeStruct>(forces.Elements[0]);
            var acceleration = Assert.IsType<BinTreeStruct>(force.Properties[AccelerationProperty]);
            acceleration.Properties[Dynamics] = new BinTreeStruct(
                Dynamics,
                AnimatedVector3Class,
                new BinTreeProperty[]
                {
                    new BinTreeContainer(Times, BinPropertyType.F32, new BinTreeProperty[]
                    {
                        new BinTreeF32(0, 0f),
                        new BinTreeF32(0, 1f)
                    }),
                    new BinTreeContainer(Values, BinPropertyType.Vector3, new BinTreeProperty[]
                    {
                        new BinTreeVector3(0, Vector3.Zero),
                        new BinTreeVector3(0, Vector3.One)
                    })
                });
            using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                tree.Write(output);
        }

        private static void WriteFixture(string path)
        {
            var first = new BinTreeStruct(
                0,
                EmitterClass,
                new BinTreeProperty[]
                {
                    new BinTreeString(EmitterName, "Complex"),
                    new BinTreeVector3(Translation, new Vector3(1f, 2f, 3f)),
                    new BinTreeStruct(
                        Rate,
                        ValueFloatClass,
                        new BinTreeProperty[] { new BinTreeF32(ConstantValue, 2.5f) })
                });
            var second = new BinTreeStruct(
                0,
                EmitterClass,
                new BinTreeProperty[]
                {
                    new BinTreeString(EmitterName, "Simple")
                });
            var system = new BinTreeObject(
                SystemPath,
                SystemClass,
                new BinTreeProperty[]
                {
                    new BinTreeContainer(ComplexList, BinPropertyType.Struct, new[] { first }),
                    new BinTreeContainer(SimpleList, BinPropertyType.Struct, new[] { second })
                });
            var tree = new BinTree(new[] { system }, Array.Empty<string>());
            using var stream = File.Create(path);
            tree.Write(stream);
        }

        private static string TempBinPath()
            => Path.Combine(Path.GetTempPath(), $"assetsmanager-vfx-authoring-{Guid.NewGuid():N}.bin");
    }
}
