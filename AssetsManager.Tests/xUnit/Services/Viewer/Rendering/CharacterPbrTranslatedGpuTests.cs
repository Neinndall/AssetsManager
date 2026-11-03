using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Tests.Support;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;
using LeagueToolkit.Core.Meta;
using Xunit;
using Xunit.Abstractions;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

public sealed class CharacterPbrTranslatedGpuTests(ITestOutputHelper output)
{
    [InstalledChampionFact("Lux")]
    public void ExtractedLuxSkinsReportTheMaterialPathsThatCanBenefit()
    {
        using var projects = new InstalledChampionProjects();
        string root = Path.Combine(projects.GetRoot("Lux"),"data","characters","lux","skins");
        Assert.True(Directory.Exists(root));
        string[] files = Directory.GetFiles(root,"skin*.bin");
        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            using var stream = File.OpenRead(file);
            var metadata = SknMaterialTextureResolver.ReadMetadata(new BinTree(stream));
            if (metadata.Pbr != null || metadata.PbrOverrides.Count > 0)
                output.WriteLine($"{Path.GetFileName(file)} default={metadata.Pbr} overrides={string.Join(';',metadata.PbrOverrides.Select(p => p.Key + ':' + p.Value))}");
        }
        output.WriteLine($"Examined {files.Length} main Lux skin documents.");
    }

    [Fact]
    public void ShippedLitUberPermutationsConsumeRmaAndTheTangentSemanticInGpu()
    {
        string install = InstalledSkins.FindInstall();
        Assert.NotNull(install);
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var context = new HiddenWglContext();
                using var gl = GL.GetApi(context.GetProcAddress);
                using var draw = new SkinSubmeshRenderer(gl, InstalledSkins.Settings(install), null);
                BitmapSource Bitmap(byte r, byte g, byte b)
                {
                    var bitmap = BitmapSource.Create(1,1,96,96,PixelFormats.Bgra32,null,new byte[] { b,g,r,255 },4);
                    bitmap.Freeze(); return bitmap;
                }
                var original = ModelMaterialDefinition.TextureOnly("base") with { Pbr = new("rma","normal") };
                var range = new MapCharacterMeshRange("Body",0,6);
                var mesh = new MapCharacterMeshData(
                    new Vector3[] { new(-1,-1,0),new(1,-1,0),new(-1,1,0),new(1,1,0) },
                    Enumerable.Repeat(Vector3.UnitZ,4).ToArray(), Enumerable.Repeat(new Vector2(.5f),4).ToArray(),
                    Enumerable.Repeat(new Vector4(1,0,0,1),4).ToArray(),new byte[16],
                    Enumerable.Range(0,16).Select(i => i % 4 == 0 ? 1f : 0f).ToArray(),new uint[] { 0,1,2,2,1,3 },new[] { range });
                SkinSubmeshRenderer.Result Render(bool rma, bool tangent, byte metal = 0, byte normalX = 128, bool rotateTangent = false)
                {
                    var material = CharacterPbrProgram.Resolve(original,tangent,path => path == "rma" && !rma ? null : 1);
                    var asset = new MapCharacterAssetData(new("test",null,null,1,Array.Empty<string>(),0),
                        rotateTangent ? mesh with { Tangents = Enumerable.Repeat(new Vector4(0,1,0,1),4).ToArray() } : mesh,null,
                        new SknMaterialTextureResolution(material,new Dictionary<string,ModelMaterialDefinition>()),
                        new Dictionary<string,BitmapSource> { ["base"] = Bitmap(153,71,26), ["rma"] = Bitmap(128,metal,255), ["normal"] = Bitmap(normalX,128,255) },null);
                    var result = draw.Render(asset,range,material);
                    Assert.True(result.Bound); Assert.True(result.Covered > 20);
                    Assert.Equal(0,result.NonFinite); Assert.Empty(result.MissingTextures);
                    return result;
                }
                var baseline = Render(false,false);
                var dielectric = Render(true,false);
                var metal = Render(true,false,255);
                var mapped = Render(true,true,0,230);
                var rotated = Render(true,true,0,230,true);
                output.WriteLine($"base={baseline.Mean}; dielectric={dielectric.Mean}; metal={metal.Mean}; normal={mapped.Mean}; rotated={rotated.Mean}");
                Assert.True(Vector4.Distance(baseline.Mean,dielectric.Mean) > .001f);
                Assert.True(Vector4.Distance(dielectric.Mean,metal.Mean) > .001f);
                Assert.True(Vector4.Distance(dielectric.Mean,mapped.Mean) > .001f);
                Assert.True(Vector4.Distance(mapped.Mean,rotated.Mean) > .001f);
                Assert.Equal(GLEnum.NoError,gl.GetError());
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(90)));
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
