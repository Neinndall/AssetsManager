using System;
using System.IO;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx
{
    public sealed class VfxShimmerDriverEvaluatorTests
    {
        private static uint H(string name) => name.StartsWith("0x") ? Convert.ToUInt32(name[2..], 16) : Fnv1a.HashLower(name);
        private static BinTreeStruct Node(string field, string type, params BinTreeProperty[] children) => new(H(field), H(type), children);
        private static BinTreeStruct Scalar(string field, float value) => Node(field, "VfxFloatConstantDriver", new BinTreeF32(H("Float"), value));
        private static BinTreeStruct Vec(string field, Vector3 value) => Node(field, "VfxVector3ConstantDriver", new BinTreeVector3(H("Vector3"), value));
        private static BinTreeStruct Reduction(string field, string type, params BinTreeProperty[] values) =>
            Node(field, type, new BinTreeContainer(H("params"), BinPropertyType.Struct, values));

        [Theory]
        [InlineData("Add", 6, 10, 14)]
        [InlineData("Multiply", 8, 21, 40)]
        [InlineData("Min", 2, 3, 4)]
        [InlineData("Max", 4, 7, 10)]
        public void ReducesEveryInputComponent(string operation, float x, float y, float z)
        {
            var graph = Reduction("Scale", $"Vfx{operation}Vector3Driver",
                Vec("", new Vector3(2, 3, 4)), Vec("", new Vector3(4, 7, 10)));
            Assert.Equal(new Vector3(x, y, z), VfxShimmerDriverEvaluator.Vector3Value(graph));
        }

        [Fact]
        public void CombinesTypedColorAndScalarGraphs()
        {
            var rgb = Node("Rgb", "VfxScaleVector3Driver",
                Node("Vector3", "VfxColorRgbConstantDriver", new BinTreeVector3(H("Color"), new Vector3(0.2f, 0.4f, 0.6f))),
                Scalar("ScaleFactor", 2));
            var alpha = Node("Alpha", "VfxClampFloatDriver",
                Scalar("Param", 1.5f), new BinTreeF32(H("Low"), 0), new BinTreeF32(H("High"), 0.75f));
            var root = Node("Color", "VfxVector4DynamicProperty", Node("Vector4", "VfxColorRgbaDriver", rgb, alpha));
            Assert.Equal(new Vector4(0.4f, 0.8f, 1.2f, 0.75f), VfxShimmerDriverEvaluator.Vector4Value(root));
        }

        [Fact]
        public void DoesNotTreatAnimatedCurveOrRandomBranchAsConstant()
        {
            var curve = Node("", "0x2d42ea41", Node("Vector3", "ValueVector3",
                new BinTreeVector3(H("constantValue"), new Vector3(99)),
                Node("dynamics", "VfxAnimatedVector3fVariableData",
                    new BinTreeContainer(H("times"), BinPropertyType.F32, new[] { new BinTreeF32(0, 0) }),
                    new BinTreeContainer(H("values"), BinPropertyType.Vector3, new[] { new BinTreeVector3(0, Vector3.One) }))));
            Assert.Null(VfxShimmerDriverEvaluator.Vector3Value(Reduction("Scale", "VfxAddVector3Driver", Vec("", Vector3.One), curve)));
            Assert.Null(VfxShimmerDriverEvaluator.Evaluate(Node("", "0x414d1503"), 1));
            Assert.Null(VfxShimmerDriverEvaluator.Evaluate(Node("", "VfxFloatEasingDriver"), 1));
            Assert.Equal(new float[] { 0 }, VfxShimmerDriverEvaluator.Evaluate(Node("", "VfxFloatEasingDriver", new BinTreeF32(H("duration"), 0)), 1));
        }

        [Fact]
        public void HonorsDefaultsAndDoesNotScavengeUnknownOrMismatchedNodes()
        {
            Assert.Equal(new Vector4(0, 0, 0, 1), VfxShimmerDriverEvaluator.Vector4Value(Node("", "VfxColorConstantDriver")));
            Assert.Equal(Vector3.One, VfxShimmerDriverEvaluator.Vector3Value(Node("", "0x2d42ea41")));
            Assert.Equal(Vector3.Zero, VfxShimmerDriverEvaluator.Vector3Value(Node("", "UnknownDriver", Vec("anything", new Vector3(99)))));
            Assert.Equal(Vector3.Zero, VfxShimmerDriverEvaluator.Vector3Value(Scalar("", 5)));
            Assert.Equal(Vector3.Zero, VfxShimmerDriverEvaluator.Vector3Value(Node("", "VfxVector3DynamicProperty",
                new BinTreeVector3(H("Vector3"), new Vector3(99)))));
            Assert.Equal(Vector3.Zero, VfxShimmerDriverEvaluator.Vector3Value(Reduction("", "VfxMultiplyVector3Driver")));
        }

        [Fact]
        public void HandlesDivisionAndInverseBounds()
        {
            var vectorDivide = Node("", "0x64707da8", Vec("Vector3", new Vector3(8, 6, 4)), Vec("Divisor", new Vector3(2, 0, 4)));
            Assert.Equal(new Vector3(4, 0, 1), VfxShimmerDriverEvaluator.Vector3Value(vectorDivide));
            Assert.Equal(new Vector3(4, 3, 2), VfxShimmerDriverEvaluator.Vector3Value(Node("", "0x95182f0a", Vec("Vector3", new Vector3(8, 6, 4)), Scalar("Divisor", 2))));
            Assert.Equal(Vector3.Zero, VfxShimmerDriverEvaluator.Vector3Value(Node("", "VfxClampVector3Driver",
                Vec("Param", new Vector3(5)), new BinTreeVector3(H("Low"), new Vector3(0, 2, 0)), new BinTreeVector3(H("High"), Vector3.One))));
        }

        [Fact]
        public void EvaluatesNormalizeLengthAbsAndUnclampedLerp()
        {
            Assert.Equal(new Vector3(0.6f, 0.8f, 0), VfxShimmerDriverEvaluator.Vector3Value(Node("", "VfxNormalizeVector3Driver", Vec("Vector3Input", new Vector3(3, 4, 0)))));
            Assert.Equal(new float[] { 5 }, VfxShimmerDriverEvaluator.Evaluate(Node("", "VfxLengthVector3Driver", Vec("Vector3Input", new Vector3(3, 4, 0))), 1));
            Assert.Equal(Vector3.One, VfxShimmerDriverEvaluator.Vector3Value(Node("", "VfxAbsVector3Driver", Vec("Param", -Vector3.One))));
            Assert.Equal(new Vector3(5), VfxShimmerDriverEvaluator.Vector3Value(Node("", "VfxVector3LerpDriver",
                Vec("From", Vector3.One), Vec("To", new Vector3(3)), Scalar("Factor", 2))));
        }

        [Fact]
        public void EvaluatesComposeBroadcastExtendAndSine()
        {
            Assert.Equal(new Vector3(1, 2, 3), VfxShimmerDriverEvaluator.Vector3Value(Node("", "0x65e1b9a2", Scalar("X", 1), Scalar("Y", 2), Scalar("Z", 3))));
            Assert.Equal(new Vector3(4), VfxShimmerDriverEvaluator.Vector3Value(Node("", "0xdef9bfd5", Scalar("Float", 4))));
            Assert.Equal(new Vector4(1, 2, 3, 4), VfxShimmerDriverEvaluator.Vector4Value(Node("", "0x9c5c4342", Vec("Input", new Vector3(1, 2, 3)), new BinTreeF32(0xb1ea6248, 4))));
            Assert.Equal(new float[] { 1 }, VfxShimmerDriverEvaluator.Evaluate(Node("", "VfxFloatSineDriver", Scalar("Time", 0.25f), Scalar("period", 1)), 1));
        }

        [Fact]
        public void LimitsNestedGraphDepth()
        {
            BinTreeStruct graph = Vec("Vector3", Vector3.One);
            for (int i = 0; i < 30; i++) graph = Node("Vector3", "VfxVector3DynamicProperty", graph);
            Assert.Null(VfxShimmerDriverEvaluator.Vector3Value(graph));
        }

        [Fact]
        public void ReadsNestedComponentColorAndPreservesFirstAnimatedGraphFallback()
        {
            var curve = Node("Vector3", "0x2d42ea41", Node("Vector3", "ValueVector3",
                Node("dynamics", "VfxAnimatedVector3fVariableData",
                    new BinTreeContainer(H("times"), BinPropertyType.F32, new[] { new BinTreeF32(0, 0) }),
                    new BinTreeContainer(H("values"), BinPropertyType.Vector3, new[] { new BinTreeVector3(0, Vector3.One) }))));
            var emitter = Node("", "VfxShimmerEmitterDefinitionData",
                Node("VfxComponents", "VfxComponents",
                    Node("RenderComponent", "VfxMaterialRenderComponent",
                        Node("Color", "ColorContainer", Node("InitialColor", "VfxVector4DynamicProperty",
                            Node("Vector4", "0x7cc5a312", Node("colors", "ValueColor",
                                new BinTreeVector4(H("constantValue"), new Vector4(0.5f, 0.25f, 1, 1)))))),
                        Node("InitialScale", "VfxVector3DynamicProperty", curve)),
                    Node("PhysicsComponent", "VfxModularPhysicsComponent",
                        Node("Scale", "VfxVector3DynamicProperty", Vec("Vector3", new Vector3(99)))),
                    Node("GeometryComponent", "VfxGeometryComponent",
                        new BinTreeString(H("mesh"), "assets/cube.gmesh"),
                        new BinTreeString(H("texture"), "assets/cube.tex"))));
            var parsed = ParseEmitter(emitter);
            Assert.Equal(new Vector4(0.5f, 0.25f, 1, 1), parsed.BirthColor.Constant);
            Assert.Equal(Vector3.One, parsed.BirthScale.Constant);
            Assert.Equal("assets/cube.gmesh", parsed.MeshPath);
            Assert.Equal("assets/cube.tex", parsed.TexturePath);
        }

        private static AssetsManager.Views.Models.Viewer.VfxEmitterDefinition ParseEmitter(BinTreeStruct emitter)
        {
            var system = new BinTreeObject("Vfx/Test", "VfxSystemDefinitionData",
                new BinTreeProperty[] { new BinTreeContainer(H("shimmerEmitterDefinitionData"), BinPropertyType.Struct, new[] { emitter }) });
            using var stream = new MemoryStream();
            new BinTree(new[] { system }, Array.Empty<string>()).Write(stream);
            return Assert.Single(Assert.Single(VfxGraphParser.ParseDocument(stream.ToArray()).Systems).Value.Emitters);
        }

        [Fact]
        public void ParsesCombinedGraphsFromSerializedShimmerBin()
        {
            var emitter = Node("", "VfxShimmerEmitterDefinitionData",
                new BinTreeString(H("mesh"), "assets/particles/test.scb"),
                Node("Scale", "VfxVector3DynamicProperty", Reduction("Vector3", "VfxAddVector3Driver", Vec("", Vector3.One), Vec("", new Vector3(2)))),
                Node("Position", "VfxVector3DynamicProperty", Node("Vector3", "VfxScaleVector3Driver", Vec("Vector3", new Vector3(10, 20, 30)), Scalar("ScaleFactor", 2))));
            var parsed = ParseEmitter(emitter);
            Assert.Equal(new Vector3(3), parsed.BirthScale.Constant);
            Assert.Equal(new Vector3(20, 40, 60), parsed.EmitterPosition.Constant);
        }
    }
}
