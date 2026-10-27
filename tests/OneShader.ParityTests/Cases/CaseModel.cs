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

    /// <summary>Four 16-bit floats per texel.</summary>
    Rgba16F,

    /// <summary>32-bit depth (the red channel of a generator); sampled with depth comparison or read back from a depth attachment.</summary>
    Depth32F,
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

    /// <summary>Position inside a descriptor array: every resource declared at one (set, binding) forms an array, indexed by this.</summary>
    public int Element { get; init; }

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

    /// <summary>Mip levels of a texture; each level is generated at its own size.</summary>
    public int MipLevels { get; init; } = 1;

    /// <summary>Six-layer texture viewed as a cube map.</summary>
    public bool Cube { get; init; }

    /// <summary>Sampler: trilinear filtering across mip levels.</summary>
    public bool MipFilter { get; init; }

    /// <summary>Sampler: depth-comparison sampler (less-or-equal).</summary>
    public bool Compare { get; init; }

    /// <summary>Storage buffer outputs: compare the 32-bit words as a sorted multiset (for lists filled by atomics, whose order varies).</summary>
    public bool Sorted { get; init; }

    /// <summary>Storage buffer outputs of 32-bit floats: largest <c>|a-b| / (1+|a|)</c> accepted when a comparison allows float tolerance. Zero means bit-exact.</summary>
    public double FloatTolerance { get; init; }

    /// <summary>Storage buffer outputs of 16-bit floats: like <see cref="FloatTolerance"/>, per 16-bit value (WebGPU may round half arithmetic differently).</summary>
    public double HalfTolerance { get; init; }

    /// <summary>Key identifying this resource in run outputs.</summary>
    public string OutputKey =>
        (Kind is ResourceKind.StorageImage ? "image:" : "buffer:") + Set + "." + Binding + (Element > 0 ? "[" + Element + "]" : "");
}

/// <summary>One vertex attribute.</summary>
public sealed record VertexInputAttr(int Location, VertexFormat Format, int Offset, int Binding);

/// <summary>One vertex buffer binding with its data expression; with <c>PerInstance</c> the attributes advance once per instance.</summary>
public sealed record VertexBufferDecl(int Binding, int Stride, string Data, bool PerInstance = false);

/// <summary>Triangle culling for a draw.</summary>
public enum CullMode
{
    None,
    Back,
    Front,
}

/// <summary>A depth attachment: cleared, optionally tested against, and optionally read back as an R32F image.</summary>
public sealed record DepthDecl(float Clear, bool Test, bool Output);

/// <summary>An offscreen draw into a colour target (RGBA8 unless the case says otherwise), with an optional depth attachment.</summary>
public sealed class GraphicsDecl
{
    /// <summary>Format of the colour target.</summary>
    public PixelFormat TargetFormat { get; init; } = PixelFormat.Rgba8;

    public DepthDecl? Depth { get; init; }
    public CullMode Cull { get; init; }


    public string VertexEntry { get; init; } = "";

    /// <summary>Pixel entry point; empty for a depth-only draw.</summary>
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

    /// <summary>Float images (targets and storage images): largest allowed <c>|a-b| / (1+|a|)</c> between two compilers. Zero means bit-exact.</summary>
    public double FloatTolerance { get; set; }
    public List<ResourceDecl> Resources { get; } = new();

    /// <summary>Data expression of the push-constant block (see <see cref="DataGen"/>); null when the shader has none.</summary>
    public string? Push { get; set; }
    public List<ExpectDecl> Expects { get; } = new();

    /// <summary>
    /// True when the case compares values that Vulkan drivers may compute at reduced precision because the SPIR-V marks them RelaxedPrecision
    /// (min-precision types without 16-bit types). Metal and WebGPU compute them in 32 bits, so only the SPIR-V output can match bit for bit.
    /// </summary>
    public bool RelaxedPrecision { get; set; }

    public bool IsCompute => Dispatches.Count > 0;

    public override string ToString() => Name;
}
