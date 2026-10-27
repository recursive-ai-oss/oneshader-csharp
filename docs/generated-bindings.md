# Generated bindings

The shape below is what the build package generates. Changes to it are listed in the changelog.

## Adding shaders to a project

Reference the `RecursiveAI.OneShader.Build` package. Every `*.hlsl` file in the project becomes a
`OneShader` item and is compiled during `dotnet build` (turn the default glob off with
`<EnableDefaultOneShaderItems>false</EnableDefaultOneShaderItems>` and list items yourself).

```xml
<ItemGroup>
  <PackageReference Include="RecursiveAI.OneShader.Build" Version="..." />
</ItemGroup>
```

The package brings `RecursiveAI.OneShader` (the support types) with it. Build with `dotnet build`: the
task is a .NET task, so Visual Studio's .NET Framework MSBuild cannot run it. Until a package is
published, `tools/pack-local.sh` packs both packages into a local folder (`.feed` by default);
`samples/` has projects that restore from it, and one (`samples/LocalSource`) that references the
repository's projects directly. Only projects that contain shaders reference the build package;
it does not flow to their consumers.

Entry points are found from `[shader("vertex")]`, `[shader("pixel")]` and `[shader("compute")]`
attributes. Item metadata can list them explicitly instead.

Item metadata, all optional: `EntryPoints="VSMain:Vertex;PSMain:Pixel"`, `Defines="A;B=2"`,
`IncludeDirectories="dir;dir"` (searched after the including file's folder), `Namespace`, `ClassName`,
`Targets`, and for variants `Axes` and `MaxVariants` (see Variants below).
Project properties: `OneShaderNamespace` (default `$(RootNamespace).Shaders`), `OneShaderDefines`,
`OneShaderIncludeDirectories`, `OneShaderMaxVariants`. Listing a file as a `OneShader` item (to give it metadata) is
fine: the default glob skips files that are already listed. A shader is compiled again only when it, a file it includes, the
options or the compiler changed.

Errors and warnings are ordinary MSBuild diagnostics on the HLSL line:

```
Shaders/Sprite.hlsl(12,17): error OSH0204: 'Atlass' is not declared. Did you mean 'Atlas'?
```

## What is generated

One `static partial class` per HLSL file, named after the file (see Names below), in the namespace
`<RootNamespace>.Shaders` (overridable). Nothing uses reflection or file I/O; output is
deterministic.

- **Constant buffers**: one blittable struct per `cbuffer` / `ConstantBuffer<T>`, named after it,
  with `[StructLayout(LayoutKind.Explicit)]`, a `uint Size` constant, and field offsets equal to the
  offsets in the compiled shader. Float vectors and matrices use `System.Numerics` types.
- **Bindings**: a nested `Bindings` class with, per resource, `const uint Set`, `Binding`,
  `Count` and `const ResourceKind Kind`, plus `All`, a span of `ResourceBinding` that also says
  which stages use each resource. See Resource arrays and Append and consume buffers below.
- **Entry points**: one nested class per entry point, named after the function, with
  `Stage`, `EntryPoint`, and `SpirV` (a `ReadOnlySpan<byte>` over data embedded in the assembly).
  - Vertex entry points add a blittable `Vertex` struct, `uint Stride`, and `Inputs`, a span of
    `VertexAttribute` (location, `VertexFormat`, offset).
  - Compute entry points add `ThreadGroupSizeX`, `ThreadGroupSizeY` and `ThreadGroupSizeZ`.
  - Metal and WebGPU outputs will be added as sibling members (`Msl`, `Wgsl`).
- **Reflection**: the file's class has `static ShaderReflection Reflection`, the reflection the rest of the class
  was generated from (resources with slots and layouts, entry points, vertex inputs, thread-group sizes). It is stored
  as text in the assembly (`ReadOnlySpan<byte> ReflectionData`, about 1 to 4 KB per shader) and parsed on the first
  read, so a program that never reads it carries no parser after trimming. Hot reload compares it with a fresh
  compile; see "Hot reload" in the `RecursiveAI.OneShader` package README.
- All counts, sizes, strides, offsets, locations and thread-group sizes are `uint`, matching
  the Vulkan API.

### Push constants

A struct-typed global or `ConstantBuffer<T>` marked `[[vk::push_constant]]` is a push-constant block, not a descriptor:

```hlsl
struct PerDraw { float4x4 Model; float4 Tint; uint Index; };
[[vk::push_constant]] ConstantBuffer<PerDraw> Draw;   // or: [[vk::push_constant]] PerDraw Draw;
```

The generated class gets a blittable `PushConstants` struct (with `Size` and the SPIR-V member offsets) and
`Bindings.PushConstants` with the delivery information; the block is not in `Bindings.All`.

```csharp
public static class PushConstants { public const uint Size = 96u; public const ShaderStages Stages = ShaderStages.Vertex | ShaderStages.Pixel;
    public const uint MslIndex = 0u;                         // when Msl is selected
    public const uint WgslGroup = 0u, WgslBinding = 2u; }    // when Wgsl is selected
```

- **Vulkan:** `vkCmdPushConstants(cmd, layout, (VkShaderStageFlags)Bindings.PushConstants.Stages, 0, Size, &data)`; the
  pipeline layout's range is offset 0, `Size` bytes, those stages. Vulkan only guarantees 128 bytes; a larger block is warning OSH0334.
- **Metal:** `setVertexBytes` / `setFragmentBytes` (or `setBytes` for compute) with the struct's bytes at `MslIndex`.
- **WebGPU:** a uniform buffer at `@group(WgslGroup) @binding(WgslBinding)`, at least `Size` rounded up to 16 bytes long.
  The binding is the one after the highest binding of the highest bind group the shader uses, so it never collides.
- Members follow the vector-relaxed std430 rules DXC uses for push constants (a `float3` may share a 16-byte row with
  the next scalar), not the constant-buffer rules; the same bytes are valid on every target.
- A file may declare several blocks as long as no entry point reads more than one (OSH0333). With one block the struct and
  the `Bindings` class are named `PushConstants`; with several, each is named after its variable, `<Name>PushConstants`.

### Resource arrays

`Texture2D<float4> Tex[4]`, `SamplerState Smp[2]`, arrays of structured buffers and `ConstantBuffer<T> Cb[2]` are
bounded arrays of resources. An array is **one** descriptor binding with a descriptor count, as in DXC's
SPIR-V (the next unnumbered resource gets the next binding number, not `Binding + Count`).

```csharp
public static class Tex { public const uint Set = 0, Binding = 0, Count = 4; public const ResourceKind Kind = ResourceKind.SampledTexture; }
// All contains: new ResourceBinding("Tex", 0, 0, 4, ResourceKind.SampledTexture, ShaderStages.Compute)
```

`Count` is 1 for a single resource. The shader may index an array with a constant or a run-time value (pass the
elements to helper functions too); `NonUniformResourceIndex(i)` marks an index that differs between invocations.
Run-time indices need the Vulkan device features `shaderSampledImageArrayDynamicIndexing`,
`shaderStorageBufferArrayDynamicIndexing`, `shaderUniformBufferArrayDynamicIndexing` and
`shaderStorageImageArrayDynamicIndexing` (the compiled shader declares the matching capability for the kinds it
indexes), and `shaderSampledImageArrayNonUniformIndexing` / `shaderStorageBufferArrayNonUniformIndexing` for
non-uniform indices. Unbounded arrays (`Tex[]`) are an error (`OSH0350`); multi-dimensional resource arrays are
an error too.

### Append and consume buffers

`AppendStructuredBuffer<T>` and `ConsumeStructuredBuffer<T>` take two descriptors: the element buffer and a hidden
4-byte counter buffer in the **same set**. The counter holds the number of elements: `Append` adds one and stores at
the old value, `Consume` subtracts one and reads the element below the old value. `GetDimensions` reports the
element buffer's capacity and stride, not the counter. The application owns both buffers: bind a 4-byte (or
larger) storage buffer at the counter binding, zero it to start an append list empty, and write the element count
into it to start a consume list full.

