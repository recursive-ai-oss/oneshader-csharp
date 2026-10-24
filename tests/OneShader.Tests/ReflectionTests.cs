using System.Collections.Generic;
using OneShader.Reflection;
using OneShader.Types;
using Xunit;

namespace OneShader.Tests;

public class ReflectionTests
{
    internal static ShaderReflection Sample()
    {
        var frame = new StructType("Frame", [
            new StructField("ViewProjection", HlslType.Matrix(ScalarType.Float, 4, 4)),
            new StructField("Tint", HlslType.Vector(ScalarType.Float, 4)),
            new StructField("Weights", HlslType.Array(ScalarType.Float, 3)),
        ]);
        Assert.True(BufferLayout.TryCompute(frame, BufferLayoutRules.ConstantBuffer, null, out var layout, out _));
        return new ShaderReflection
        {
            SourceName = "Shaders/Sprite \"quoted\".hlsl",
            Targets = [CompileTarget.SpirV, CompileTarget.Wgsl],
            Resources =
            [
                new ResourceInfo
                {
                    Name = "Frame",
                    Kind = ResourceKind.ConstantBuffer,
                    Stages = ShaderStages.Vertex | ShaderStages.Pixel,
                    ResourceType = ResourceTypeKind.ConstantBuffer,
                    Slots = [new TargetSlot(CompileTarget.SpirV, 0, 0), new TargetSlot(CompileTarget.Wgsl, 0, 0)],
                    Layout = ReflectedType.FromLayout(layout!),
                },
                new ResourceInfo
                {
                    Name = "Atlas",
                    Kind = ResourceKind.SampledTexture,
                    Count = 1,
                    Stages = ShaderStages.Pixel,
                    HlslType = "Texture2D<float4>",
                    ResourceType = ResourceTypeKind.Texture2D,
                    Slots = [new TargetSlot(CompileTarget.SpirV, 0, 1), new TargetSlot(CompileTarget.Wgsl, 0, 1)],
                    Layout = new ReflectedType { Kind = ReflectedTypeKind.Vector, Scalar = ScalarKind.Float, Rows = 4, Size = 16, Alignment = 16 },
                },
            ],
            EntryPoints =
            [
                new EntryPointInfo
                {
                    Name = "VSMain",
                    Stage = ShaderStage.Vertex,
                    VertexStride = 32,
                    Inputs =
                    [
                        new StageVariable { Name = "Position", Semantic = "POSITION", Location = 0, Scalar = ScalarKind.Float, Components = 2, Format = VertexFormat.Float32x2, Offset = 0 },
                        new StageVariable { Name = "Color", Semantic = "COLOR", SemanticIndex = 0, Location = 2, Scalar = ScalarKind.Float, Components = 4, Format = VertexFormat.Unorm8x4, Offset = 16 },
                    ],
                    Outputs =
                    [
                        new StageVariable { Name = "Position", Semantic = "SV_Position", BuiltIn = BuiltIn.Position, Scalar = ScalarKind.Float, Components = 4 },
                        new StageVariable { Name = "Uv", Semantic = "TEXCOORD", SemanticIndex = 1, Location = 0, Scalar = ScalarKind.Float, Components = 2, Interpolation = Interpolation.NoPerspective },
                    ],
                    UsedResources = ["Frame"],
                },
                new EntryPointInfo { Name = "CSMain", Stage = ShaderStage.Compute, WorkgroupSize = (8, 8, 1) },
            ],
        };
    }

    [Fact]
    public void RoundTripsAndIsStable()
    {
        var model = Sample();
        string text = ReflectionFormat.Write(model);
        Assert.True(ReflectionFormat.TryRead(text, out var back, out var error), error);
        Assert.True(ReflectionFormat.AreEqual(model, back!));
        Assert.Equal(text, ReflectionFormat.Write(back!));
        Assert.Equal(ReflectionFormat.WriteBytes(model), ReflectionFormat.WriteBytes(Sample()));

        // Spot checks on the parsed model, not only on text equality.
        Assert.Equal(model.SourceName, back!.SourceName);
        Assert.Equal(64u, back.Resources[0].Layout!.Members[0].Type.Size);
        Assert.Equal(80u, back.Resources[0].Layout!.Members[2].Offset);
        Assert.Equal((8u, 8u, 1u), back.EntryPoints[1].WorkgroupSize);
        Assert.Null(back.EntryPoints[0].Outputs[0].Location);
        Assert.Equal(VertexFormat.Unorm8x4, back.EntryPoints[0].Inputs[1].Format);
        Assert.Equal(Interpolation.NoPerspective, back.EntryPoints[0].Outputs[1].Interpolation);
    }

    [Fact]
    public void RejectsMalformedInputWithoutThrowing()
    {
        foreach (var bad in new[] { "", "garbage", "oneshader-reflection 99\n", "oneshader-reflection 1\nresource name=\"x\n", "oneshader-reflection 1\nresource name=\"x\" kind=Nope\n", "oneshader-reflection 1\n   odd\n" })
        {
            Assert.False(ReflectionFormat.TryRead(bad, out var m, out var error));
            Assert.Null(m);
            Assert.False(string.IsNullOrEmpty(error));
        }
    }

    [Fact]
    public void LayoutMatchesBufferRules()
    {
        var model = Sample();
        var members = model.Resources[0].Layout!.Members;
        Assert.Equal(0u, members[0].Offset);
        Assert.Equal(64u, members[1].Offset);
        Assert.Equal(80u, members[2].Offset);
        Assert.Equal(16u, model.Resources[0].Layout!.Members[2].Type.ArrayStride);
        Assert.Equal(128u, model.Resources[0].Layout!.Size);
    }

    [Fact]
    public void CompileReturnsReflectionAndNoBlobsYet()
    {
        var r = ShaderCompiler.Compile("[shader(\"pixel\")] float4 main() : SV_Target { return 0; }", "a.hlsl", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(r.Success);
        Assert.Equal("main", Assert.Single(r.Reflection!.EntryPoints).Name);
        Assert.Empty(r.Blobs);
    }

    [Fact]
    public void CompileReportsErrorsAsDiagnosticsNotExceptions()
    {
        var r = ShaderCompiler.Compile("float4 main( : SV_Target { return 0; }", "a.hlsl", cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(r.Success);
        Assert.Null(r.Reflection);
        Assert.StartsWith("a.hlsl(1,", r.Diagnostics[0].ToString());
    }
}
