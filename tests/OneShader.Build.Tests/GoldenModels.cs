using System;
using System.Collections.Generic;
using OneShader.Build.Tasks;
using OneShader.Reflection;
using OneShader.Types;

namespace OneShader.Build.Tests;

/// <summary>Hand-built reflection and blobs for shaders the compiler cannot produce yet, laid out with the real layout rules.</summary>
internal static class GoldenModels
{
    public const string Namespace = "PrebuiltSample.Shaders";

    public static readonly string[] Names = ["Sprite", "Particles", "Matrices"];

    public static (ShaderReflection Reflection, List<ShaderBlob> Blobs) Build(string name) => name switch
    {
        "Sprite" => Sprite(),
        "Particles" => Particles(),
        "Matrices" => Matrices(),
        _ => throw new ArgumentException("unknown model " + name, nameof(name)),
    };

    /// <summary>The text the build task writes for a model: what the generator reads.</summary>
    public static string FileText(string name)
    {
        var (reflection, blobs) = Build(name);
        return CompileShaders.Render(reflection, blobs, "/unused/" + name + ".hlsl", reflection.SourceName, Namespace, "", "0", new SortedSet<string>(StringComparer.Ordinal), [], "/unused");
    }

    private static ReflectedType Layout(HlslType t, BufferLayoutRules rules)
    {
        Xunit.Assert.True(BufferLayout.TryCompute(t, rules, null, out var layout, out string? error), error);
        return ReflectedType.FromLayout(layout!);
    }

    private static ResourceInfo Cbuffer(string name, StructType type, uint binding, ShaderStages stages) => new()
    {
        Name = name,
        Kind = ResourceKind.ConstantBuffer,
        Stages = stages,
        ResourceType = ResourceTypeKind.ConstantBuffer,
        Slots = [new TargetSlot(CompileTarget.SpirV, 0, binding)],
        Layout = Layout(type, BufferLayoutRules.ConstantBuffer),
    };

    private static ShaderBlob Blob(string entry, ShaderStage stage)
    {
        // Not real SPIR-V (the generator copies bytes without looking at them): a header-sized
        // run of words that differs per entry point.
        var data = new byte[28];
        data[0] = 0x03;
        data[1] = 0x02;
        data[2] = 0x23;
        data[3] = 0x07;
        for (int i = 4; i < data.Length; i++)
        {
            data[i] = (byte)((i * 7) + entry.Length + (int)stage);
        }

        return new ShaderBlob(entry, stage, CompileTarget.SpirV, data);
    }