The counter binding is `[[vk::counter_binding(n)]]` if given; otherwise it is the next free binding of the buffer's
set after the resources declared so far (the same numbering DXC uses), so `AppendStructuredBuffer<T> A : register(u0)`
next to `B : register(u1)` puts `A`'s counter at 2 and `B`'s at 3 if those are free.

```csharp
public static class Hits
{
    public const uint Set = 0, Binding = 2, Count = 1;
    public const ResourceKind Kind = ResourceKind.StorageBuffer;
    public const uint CounterBinding = 3;   // same Set
}
// All contains the buffer and, right after it, its counter:
//   new ResourceBinding("Hits", 0, 2, 1, ResourceKind.StorageBuffer, ShaderStages.Compute)
//   new ResourceBinding("Hits.counter", 0, 3, 1, ResourceKind.StorageBuffer, ShaderStages.Compute)
```

A resource without a counter has no `CounterBinding` constant. Arrays of append or consume buffers are an error.
`RWStructuredBuffer.IncrementCounter` and `DecrementCounter` are not supported yet.

The names `Reflection` and `ReflectionData` are taken in the file's class, so a constant buffer, struct or entry point
with one of those names is a name-clash error.

Member types in generated structs:

- `float`, `int`, `uint` and the other scalars map to the C# type of the same width; `bool` is a `uint`
  (HLSL stores it as a 32-bit word).
