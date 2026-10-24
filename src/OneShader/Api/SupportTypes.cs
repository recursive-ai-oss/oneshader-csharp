using System;

namespace OneShader;

/// <summary>The pipeline stage an entry point runs in. Only these three stages are supported.</summary>
public enum ShaderStage
{
    /// <summary>Vertex shader.</summary>
    Vertex = 0,

    /// <summary>Pixel (fragment) shader.</summary>
    Pixel = 1,

    /// <summary>Compute shader.</summary>
    Compute = 2,
}

/// <summary>A set of stages, used to say which stages touch a resource.</summary>
[Flags]
public enum ShaderStages
{
    /// <summary>No stage.</summary>
    None = 0,

    /// <summary>Vertex shader.</summary>
    Vertex = 1,

    /// <summary>Pixel (fragment) shader.</summary>
    Pixel = 2,

    /// <summary>Compute shader.</summary>
    Compute = 4,
}

/// <summary>What a bound resource is, in terms of the descriptor a renderer must provide.</summary>
public enum ResourceKind
{
    /// <summary>A <c>cbuffer</c> or <c>ConstantBuffer&lt;T&gt;</c> (uniform buffer).</summary>
    ConstantBuffer = 0,

    /// <summary>A read-only texture such as <c>Texture2D</c>.</summary>
    SampledTexture = 1,

    /// <summary>A read-write texture such as <c>RWTexture2D</c>.</summary>
    StorageTexture = 2,

    /// <summary>A <c>SamplerState</c> or <c>SamplerComparisonState</c>.</summary>
    Sampler = 3,

    /// <summary>A read-only storage buffer: <c>StructuredBuffer</c> or <c>ByteAddressBuffer</c>.</summary>
    ReadOnlyStorageBuffer = 4,

    /// <summary>A read-write storage buffer: <c>RWStructuredBuffer</c> or <c>RWByteAddressBuffer</c>.</summary>
    StorageBuffer = 5,

    /// <summary>A read-only typed buffer, <c>Buffer&lt;T&gt;</c> (uniform texel buffer).</summary>
    UniformTexelBuffer = 6,

    /// <summary>A read-write typed buffer, <c>RWBuffer&lt;T&gt;</c> (storage texel buffer).</summary>
    StorageTexelBuffer = 7,
}

/// <summary>The format of one vertex attribute as a renderer declares it in a vertex layout.</summary>
public enum VertexFormat
{
    /// <summary>One 32-bit float.</summary>
    Float32 = 0,

    /// <summary>Two 32-bit floats.</summary>
    Float32x2 = 1,

    /// <summary>Three 32-bit floats.</summary>
    Float32x3 = 2,

    /// <summary>Four 32-bit floats.</summary>
    Float32x4 = 3,

    /// <summary>One signed 32-bit integer.</summary>
    Sint32 = 4,

    /// <summary>Two signed 32-bit integers.</summary>
    Sint32x2 = 5,

    /// <summary>Three signed 32-bit integers.</summary>
    Sint32x3 = 6,

    /// <summary>Four signed 32-bit integers.</summary>
    Sint32x4 = 7,

    /// <summary>One unsigned 32-bit integer.</summary>
    Uint32 = 8,

    /// <summary>Two unsigned 32-bit integers.</summary>
    Uint32x2 = 9,

    /// <summary>Three unsigned 32-bit integers.</summary>
    Uint32x3 = 10,

    /// <summary>Four unsigned 32-bit integers.</summary>
    Uint32x4 = 11,

    /// <summary>Two 16-bit floats.</summary>
    Float16x2 = 12,

    /// <summary>Four 16-bit floats.</summary>
    Float16x4 = 13,

    /// <summary>Four unsigned 8-bit values normalized to 0..1 (for example a packed colour).</summary>
    Unorm8x4 = 14,

    /// <summary>Four signed 8-bit values normalized to -1..1.</summary>
    Snorm8x4 = 15,

    /// <summary>Four unsigned 8-bit integers.</summary>
    Uint8x4 = 16,

    /// <summary>Four signed 8-bit integers.</summary>
    Sint8x4 = 17,

    /// <summary>Two unsigned 16-bit values normalized to 0..1.</summary>
    Unorm16x2 = 18,

    /// <summary>Four unsigned 16-bit values normalized to 0..1.</summary>
    Unorm16x4 = 19,

    /// <summary>Two signed 16-bit values normalized to -1..1.</summary>
    Snorm16x2 = 20,

    /// <summary>Four signed 16-bit values normalized to -1..1.</summary>
    Snorm16x4 = 21,

    /// <summary>Two unsigned 16-bit integers.</summary>
    Uint16x2 = 22,

    /// <summary>Four unsigned 16-bit integers.</summary>
    Uint16x4 = 23,

    /// <summary>Two signed 16-bit integers.</summary>
    Sint16x2 = 24,

    /// <summary>Four signed 16-bit integers.</summary>
    Sint16x4 = 25,
}

/// <summary>One vertex attribute of a vertex entry point.</summary>
/// <param name="Location">The shader input location.</param>
/// <param name="Format">The attribute format.</param>
/// <param name="Offset">Byte offset inside the vertex.</param>
public readonly record struct VertexAttribute(uint Location, VertexFormat Format, uint Offset);

/// <summary>One resource a shader file binds, with the stages that use it.</summary>
/// <param name="Name">The HLSL name.</param>
/// <param name="Set">Descriptor set (Vulkan) / bind group (WebGPU): the register space unless overridden.</param>
/// <param name="Binding">Binding number inside the set.</param>
/// <param name="Count">Array size; 1 for a single resource, 0 when unbounded.</param>
/// <param name="Kind">The descriptor kind.</param>
/// <param name="Stages">Stages whose entry points use the resource.</param>
public readonly record struct ResourceBinding(
    string Name, uint Set, uint Binding, uint Count, ResourceKind Kind, ShaderStages Stages);
