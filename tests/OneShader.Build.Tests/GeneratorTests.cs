using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using OneShader.Reflection;
using Xunit;

namespace OneShader.Build.Tests;

public class GeneratorTests
{
    private const string Probe = """
        namespace Golden.Probe;

        public static class SpriteProbe
        {
            public static byte[] SpirV(string entry) => entry == "VSMain" ? PrebuiltSample.Shaders.Sprite.VSMain.SpirV.ToArray() : PrebuiltSample.Shaders.Sprite.PSMain.SpirV.ToArray();
            public static global::OneShader.VertexAttribute[] Inputs() => PrebuiltSample.Shaders.Sprite.VSMain.Inputs.ToArray();
            public static global::OneShader.ResourceBinding[] Bindings() => PrebuiltSample.Shaders.Sprite.Bindings.All.ToArray();
            public static uint[] Numbers() => new uint[]
            {
                PrebuiltSample.Shaders.Sprite.Frame.Size,
                PrebuiltSample.Shaders.Sprite.VSMain.Stride,
                PrebuiltSample.Shaders.Sprite.Bindings.Frame.Set, PrebuiltSample.Shaders.Sprite.Bindings.Frame.Binding, PrebuiltSample.Shaders.Sprite.Bindings.Frame.Count,
                PrebuiltSample.Shaders.Sprite.Bindings.Atlas.Binding,
                PrebuiltSample.Shaders.Sprite.Bindings.AtlasSampler.Binding,
            };
            public static global::OneShader.ShaderStage[] Stages() => new[] { PrebuiltSample.Shaders.Sprite.VSMain.Stage, PrebuiltSample.Shaders.Sprite.PSMain.Stage };
            public static global::OneShader.ResourceKind[] Kinds() => new[] { PrebuiltSample.Shaders.Sprite.Bindings.Frame.Kind, PrebuiltSample.Shaders.Sprite.Bindings.Atlas.Kind, PrebuiltSample.Shaders.Sprite.Bindings.AtlasSampler.Kind };
            public static string[] Names() => new[] { PrebuiltSample.Shaders.Sprite.VSMain.EntryPoint, PrebuiltSample.Shaders.Sprite.PSMain.EntryPoint };
            public static uint[] Compute() => new uint[]
            {
                PrebuiltSample.Shaders.Particles.CSMain.ThreadGroupSizeX, PrebuiltSample.Shaders.Particles.CSMain.ThreadGroupSizeY, PrebuiltSample.Shaders.Particles.CSMain.ThreadGroupSizeZ,
                PrebuiltSample.Shaders.Particles.Bindings.LightList.Set, PrebuiltSample.Shaders.Particles.Bindings.LightList.Binding,
            };
        }
        """;

    private static List<string> GenerateGolden(string name)
    {
        var (sources, diagnostics) = Harness.Generate(("obj/oneshader/Shaders/" + name + ".hlsl.oneshader", GoldenModels.FileText(name)));
        Assert.Empty(diagnostics);
        Assert.Single(sources);
        return [sources[0].Text];
    }

    [Theory]
    [InlineData("Sprite")]
    [InlineData("Particles")]
    [InlineData("Matrices")]
    public void InputFileMatchesTheWriter(string name)
    {
        // The generator must parse what the real writer emits; the checked-in file is the writer's output.
        Harness.AssertGolden(name + ".oneshader", GoldenModels.FileText(name));
        string sample = Path.Combine(Harness.RepoRoot, "samples", "Prebuilt", name + ".oneshader");
        Assert.True(File.Exists(sample), "sample is missing " + name + ".oneshader");
        Assert.Equal(File.ReadAllText(Harness.Golden(name + ".oneshader")), File.ReadAllText(sample));
    }

    [Theory]
    [InlineData("Sprite")]
    [InlineData("Particles")]
    [InlineData("Matrices")]
    public void GeneratedSourceMatchesGolden(string name)
    {
        Harness.AssertGolden(name + ".g.cs", GenerateGolden(name)[0]);
    }