- `float2/3/4` are `Vector2/3/4`. Vectors of other element types have no `System.Numerics` type and
  become one field per component (`Offset_X`, `Offset_Y`).
- Arrays become one member per element (`Weights_0`, `Weights_1`), so the struct needs no unsafe code.
- Nested HLSL structs become nested C# structs with the same rules. If one HLSL struct appears under
  two packings (a constant buffer and a structured buffer), the second gets a `_1` suffix.
- Matrices are `Matrix4x4` members, for every size up to 4x4 (elements outside the HLSL shape stay
  zero). A row-major 4x4 is a plain field; the other shapes are properties over private storage that
  does the arranging, so element (i, j) is `M[i][j]` either way.
- Each `StructuredBuffer<T>` / `RWStructuredBuffer<T>` whose `T` is a struct also gets a C# struct,
  named after `T`, with `Size` equal to the element stride.
- Vertex fields follow the format: `Float32x2` is a `Vector2`; packed formats (`Unorm8x4` and the
  like) are a `uint` or `ulong` holding the raw bits; integer and half vectors are one field per
  component.

The support types (`ShaderStage`, `ResourceKind`, `VertexFormat`, `VertexAttribute`,
`ResourceBinding`) live in `RecursiveAI.OneShader`, which the build package brings in, so a
rendering layer in another assembly can accept them. When only these types are used, trimming
removes the compiler from a NativeAOT app.

## Specialization constants

A file that declares `[[vk::constant_id(N)]] const T Name = default;` constants gets, next to the bindings:

```csharp
public static class SpecializationConstants { public static class Rounds { public const uint Id = 1u; public const int Default = 4; } /* ... */ }
public struct Specialization { public uint UseOffset; public int Rounds; public uint Mask; public float Scale;   // 4 bytes each, declaration order; a bool is 0 or 1
    public static Specialization Defaults { get; }
    public SpecializationInfo ToVulkan(ReadOnlySpan<uint> ids = default);     // map entries + data for VkSpecializationInfo
    public IReadOnlyList<FunctionConstantValue> ToMetal(ReadOnlySpan<uint> ids = default);
    public IReadOnlyDictionary<string, double> ToWebGpu(ReadOnlySpan<uint> ids = default); }   // keys: decimal ids
public static class CSMain { public static ReadOnlySpan<uint> SpecConstantIds { get; } /* ids this entry point reads */ }
```