    private static (ShaderReflection, List<ShaderBlob>) Sprite()
    {
        var frame = new StructType("Frame", [new StructField("ViewProjection", HlslType.Matrix(ScalarType.Float, 4, 4))]);
        var reflection = new ShaderReflection
        {
            SourceName = "Shaders/Sprite.hlsl",
            Targets = [CompileTarget.SpirV],
            Resources =
            [
                Cbuffer("Frame", frame, 0, ShaderStages.Vertex),
                new ResourceInfo
                {
                    Name = "Atlas",
                    Kind = ResourceKind.SampledTexture,
                    Stages = ShaderStages.Pixel,
                    HlslType = "Texture2D<float4>",
                    ResourceType = ResourceTypeKind.Texture2D,
                    Slots = [new TargetSlot(CompileTarget.SpirV, 0, 1)],
                },
                new ResourceInfo
                {
                    Name = "AtlasSampler",
                    Kind = ResourceKind.Sampler,
                    Stages = ShaderStages.Pixel,
                    HlslType = "SamplerState",
                    ResourceType = default, // the model has no sampler family; the generator does not read it
                    Slots = [new TargetSlot(CompileTarget.SpirV, 0, 2)],
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
                        new StageVariable { Name = "i.Position", Semantic = "POSITION", Location = 0, Scalar = ScalarKind.Float, Components = 2, Format = VertexFormat.Float32x2, Offset = 0 },
                        new StageVariable { Name = "i.Uv", Semantic = "TEXCOORD", Location = 1, Scalar = ScalarKind.Float, Components = 2, Format = VertexFormat.Float32x2, Offset = 8 },
                        new StageVariable { Name = "i.Color", Semantic = "COLOR", Location = 2, Scalar = ScalarKind.Float, Components = 4, Format = VertexFormat.Float32x4, Offset = 16 },
                    ],
                    Outputs =
                    [
                        new StageVariable { Name = "Position", Semantic = "SV_Position", BuiltIn = BuiltIn.Position, Scalar = ScalarKind.Float, Components = 4 },
                    ],
                    UsedResources = ["Frame"],
                },
                new EntryPointInfo
                {
                    Name = "PSMain",
                    Stage = ShaderStage.Pixel,
                    UsedResources = ["Atlas", "AtlasSampler"],
                },
            ],
        };
        return (reflection, [Blob("VSMain", ShaderStage.Vertex), Blob("PSMain", ShaderStage.Pixel)]);
    }

    private static (ShaderReflection, List<ShaderBlob>) Particles()
    {
        var f3 = HlslType.Vector(ScalarType.Float, 3);
        var light = new StructType("Light", [
            new StructField("Intensity", ScalarType.Float),
            new StructField("Falloff", ScalarType.Float),
        ]);
        var parameters = new StructType("Params", [
            new StructField("Count", ScalarType.UInt),
            new StructField("Scale", ScalarType.Float),
            new StructField("Offset", HlslType.Vector(ScalarType.Int, 2)),
            new StructField("Flag", ScalarType.Bool),
            new StructField("Weights", HlslType.Array(HlslType.Vector(ScalarType.Float, 4), 2)),
            new StructField("Lights", HlslType.Array(light, 2)),
            new StructField("Counts", HlslType.Array(ScalarType.UInt, 3)),
            new StructField("Tint", f3),
        ]);
        var particle = new StructType("Particle", [
            new StructField("Position", HlslType.Vector(ScalarType.Float, 2)),
            new StructField("Velocity", HlslType.Vector(ScalarType.Float, 2)),
            new StructField("Color", HlslType.Vector(ScalarType.Float, 4)),
        ]);
        var reflection = new ShaderReflection
        {
            SourceName = "Shaders/Particles.hlsl",
            Targets = [CompileTarget.SpirV],
            Resources =
            [
                Cbuffer("Params", parameters, 0, ShaderStages.Compute),
                new ResourceInfo
                {
                    Name = "Particles",
                    Kind = ResourceKind.StorageBuffer,
                    Stages = ShaderStages.Compute,
                    HlslType = "RWStructuredBuffer<Particle>",
                    ResourceType = ResourceTypeKind.RWStructuredBuffer,
                    Slots = [new TargetSlot(CompileTarget.SpirV, 0, 1)],
                    Layout = Layout(particle, BufferLayoutRules.StorageBuffer),
                },

                // The same struct under the other packing rules gets its own C# type.
                new ResourceInfo
                {
                    Name = "LightList",
                    Kind = ResourceKind.ReadOnlyStorageBuffer,
                    Stages = ShaderStages.Compute,
                    HlslType = "StructuredBuffer<Light>",
                    ResourceType = ResourceTypeKind.StructuredBuffer,
                    Slots = [new TargetSlot(CompileTarget.SpirV, 1, 0)],
                    Layout = Layout(light, BufferLayoutRules.StorageBuffer),
                },
            ],
            EntryPoints =
            [
                new EntryPointInfo
                {
                    Name = "CSMain",
                    Stage = ShaderStage.Compute,
                    WorkgroupSize = (8, 8, 1),
                    UsedResources = ["Params", "Particles", "LightList"],
                },
            ],
        };
        return (reflection, [Blob("CSMain", ShaderStage.Compute)]);
    }

    private static (ShaderReflection, List<ShaderBlob>) Matrices()
    {
        var f = ScalarType.Float;
        var mats = new StructType("Mats", [
            new StructField("ColumnMajor4x4", HlslType.Matrix(f, 4, 4)) { MatrixPacking = MatrixPacking.ColumnMajor },
            new StructField("RowMajor4x4", HlslType.Matrix(f, 4, 4)) { MatrixPacking = MatrixPacking.RowMajor },
            new StructField("ColumnMajor4x3", HlslType.Matrix(f, 4, 3)) { MatrixPacking = MatrixPacking.ColumnMajor },
            new StructField("RowMajor4x3", HlslType.Matrix(f, 4, 3)) { MatrixPacking = MatrixPacking.RowMajor },
            new StructField("Default3x3", HlslType.Matrix(f, 3, 3)),
            new StructField("RowMajor2x4", HlslType.Matrix(f, 2, 4)) { MatrixPacking = MatrixPacking.RowMajor },
            new StructField("Bones", HlslType.Array(HlslType.Matrix(f, 4, 4), 2)),
        ]);
        var reflection = new ShaderReflection
        {
            SourceName = "Shaders/Matrices.hlsl",
            Targets = [CompileTarget.SpirV],
            Resources = [Cbuffer("Mats", mats, 0, ShaderStages.Vertex)],
            EntryPoints =
            [
                new EntryPointInfo { Name = "VSMain", Stage = ShaderStage.Vertex, UsedResources = ["Mats"] },
            ],
        };
        return (reflection, [Blob("VSMain", ShaderStage.Vertex)]);
    }
}
