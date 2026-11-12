using System;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Views.Dialogs;
using LeagueToolkit.Core.Mesh;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer;

public sealed class SknVertexDiffAnalyzerTests
{
    private sealed record Vertex(Vector3 Position)
    {
        public Vector3 Normal { get; init; } = Vector3.UnitZ;
        public Vector2 Uv { get; init; }
        public byte Bone { get; init; }
        public Vector4 Weights { get; init; } = Vector4.UnitX;
        public uint Color { get; init; } = uint.MaxValue;
        public Vector4 Tangent { get; init; } = new(1, 0, 0, 1);
    }

    private static Vertex[] Triangle() => new[]
    {
        new Vertex(Vector3.Zero), new Vertex(Vector3.UnitX), new Vertex(Vector3.UnitY)
    };

    [Theory]
    [InlineData("Normals")]
    [InlineData("UV")]
    [InlineData("Colors")]
    [InlineData("Bone indices")]
    [InlineData("Bone weights")]
    [InlineData("Tangents")]
    public void AttributeOnlyChangesAreReportedWithoutGeometryChanges(string channel)
    {
        var original = Triangle();
        var edited = Triangle();
        edited[1] = channel switch
        {
            "Normals" => edited[1] with { Normal = new(0, 0, MathF.BitIncrement(1f)) },
            "UV" => edited[1] with { Uv = Vector2.One },
            "Colors" => edited[1] with { Color = 0xff00ffff },
            "Bone indices" => edited[1] with { Bone = 7 },
            "Bone weights" => edited[1] with { Weights = new(0.5f, 0.5f, 0, 0) },
            _ => edited[1] with { Tangent = new(0, 1, 0, -1) }
        };
        using var oldMesh = Mesh(original);
        using var newMesh = Mesh(edited);

        var result = SknVertexDiffAnalyzer.Compare(oldMesh, newMesh)["Body"];
        var change = Assert.Single(result.Changes);
        Assert.Equal(channel, change.Name);
        Assert.Equal(1, change.OldOnly);
        Assert.Equal(1, change.NewOnly);
        Assert.Equal(Vector3.UnitX, Assert.Single(result.ChangedPositions));
    }

    [Fact]
    public void VertexReorderingAndDuplicateSeamsDoNotCreateAttributeChanges()
    {
        var original = Triangle().Append(new Vertex(Vector3.Zero) { Uv = Vector2.One }).ToArray();
        using var oldMesh = Mesh(original);
        using var newMesh = Mesh(original.Reverse().ToArray());

        Assert.False(SknVertexDiffAnalyzer.Compare(oldMesh, newMesh)["Body"].HasChanges);
    }

    [Fact]
    public void ChangingAttributeAssociationsAtCoincidentVerticesIsDetected()
    {
        var a = new Vertex(Vector3.Zero);
        var b = a with { Normal = Vector3.UnitY, Uv = Vector2.One };
        using var oldMesh = Mesh(new[] { a, b, new Vertex(Vector3.UnitX) });
        using var newMesh = Mesh(new[] { a with { Uv = b.Uv }, b with { Uv = a.Uv }, new Vertex(Vector3.UnitX) });

        Assert.Equal("Attribute combinations",
            Assert.Single(SknVertexDiffAnalyzer.Compare(oldMesh, newMesh)["Body"].Changes).Name);
    }

    [Fact]
    public void PositionChangesDoNotMisreportEveryAttributeAsModified()
    {
        var edited = Triangle();
        edited[1] = edited[1] with { Position = new(2, 0, 0) };
        using var oldMesh = Mesh(Triangle());
        using var newMesh = Mesh(edited);

        var change = Assert.Single(SknVertexDiffAnalyzer.Compare(oldMesh, newMesh)["Body"].Changes);
        Assert.Equal("Positions", change.Name);
        Assert.Equal(1, change.OldOnly);
        Assert.Equal(1, change.NewOnly);
    }

    [Fact]
    public void AddedAndRemovedPositionsKeepTheirSeparateCounts()
    {
        using var oldMesh = Mesh(Triangle());
        using var newMesh = Mesh(Triangle().Append(new Vertex(new(3, 0, 0))).ToArray());
        var forward = Assert.Single(SknVertexDiffAnalyzer.Compare(oldMesh, newMesh)["Body"].Changes);
        Assert.Equal("Positions", forward.Name);
        Assert.Equal(0, forward.OldOnly);
        Assert.Equal(1, forward.NewOnly);
        var reverse = Assert.Single(SknVertexDiffAnalyzer.Compare(newMesh, oldMesh)["Body"].Changes);
        Assert.Equal(1, reverse.OldOnly);
        Assert.Equal(0, reverse.NewOnly);
    }