Pass the entry point's `SpecConstantIds` to the `To...` methods so a pipeline gets only the constants its entry point reads (WebGPU rejects others; Vulkan and
Metal ignore them). `CSMain.Layout.SpecConstants` lists them with names, types and defaults, and `SpecializationValues.For(CSMain.Layout)` is the same data
driven by names (`Set("Rounds", 7)`) instead of struct fields. Entry points that read no constant have no `SpecConstantIds`. Names that would clash with a
member of `Specialization` (`Size`, `Count`, `Defaults`, ...) are reported by the generator. Metal hosts: a function with function constants must be created with
`makeFunction(name:constantValues:)` even to get the defaults (see the MSL backend notes). `CompileOptions.SpecializationOverrides` (runtime compiler API) fixes
constants at compile time instead.

In a variant file the same `SpecializationConstants` and `Specialization` types are generated inside each variant (`Variants.<Name>.Specialization`), because the set of constants can differ per variant; the aggregate `Entry.SpecConstantIds` returns the ids the looked-up variant reads.

## Variants

A `OneShader` item with `Axes="ALPHA_TEST;LIGHTING=NONE|LAMBERT|PBR"`, or `OneShaderVariant` items naming the shader,
is compiled once per define set (see the build package readme for the MSBuild side). The file's class then has this shape
instead of the single-variant members `Bindings`, `Reflection`, `Pipeline` and the entry-point classes at file level
(those move into each variant):

| Member | Meaning |
|---|---|
| `Frame`, `PushConstants`, `<Entry>Vertex`, other structs | generated once when every variant agrees on the layout |
| `<Axis>Value` | `enum` per choice axis (`LightingValue { None, Lambert, Pbr }`); flags are `bool`; named sets use `VariantName` |
| `Key` | `readonly struct`, one property per axis, `Index` (-1 for an enum value out of range), equality |
| `Variant` | `Index`, `Name`, `Defines`, `Key`, `Bindings`, `Reflection`, `Pipeline` (when any variant has one) and one `Entry` property per entry point |
| `Entry` | `Stage`, `EntryPoint`, `SpirV`, `Msl`, `MslFunctionName`, `MslVertexBufferIndex`, `Wgsl`, `WgslEntryPoint`, `Stride`, `Inputs`, `ThreadGroupSizeX/Y/Z`, `SubgroupOperations`, `RequiredFeatures`, `Layout` (each only when some variant has it) |
| `Variants` | `Count`, `All`, `Get(Key)`, `Get(int)`, `TryGet(Key, out Variant)`, and one static class per variant (`Variants.AlphaTestOn_LightingPbr`) shaped like a single-variant file's class |

Variant order is the cartesian product with the first axis changing slowest; `Key.Index` is the position in `Variants.All`.
A flag axis contributes `<Axis>On`/`<Axis>Off` to the variant class name, a choice contributes `<Axis><Value>`, joined by `_`;
all-capitals macro names and values are PascalCased (`ALPHA_TEST` gives `AlphaTest`). Lookup is an array index and the blobs are
returned through switches over constants: no allocation, no reflection, trim- and AOT-clean.

**When variants disagree about a struct.** Two variants may lay out a `cbuffer` (or push block, or vertex input) differently, for
example because a member sits under `#if`. Then no shared type is generated for that name; each variant's class declares its own
(`Variants.<Name>.Frame`, `Variants.<Name>.VSMain.Vertex`), matching that variant's reflection. Resource slots that differ between
variants are not an error: read them from `Variant.Bindings` and `Variant.Pipeline`.

## Pipeline layouts

Each entry-point class has `Layout`, an `OneShader.Layouts.EntryLayout` with the resources that entry point uses (with its own stage
only), its push-constant block, its vertex input and its thread-group size, with the slot on every selected target. The file's class has
`Pipeline` when the file has exactly one vertex and one pixel entry point, or exactly one compute entry point, and no cbuffer, struct or
entry point of that name. For other combinations, merge entry points yourself with `PipelineLayout.TryCreate`, or with
`PipelineLayout.TryCreateUnion` when one layout must serve many entry points, several per stage.

```csharp
public static class VSMain { public static EntryLayout Layout { get; } /* ... */ }
public static PipelineLayout Pipeline { get; }    // Create(VSMain.Layout, PSMain.Layout)
```