    [Fact]
    public void GenerationIsDeterministicAndIndependentOfFileOrder()
    {
        var files = GoldenModels.Names.Select(n => ("obj/" + n + ".oneshader", GoldenModels.FileText(n))).ToArray();
        var a = Harness.Generate(files).Sources;
        var b = Harness.Generate(files.Reverse().ToArray()).Sources;
        Assert.Equal(3, a.Count);
        Assert.Equal(a.OrderBy(s => s.Name, StringComparer.Ordinal).ToList(), b.OrderBy(s => s.Name, StringComparer.Ordinal).ToList());
    }

    private static Assembly CompileAll(out System.Runtime.Loader.AssemblyLoadContext context)
    {
        var files = GoldenModels.Names.Select(n => ("obj/" + n + ".oneshader", GoldenModels.FileText(n))).ToArray();
        var (sources, diagnostics) = Harness.Generate(files);
        Assert.Empty(diagnostics);
        return Harness.Compile(sources.Select(s => s.Text).Append(Probe), out context);
    }

    [Fact]
    public void SpriteExposesTheDocumentedShape()
    {
        var asm = CompileAll(out var ctx);
        try
        {
            var probe = asm.GetType("Golden.Probe.SpriteProbe")!;
            T Call<T>(string method, params object[] args) => (T)probe.GetMethod(method)!.Invoke(null, args)!;

            var (reflection, blobs) = GoldenModels.Build("Sprite");
            var frame = reflection.Resources[0].Layout!;
            Assert.Equal([frame.Size, 32u, 0u, 0u, 1u, 1u, 2u], Call<uint[]>("Numbers"));
            Assert.Equal([ShaderStage.Vertex, ShaderStage.Pixel], Call<ShaderStage[]>("Stages"));
            Assert.Equal([ResourceKind.ConstantBuffer, ResourceKind.SampledTexture, ResourceKind.Sampler], Call<ResourceKind[]>("Kinds"));
            Assert.Equal(["VSMain", "PSMain"], Call<string[]>("Names"));
            Assert.Equal(blobs[0].ToArray(), Call<byte[]>("SpirV", "VSMain"));
            Assert.Equal(blobs[1].ToArray(), Call<byte[]>("SpirV", "PSMain"));
            Assert.Equal(
                [new VertexAttribute(0, VertexFormat.Float32x2, 0), new VertexAttribute(1, VertexFormat.Float32x2, 8), new VertexAttribute(2, VertexFormat.Float32x4, 16)],
                Call<VertexAttribute[]>("Inputs"));
            Assert.Equal(
                [
                    new ResourceBinding("Frame", 0, 0, 1, ResourceKind.ConstantBuffer, ShaderStages.Vertex),
                    new ResourceBinding("Atlas", 0, 1, 1, ResourceKind.SampledTexture, ShaderStages.Pixel),
                    new ResourceBinding("AtlasSampler", 0, 2, 1, ResourceKind.Sampler, ShaderStages.Pixel),
                ],
                Call<ResourceBinding[]>("Bindings"));
            Assert.Equal([8u, 8u, 1u, 1u, 0u], Call<uint[]>("Compute"));

            // The vertex struct: Size = stride, fields at the reflected offsets with the documented types.
            var vertex = asm.GetType("PrebuiltSample.Shaders.Sprite+VSMain+Vertex")!;
            Assert.Equal(32, Marshal.SizeOf(vertex));
            Assert.Equal(0, (int)Marshal.OffsetOf(vertex, "Position"));
            Assert.Equal(8, (int)Marshal.OffsetOf(vertex, "Uv"));
            Assert.Equal(16, (int)Marshal.OffsetOf(vertex, "Color"));
            Assert.Equal(typeof(Vector2), vertex.GetField("Position")!.FieldType);
            Assert.Equal(typeof(Vector4), vertex.GetField("Color")!.FieldType);

            // Every count, size, stride, offset and location constant is a uint.
            foreach (var t in asm.GetTypes())
            {
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && new[] { "Size", "Set", "Binding", "Count", "Stride", "ThreadGroupSizeX" }.Contains(f.Name)))
                {
                    Assert.Equal(typeof(uint), f.FieldType);
                }
            }
        }
        finally
        {
            ctx.Unload();
        }
    }

    // ---- layout against the reflection ----

    /// <summary>Leaf fields a reflected struct should produce in C#: (name, offset) pairs, ignoring matrices.</summary>
    private static void ExpectedFields(ReflectedType t, string prefix, uint baseOffset, List<(string Name, uint Offset)> into, List<(string Name, ReflectedType Matrix, uint Offset)> matrices)
    {
        foreach (var m in t.Members)
        {
            ExpectedMember(m.Type, prefix + m.Name, baseOffset + m.Offset, into, matrices);
        }
    }

    private static void ExpectedMember(ReflectedType t, string name, uint offset, List<(string Name, uint Offset)> into, List<(string Name, ReflectedType Matrix, uint Offset)> matrices)
    {
        switch (t.Kind)
        {
            case ReflectedTypeKind.Scalar:
                into.Add((name, offset));
                break;
            case ReflectedTypeKind.Vector:
                if (t.Scalar == OneShader.Types.ScalarKind.Float || t.Rows == 1)
                {
                    into.Add((name, offset));
                }
                else
                {
                    for (uint i = 0; i < t.Rows; i++)
                    {
                        into.Add((name + "_" + "XYZW"[(int)i], offset + (i * 4)));
                    }
                }

                break;
            case ReflectedTypeKind.Matrix:
                matrices.Add((name, t, offset));
                break;
            case ReflectedTypeKind.Array:
                for (uint i = 0; i < t.ArrayLength; i++)
                {
                    ExpectedMember(t.Element!, name + "_" + i.ToString(CultureInfo.InvariantCulture), offset + (i * t.ArrayStride), into, matrices);
                }

                break;
            case ReflectedTypeKind.Struct:
                into.Add((name, offset));
                break;
            default:
                break;
        }
    }

    private static void CheckStruct(Assembly asm, string typeName, ReflectedType layout)
    {
        var type = asm.GetType(typeName) ?? throw new InvalidOperationException("no generated type " + typeName);
        Assert.Equal((int)layout.Size, Marshal.SizeOf(type));
        Assert.Equal((int)layout.Size, (int)typeof(System.Runtime.CompilerServices.Unsafe).GetMethod("SizeOf")!.MakeGenericMethod(type).Invoke(null, null)!);
        Assert.Equal(layout.Size, (uint)type.GetField("Size")!.GetRawConstantValue()!);
        var fields = new List<(string, uint)>();
        var matrices = new List<(string, ReflectedType, uint)>();
        ExpectedFields(layout, "", 0, fields, matrices);
        foreach (var (name, offset) in fields)
        {
            Assert.Equal((int)offset, (int)Marshal.OffsetOf(type, name));
        }
    }

    [Fact]
    public void StructSizesAndOffsetsEqualReflection()
    {
        var asm = CompileAll(out var ctx);
        try
        {
            foreach (var name in new[] { "Sprite", "Particles" })
            {
                foreach (var r in GoldenModels.Build(name).Reflection.Resources.Where(r => r.Kind == ResourceKind.ConstantBuffer))
                {
                    CheckStruct(asm, "PrebuiltSample.Shaders." + name + "+" + r.Name, r.Layout!);
                }
            }

            // Nested structs, element structs of structured buffers, and the same HLSL struct under two packings.
            var particles = GoldenModels.Build("Particles").Reflection;
            var parameters = particles.Resources[0].Layout!;
            var lightInCbuffer = parameters.Members.First(m => m.Name == "Lights").Type.Element!;
            var lightInBuffer = particles.Resources[2].Layout!;
            Assert.NotEqual(lightInCbuffer.Size, lightInBuffer.Size);
            CheckStruct(asm, "PrebuiltSample.Shaders.Particles+Particle", particles.Resources[1].Layout!);
            CheckStruct(asm, "PrebuiltSample.Shaders.Particles+Light", lightInBuffer);
            CheckStruct(asm, "PrebuiltSample.Shaders.Particles+Light_1", lightInCbuffer);

            // Array elements and the types of non-float vectors and bools.
            var pt = asm.GetType("PrebuiltSample.Shaders.Particles+Params")!;
            Assert.Equal(typeof(int), pt.GetField("Offset_X")!.FieldType);
            Assert.Equal(typeof(uint), pt.GetField("Flag")!.FieldType);
            Assert.Equal(typeof(Vector4), pt.GetField("Weights_1")!.FieldType);
            Assert.Equal(asm.GetType("PrebuiltSample.Shaders.Particles+Light_1"), pt.GetField("Lights_1")!.FieldType);
            Assert.Equal(typeof(uint), pt.GetField("Counts_2")!.FieldType);
            Assert.Equal(typeof(Vector3), pt.GetField("Tint")!.FieldType);
        }
        finally
        {
            ctx.Unload();
        }
    }

    [Fact]
    public void MatricesWriteTheBytesTheReflectionImplies()
    {
        var asm = CompileAll(out var ctx);
        try
        {
            var layout = GoldenModels.Build("Matrices").Reflection.Resources[0].Layout!;
            var type = asm.GetType("PrebuiltSample.Shaders.Matrices+Mats")!;
            Assert.Equal((int)layout.Size, Marshal.SizeOf(type));

            var fields = new List<(string, uint)>();
            var matrices = new List<(string Name, ReflectedType Type, uint Offset)>();
            ExpectedFields(layout, "", 0, fields, matrices);
            Assert.Equal(8, matrices.Count);

            object boxed = Activator.CreateInstance(type)!;
            var expected = new byte[layout.Size];
            int serial = 1;
            foreach (var (name, m, offset) in matrices)
            {
                // Distinct values, one per element, so a transposed or shifted element shows.
                var value = default(Matrix4x4);
                var elements = new float[4, 4];
                for (int i = 0; i < m.Rows; i++)
                {
                    for (int j = 0; j < m.Columns; j++)
                    {
                        elements[i, j] = serial++;
                        uint at = m.Packing == OneShader.Types.MatrixPacking.ColumnMajor
                            ? offset + ((uint)j * m.MatrixStride) + ((uint)i * 4)
                            : offset + ((uint)i * m.MatrixStride) + ((uint)j * 4);
                        BitConverter.TryWriteBytes(expected.AsSpan((int)at), elements[i, j]);
                    }
                }

                value.M11 = elements[0, 0]; value.M12 = elements[0, 1]; value.M13 = elements[0, 2]; value.M14 = elements[0, 3];
                value.M21 = elements[1, 0]; value.M22 = elements[1, 1]; value.M23 = elements[1, 2]; value.M24 = elements[1, 3];
                value.M31 = elements[2, 0]; value.M32 = elements[2, 1]; value.M33 = elements[2, 2]; value.M34 = elements[2, 3];
                value.M41 = elements[3, 0]; value.M42 = elements[3, 1]; value.M43 = elements[3, 2]; value.M44 = elements[3, 3];

                var field = type.GetField(name);
                var property = type.GetProperty(name);
                if (field is not null)
                {
                    field.SetValue(boxed, value);
                }
                else
                {
                    Assert.NotNull(property);
                    property.SetValue(boxed, value);
                }

                // Reading it back gives the same matrix (elements outside the HLSL shape stay zero).
                var back = (Matrix4x4)(field is not null ? field.GetValue(boxed)! : property!.GetValue(boxed)!);
                Assert.Equal(value, back);
            }

            var actual = new byte[layout.Size];
            IntPtr p = Marshal.AllocHGlobal(actual.Length);
            try
            {
                Marshal.StructureToPtr(boxed, p, false);
                Marshal.Copy(p, actual, 0, actual.Length);
            }
            finally
            {
                Marshal.FreeHGlobal(p);
            }

            Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(actual));

            // Both row-major and column-major 4x4 cases are in the set (the first two members).
            Assert.Contains(matrices, m => m.Type.Packing == OneShader.Types.MatrixPacking.RowMajor && m.Type.Rows == 4 && m.Type.Columns == 4);
            Assert.Contains(matrices, m => m.Type.Packing == OneShader.Types.MatrixPacking.ColumnMajor && m.Type.Rows == 4 && m.Type.Columns == 3);
        }
        finally
        {
            ctx.Unload();
        }
    }

    // ---- diagnostics ----

    private static (List<(string Name, string Text)> Sources, string[] Errors) GenerateFrom(string text, string name = "Sprite")
    {
        var (sources, diagnostics) = Harness.Generate(("obj/" + name + ".oneshader", text));
        return (sources, diagnostics.Select(d => d.Id + ": " + d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).ToArray());
    }

    [Fact]
    public void EntryPointNamedLikeAConstantBufferIsDiagnosed()
    {
        string text = GoldenModels.FileText("Sprite").Replace("entry name=\"PSMain\"", "entry name=\"Frame\"", StringComparison.Ordinal);
        var (sources, errors) = GenerateFrom(text);
        Assert.Empty(sources);
        string error = Assert.Single(errors);
        Assert.StartsWith("OSH0951:", error, StringComparison.Ordinal);
        Assert.Contains("Frame", error, StringComparison.Ordinal);
        Assert.Contains("Rename", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoFilesWithTheSameClassAreDiagnosed()
    {
        string a = GoldenModels.FileText("Sprite");
        var (sources, diagnostics) = Harness.Generate(("obj/a/Sprite.oneshader", a), ("obj/b/Sprite.oneshader", a));
        Assert.Single(sources);
        var d = Assert.Single(diagnostics);
        Assert.Equal("OSH0951", d.Id);
        Assert.Contains("ClassName or Namespace", d.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public void MemberNamedSizeIsDiagnosed()
    {
        string text = GoldenModels.FileText("Sprite").Replace("member name=\"ViewProjection\"", "member name=\"Size\"", StringComparison.Ordinal);
        var (_, errors) = GenerateFrom(text);
        Assert.StartsWith("OSH0952:", Assert.Single(errors), StringComparison.Ordinal);
    }

    [Fact]
    public void IntegerMatrixIsDiagnosedWithAFix()
    {
        string text = GoldenModels.FileText("Matrices").Replace("kind=Matrix scalar=Float", "kind=Matrix scalar=Int", StringComparison.Ordinal);
        var (sources, errors) = GenerateFrom(text, "Matrices");
        Assert.Empty(sources);
        Assert.NotEmpty(errors);
        Assert.All(errors, e => Assert.StartsWith("OSH0952:", e, StringComparison.Ordinal));
        Assert.Contains("only float matrices", errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public void BrokenFileIsDiagnosedNotThrown()
    {
        var (sources, errors) = GenerateFrom("not a shader file\n");
        Assert.Empty(sources);
        Assert.StartsWith("OSH0950:", Assert.Single(errors), StringComparison.Ordinal);
        (sources, errors) = GenerateFrom("oneshader-reflection 1\nresource name=\"x\"\n");
        Assert.Empty(sources);
        Assert.StartsWith("OSH0950:", Assert.Single(errors), StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpKeywordsAsHlslNamesAreEscaped()
    {
        string text = GoldenModels.FileText("Sprite")
            .Replace("name=\"ViewProjection\"", "name=\"class\"", StringComparison.Ordinal)
            .Replace("name=\"Frame\"", "name=\"event\"", StringComparison.Ordinal)
            .Replace("uses name=\"Frame\"", "uses name=\"event\"", StringComparison.Ordinal)
            .Replace("entry name=\"VSMain\"", "entry name=\"namespace\"", StringComparison.Ordinal);
        var (sources, errors) = GenerateFrom(text);
        Assert.Empty(errors);
        var asm = Harness.Compile(sources.Select(s => s.Text), out var ctx);
        try
        {
            var cb = asm.GetType("PrebuiltSample.Shaders.Sprite+event")!;
            Assert.NotNull(cb.GetProperty("class")); // a column-major matrix is a property
            Assert.NotNull(asm.GetType("PrebuiltSample.Shaders.Sprite+namespace"));
        }
        finally
        {
            ctx.Unload();
        }
    }
}
