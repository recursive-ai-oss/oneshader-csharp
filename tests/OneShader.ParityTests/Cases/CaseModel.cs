using System.Collections.Generic;

namespace OneShader.ParityTests;

/// <summary>Pipeline stage of a shader entry point.</summary>
public enum ShaderStage
{
    Vertex,
    Pixel,
    Compute,
}

/// <summary>Kind of a descriptor-bound resource a case provides.</summary>
public enum ResourceKind
{
    Uniform,
    Storage,
    Texture,
    Sampler,
    StorageImage,
}

/// <summary>Texel formats the harness can create, upload and read back.</summary>
public enum PixelFormat
{
    R8,
    Rgba8,
    R32F,
    Rgba32F,

    /// <summary>One unsigned 32-bit integer per texel (the red channel of a generator, rounded down).</summary>
    R32UInt,
}

/// <summary>Vertex attribute formats a case can declare.</summary>
public enum VertexFormat
{
    Float1,
    Float2,
    Float3,
    Float4,
    Unorm8x4,
    Word,
    Uint4,
}

/// <summary>How the colour output is blended into the target.</summary>
public enum BlendMode
{
    None,
    Premultiplied,
    Alpha,
}

/// <summary>One resource bound at (set, binding).</summary>
public sealed class ResourceDecl
{
    public ResourceKind Kind { get; init; }
    public int Set { get; init; }
    public int Binding { get; init; }

    /// <summary>Data expression for buffers (see <see cref="DataGen"/>); null means zeros.</summary>
    public string? Data { get; init; }

    /// <summary>Minimum buffer size in bytes (zero-filled beyond the data).</summary>
    public int SizeBytes { get; init; }

    /// <summary>Whether the contents are read back and compared after the run.</summary>
    public bool Output { get; init; }

    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Array layers; 0 means a plain 2D image.</summary>
    public int Layers { get; init; }
    public PixelFormat Format { get; init; }
    public string Generator { get; init; } = "";
    public bool Linear { get; init; }
    public bool Repeat { get; init; }

    /// <summary>Key identifying this resource in run outputs.</summary>
    public string OutputKey =>
        (Kind is ResourceKind.StorageImage ? "image:" : "buffer:") + Set + "." + Binding;
}

/// <summary>One vertex attribute.</summary>
public sealed record VertexInputAttr(int Location, VertexFormat Format, int Offset, int Binding);

/// <summary>One vertex buffer binding with its data expression; with <c>PerInstance</c> the attributes advance once per instance.</summary>
public sealed record VertexBufferDecl(int Binding, int Stride, string Data, bool PerInstance = false);

/// <summary>An offscreen draw into an RGBA8 target.</summary>
public sealed class GraphicsDecl
{
    public string VertexEntry { get; init; } = "";
    public string PixelEntry { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Generator for the target's initial contents; empty means transparent black.</summary>
    public string Background { get; init; } = "";
    public BlendMode Blend { get; init; }

    /// <summary>Largest allowed per-channel difference between two renders, in 8-bit steps.</summary>
    public int Tolerance { get; init; }
    public List<VertexBufferDecl> VertexBuffers { get; } = new();
    public List<VertexInputAttr> Attributes { get; } = new();

    /// <summary>Index data expression; null draws non-indexed.</summary>
    public string? IndexData { get; init; }
    public bool IndexIs16Bit { get; init; }
    public int Count { get; init; }
    public int Instances { get; init; } = 1;
}

/// <summary>One compute dispatch.</summary>
public sealed record DispatchDecl(string Entry, int X, int Y, int Z);

/// <summary>A reference computed on the CPU that GPU outputs are checked against.</summary>
public sealed record ExpectDecl(string Name);

/// <summary>A parsed case description.</summary>
public sealed class CaseModel
{
    public string Name { get; init; } = "";

    /// <summary>Shader path relative to the corpus directory.</summary>
    public string Shader { get; init; } = "";
    public List<KeyValuePair<string, string>> Defines { get; } = new();
    public GraphicsDecl? Graphics { get; set; }
    public List<DispatchDecl> Dispatches { get; } = new();
    public List<ResourceDecl> Resources { get; } = new();
    public List<ExpectDecl> Expects { get; } = new();

    public bool IsCompute => Dispatches.Count > 0;

    public override string ToString() => Name;
}
