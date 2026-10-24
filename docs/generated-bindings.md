# Generated bindings

Status: draft. The shape below is what the build package generates; names may still be adjusted
until the first prerelease package, after which changes are listed in the changelog.

## Adding shaders to a project

Reference the `RecursiveAI.OneShader.Build` package. Every `*.hlsl` file in the project becomes a
`OneShader` item and is compiled during `dotnet build` (turn the default glob off with
`<EnableDefaultOneShaderItems>false</EnableDefaultOneShaderItems>` and list items yourself).

Entry points are found from `[shader("vertex")]`, `[shader("pixel")]` and `[shader("compute")]`
attributes. Item metadata can list them explicitly instead.

Errors and warnings are ordinary MSBuild diagnostics on the HLSL line:

```
Shaders/Sprite.hlsl(12,17): error OSH0204: 'Atlass' is not declared. Did you mean 'Atlas'?
```

## What is generated

One `static partial class` per HLSL file, named after the file, in the namespace
`<RootNamespace>.Shaders` (overridable). Nothing uses reflection or file I/O; output is
deterministic.

- **Constant buffers**: one blittable struct per `cbuffer` / `ConstantBuffer<T>`, named after it,
  with `[StructLayout(LayoutKind.Explicit)]`, a `uint Size` constant, and field offsets equal to the
  offsets in the compiled shader. Float vectors and matrices use `System.Numerics` types.
- **Bindings**: a nested `Bindings` class with, per resource, `const uint Set`, `Binding`,
  `Count` and `const ResourceKind Kind`, plus `All`, a span of `ResourceBinding` that also says
  which stages use each resource.
- **Entry points**: one nested class per entry point, named after the function, with
  `Stage`, `EntryPoint`, and `SpirV` (a `ReadOnlySpan<byte>` over data embedded in the assembly).
  - Vertex entry points add a blittable `Vertex` struct, `uint Stride`, and `Inputs`, a span of
    `VertexAttribute` (location, `VertexFormat`, offset).
  - Compute entry points add `ThreadGroupSizeX`, `ThreadGroupSizeY` and `ThreadGroupSizeZ`.
  - Metal and WebGPU outputs will be added as sibling members (`Msl`, `Wgsl`).

The support types (`ShaderStage`, `ResourceKind`, `VertexFormat`, `VertexAttribute`,
`ResourceBinding`) live in `RecursiveAI.OneShader`, which the build package brings in, so a
rendering layer in another assembly can accept them. When only these types are used, trimming
removes the compiler from a NativeAOT app.

## Layout rules

- One byte layout per buffer on every target: HLSL packing as used for Vulkan (relaxed std140
  for constant buffers, relaxed std430 for structured buffers); `packoffset` and
  `[[vk::offset]]` are honoured. The same C# struct is valid for every backend.
- Matrices are `column_major` by default, as in HLSL; `row_major` and `#pragma pack_matrix` are
  honoured. The generated C# member is arranged so that element (i, j) in C# is `M[i][j]` in
  HLSL whichever packing the shader uses.
- Vulkan descriptor slots: `[[vk::binding(binding, set)]]` wins. Otherwise set is the register
  space and binding is the register number. Two resources on the same slot (for example `t0` and
  `s0`) are a build error that says to add `[[vk::binding]]` or renumber.
- Vertex input locations follow declaration order unless `[[vk::location]]` is given. Formats
  follow the shader type; a packed format (for example a `unorm8x4` colour) can be declared in
  the shader source.

## Example

```hlsl
// Shaders/Sprite.hlsl
cbuffer Frame : register(b0) { float4x4 ViewProjection; };
Texture2D<float4> Atlas : register(t1);
SamplerState AtlasSampler : register(s2);

struct VSIn  { float2 Position : POSITION; float2 Uv : TEXCOORD0; float4 Color : COLOR0; };
struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; float4 Color : COLOR0; };

[shader("vertex")]
VSOut VSMain(VSIn i)
{
    VSOut o;
    o.Position = mul(ViewProjection, float4(i.Position, 0, 1));
    o.Uv = i.Uv;
    o.Color = i.Color;
    return o;
}

[shader("pixel")]
float4 PSMain(VSOut i) : SV_Target { return Atlas.Sample(AtlasSampler, i.Uv) * i.Color; }
```

generates, in outline:

```csharp
namespace MyGame.Shaders;

public static partial class Sprite
{
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct Frame { public const uint Size = 64; /* ViewProjection at offset 0 */ }

    public static class Bindings
    {
        public static class Frame        { public const uint Set = 0, Binding = 0, Count = 1; public const ResourceKind Kind = ResourceKind.ConstantBuffer; }
        public static class Atlas        { public const uint Set = 0, Binding = 1, Count = 1; public const ResourceKind Kind = ResourceKind.SampledTexture; }
        public static class AtlasSampler { public const uint Set = 0, Binding = 2, Count = 1; public const ResourceKind Kind = ResourceKind.Sampler; }
        public static ReadOnlySpan<ResourceBinding> All => /* ... */;
    }

    public static class VSMain
    {
        public const ShaderStage Stage = ShaderStage.Vertex;
        public const string EntryPoint = "VSMain";
        public static ReadOnlySpan<byte> SpirV => /* embedded */;

        [StructLayout(LayoutKind.Explicit, Size = 32)]
        public struct Vertex { /* Position @0 Vector2, Uv @8 Vector2, Color @16 Vector4 */ }
        public const uint Stride = 32;
        public static ReadOnlySpan<VertexAttribute> Inputs => /* (0, Float32x2, 0), (1, Float32x2, 8), (2, Float32x4, 16) */;
    }

    public static class PSMain
    {
        public const ShaderStage Stage = ShaderStage.Pixel;
        public const string EntryPoint = "PSMain";
        public static ReadOnlySpan<byte> SpirV => /* embedded */;
    }
}
```