    [Fact]
    public void AttributeCombinationChangesRemainHighlightedAlongsideOtherChanges()
    {
        var original = Triangle().Concat(new[]
        {
            new Vertex(new(2, 0, 0)),
            new Vertex(new(2, 0, 0)) { Normal = Vector3.UnitY, Uv = Vector2.One }
        }).ToArray();
        var edited = original.ToArray();
        edited[0] = edited[0] with { Normal = Vector3.UnitX };
        edited[3] = edited[3] with { Uv = Vector2.One };
        edited[4] = edited[4] with { Uv = Vector2.Zero };
        using var oldMesh = Mesh(original);
        using var newMesh = Mesh(edited);
        var result = SknVertexDiffAnalyzer.Compare(oldMesh, newMesh)["Body"];
        Assert.Equal("Normals", Assert.Single(result.Changes).Name);
        Assert.Contains(new Vector3(2, 0, 0), result.ChangedPositions);
        Assert.Contains(Vector3.Zero, result.ChangedPositions);
    }

    [Fact]
    public void OptionalStreamPresenceAndUnreferencedVertexChangesAreReported()
    {
        using var basic = Mesh(Triangle(), vertexSize: 52);
        using var colored = Mesh(Triangle(), vertexSize: 56);
        Assert.Equal("Colors", Assert.Single(SknVertexDiffAnalyzer.Compare(basic, colored)["Body"].Changes).Name);

        var original = Triangle().Append(new Vertex(new(3, 0, 0))).ToArray();
        var edited = original.ToArray();
        edited[3] = edited[3] with { Uv = Vector2.One };
        using var oldMesh = Mesh(original);
        using var newMesh = Mesh(edited);
        Assert.Equal("UV", Assert.Single(SknVertexDiffAnalyzer.Compare(oldMesh, newMesh)["Body"].Changes).Name);
    }

    [Fact]
    public void AffectedSurfacesContainOnlyTrianglesTouchingChangedPositions()
    {
        var mesh = new System.Windows.Media.Media3D.MeshGeometry3D();
        foreach (var point in new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, new Vector3(2, 0, 0) })
            mesh.Positions.Add(new(point.X, point.Y, point.Z));
        foreach (int index in new[] { 0, 1, 2, 1, 3, 2 })
            mesh.TriangleIndices.Add(index);

        var affected = SknMeshDiffAnalyzer.ExtractAffectedMesh(mesh, new System.Collections.Generic.HashSet<Vector3> { Vector3.Zero });
        Assert.Equal(1, SknMeshDiffAnalyzer.GetTriangleCount(affected));
        Assert.Equal(1, SknMeshDiffAnalyzer.GetTriangleCount(SknMeshDiffAnalyzer.ExtractDifferenceMesh(affected, mesh)));
    }

    [Fact]
    public void ArchivedEvelynnNormalsMatchIndependentBinaryAuditWhenFixtureIsProvided()
    {
        string root = Environment.GetEnvironmentVariable("ASSETSMANAGER_SKN_DIFF_DIAGNOSTIC_ROOT");
        if (string.IsNullOrWhiteSpace(root))
            return;

        using var oldMesh = SkinnedMesh.ReadFromSimpleSkin(Path.Combine(root, "old-evelynn_skin66.skn"));
        using var newMesh = SkinnedMesh.ReadFromSimpleSkin(Path.Combine(root, "new-evelynn_skin66.skn"));
        var changes = SknVertexDiffAnalyzer.Compare(oldMesh, newMesh).Values.SelectMany(part => part.Changes).ToArray();
        Assert.Equal(6, changes.Length);
        Assert.All(changes, change => Assert.Equal("Normals", change.Name));
        Assert.Equal(1765, changes.Sum(change => change.OldOnly));
        Assert.Equal(1765, changes.Sum(change => change.NewOnly));
    }

    private static SkinnedMesh Mesh(Vertex[] vertices, int vertexSize = 72)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x00112233u);
            writer.Write((ushort)4);
            writer.Write((ushort)1);
            writer.Write(1);
            var name = new byte[64];
            System.Text.Encoding.ASCII.GetBytes("Body").CopyTo(name, 0);
            writer.Write(name);
            writer.Write(0);
            writer.Write(vertices.Length);
            writer.Write(0);
            writer.Write(3);
            writer.Write(0u);
            writer.Write(3);
            writer.Write(vertices.Length);
            writer.Write(vertexSize);
            writer.Write(vertexSize == 72 ? 2 : vertexSize == 56 ? 1 : 0);
            for (int i = 0; i < 10; i++) writer.Write(0f);
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)2);
            foreach (var vertex in vertices)
            {
                Write(writer, vertex.Position);
                writer.Write(vertex.Bone);
                writer.Write(new byte[3]);
                Write(writer, vertex.Weights);
                Write(writer, vertex.Normal);
                writer.Write(vertex.Uv.X);
                writer.Write(vertex.Uv.Y);
                if (vertexSize >= 56) writer.Write(vertex.Color);
                if (vertexSize == 72) Write(writer, vertex.Tangent);
            }
        }
        stream.Position = 0;
        return SkinnedMesh.ReadFromSimpleSkin(stream, leaveOpen: true);
    }

    private static void Write(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private static void Write(BinaryWriter writer, Vector4 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
        writer.Write(value.W);
    }
}