Both are built on first use from constant arrays (no reflection parsing) and cached. `Pipeline.ToVulkan()`, `TryToMetal()` and
`TryToWebGpu()` convert to the target's description; see the `RecursiveAI.OneShader` readme.

## Names

C# warns about type names made only of lower-case ASCII letters (CS8981), which is an error in
projects that treat warnings as errors. So every generated **type** name (the file's class, constant
buffers, structs, the classes under `Bindings`, entry-point classes) follows one rule:

- A name with an upper-case letter keeps its spelling: `Quad`, `Tile`, `UiRect`, `Frame`. Characters
  that cannot be in an identifier (`-`, `.`, space) become `_`: `UI-rect` gives `UI_rect`.
- Any other name is turned into PascalCase: split at `_` and at characters that cannot be in an
  identifier, upper-case the first letter of each part, join. `sprite` gives `Sprite`, `msdf_text`
  gives `MsdfText`, `ui-rect` gives `UiRect`, `vs_main` gives `VsMain`.
- A name that starts with a digit gets a `_` in front: `2d_sprite` gives `_2dSprite`.
- The `ClassName` item metadata is used exactly as written.

Strings are never changed: `EntryPoint` is still `"vs_main"`, and `Bindings.All` lists the HLSL
resource names. Members (struct fields, properties, constants) keep their HLSL spelling; a C#
keyword is escaped with `@` (`@class`). Two names that map to the same C# name, such as `ui-rect`
and `ui_rect`, are a build error that says to rename one or set `ClassName`.

## 16-bit and 64-bit members

A buffer member of a sized type is generated at its DXC offset with the matching C# type: `half` is `System.Half`, `int16_t` `short`, `uint16_t` `ushort`,
`int64_t` `long`, `uint64_t` `ulong`, `double` `double` (min-precision types without 16-bit types are `float`/`int`/`uint`). Vectors of these have no
`System.Numerics` form, so they are one field per component: `half3 b` becomes `b_X`, `b_Y`, `b_Z` (each at its own offset). A matrix of a 16-bit or 64-bit element is an
error that says to split it (only float matrices up to 4x4 are generated). Each entry class gets `RequiredFeatures` (a `DeviceFeatures` constant) beside `SubgroupOperations`,
and its `Layout` carries it; enable the Vulkan features it names (or request `shader-f16` on WebGPU) when creating the device. Turn the types on with
`<OneShaderEnable16BitTypes>true</OneShaderEnable16BitTypes>` or `Enable16BitTypes="true"` on a `OneShader` item.

## Layout rules

- One byte layout per buffer on every target: HLSL packing as used for Vulkan (relaxed std140
  for constant buffers, relaxed std430 for structured buffers); `packoffset` and
  `[[vk::offset]]` are honoured. The same C# struct is valid for every backend.
- Matrices are `column_major` by default, as in HLSL; `row_major` and `#pragma pack_matrix` are
  honoured. The generated C# member is arranged so that element (i, j) in C# is `M[i][j]` in
  HLSL whichever packing the shader uses.
- Vulkan descriptor slots: `[[vk::binding(binding, set)]]` wins. Otherwise set is the register
  space and binding is the register number. Two resources on the same slot (for example `t0` and
  `s0`) are a build error that says to add `[[vk::binding]]` or renumber. Resources with neither
  get set 0 and the lowest binding number not taken by an explicit slot, in declaration order (what
  DXC does). Plain globals outside any `cbuffer` collect in an implicit constant buffer named
  `Globals` (`_Globals` if that name is taken).
- Vertex input locations follow declaration order unless `[[vk::location]]` is given. Formats
  follow the shader type; a packed format is declared on the input field with
  `[[oneshader::format("unorm8x4")]]` (any `VertexFormat` name, case-insensitive; the component
  count and integer/float kind must match the declared type, e.g. `float4` for `unorm8x4`). The
  shader sees the converted `float4`. DXC compiles the same file with a warning ("unknown
  attribute 'format' ignored") and no error. Offsets are packed in declaration order, so the
  vertex struct follows the format as described under "Vertex fields follow the format".

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
