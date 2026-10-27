# Reflection file format

The build task writes one reflection file per shader; the source generator reads it without loading
the compiler. The model is `OneShader.Reflection.ShaderReflection`; `ReflectionFormat.Write` and
`TryRead` implement this format. Version 1.

## Rules

- UTF-8, no byte-order mark, `\n` line ends, one record per line, ending with a newline.
- First line: `oneshader-reflection 1`. A reader must reject other versions. Additive changes
  (new records or new attributes) keep version 1; readers ignore unknown records and attributes.
- A record is `keyword key=value key=value ...`. Values are bare tokens (no spaces) or double-quoted
  strings with `\\`, `\"`, `\n`, `\r`, `\t` and `\uXXXX` escapes. Numbers are invariant-culture
  decimals; all counts, sizes, strides, offsets, locations and workgroup sizes are unsigned.
  Enum values use the member names of the C# enums. Lists are comma-separated; an empty list is `-`.
- Nesting is by indentation: two spaces per level.
- Order is the model's order (declaration order). Equal models give identical bytes.

## Records

```
source name="Shaders/Sprite.hlsl"
targets list=SpirV,Wgsl
resource name="Frame" kind=ConstantBuffer count=1 stages=Vertex,Pixel rtype=ConstantBuffer hlsl=""
  slot target=SpirV set=0 binding=0
  layout
    type kind=Struct scalar=Bool rows=0 cols=0 name="Frame" size=128 align=16 length=0 stride=0 mstride=0 packing=ColumnMajor
      member name="ViewProjection" offset=0
        type kind=Matrix ... mstride=16 packing=ColumnMajor
      member name="Weights" offset=80
        type kind=Array ... length=3 stride=16
          element
            type kind=Scalar scalar=Float ...
entry name="VSMain" stage=Vertex workgroup=0,0,0 stride=32
  input name="Position" semantic="POSITION" index=0 location=0 builtin=None scalar=Float components=2 format=Float32x2 offset=0 interp=Default
  output name="Position" semantic="SV_Position" index=0 location=- builtin=Position scalar=Float components=4 format=None offset=0 interp=Default
  uses name="Frame"
```

- `resource`: an optional `format` attribute holds the `[[vk::image_format]]` name of a read-write texture; `kind` is `ResourceKind`; `stages` a list of `Vertex`, `Pixel`, `Compute` (`-` for none);
  `rtype` is `ResourceTypeKind`; `hlsl` the type as written. Child `slot` records give one slot per
  target (`target` is `SpirV`, `Msl` or `Wgsl`; Metal uses the argument index as `binding` and 0 as `set`).
  `count` is the array size (1 for a single resource); an array occupies one `slot`. A `layout` child holds a `type` tree.
- `counter` (child of `resource`, append and consume buffers only): `target`, `set`, `binding` of the hidden
  counter buffer, one record per target like `slot`. Readers that do not know it ignore it.
- `push`: a push-constant block (not a `resource`): `name`, `stages`, a `layout` child with a `type` tree, and `slot`
  children for the targets that deliver it as a bound buffer: `Msl` (`binding` is the `[[buffer(n)]]` index, `set` 0) and
  `Wgsl` (`set` is the bind group, `binding` the binding). SPIR-V has no slot. An `entry` names the block it reads with a
  `pushuse name=...` child.
- `features` (child of `entry`, written only when non-zero): `bits`, the `DeviceFeatures` flags the entry point needs for 16-bit and 64-bit types (1 Float16, 2 Int16, 4 Float64, 8 Int64, 16 StorageBuffer16, 32 UniformBuffer16, 64 PushConstant16, 128 InputOutput16, 256 Int64Atomics).
- `spec`: a specialization constant: `name`, `id` (the `constant_id`), `scalar` (`Bool`, `Int`, `UInt` or `Float`) and `default` (the 32-bit pattern, as a number).
  Child `slot` records (`target`, `index`, `name`) give the SPIR-V `SpecId`, the Metal function-constant index and variable, and the WGSL `@id` and override
  name per target. Written only for files that have constants; it follows the `push` records. An `entry` lists the constants it reads with `specuse name=...` children.
- `type`: `kind` is `Scalar`, `Vector`, `Matrix`, `Array` or `Struct`. `rows` is the vector component
  count or matrix rows; `cols` matrix columns; `size`, `align` in bytes; `length`/`stride` for
  arrays (length 0 = unsized); `mstride`/`packing` for matrices (the stride is between stored
  columns for `ColumnMajor`, rows for `RowMajor`). An `Array` has one `element` child holding a
  `type`; a `Struct` has `member` children (`name`, `offset`) each holding one `type`.
- `entry`: `workgroup` is `x,y,z` (zeros unless compute); `stride` the interleaved vertex size
  (vertex stage only). `input`/`output` are stage variables; `location=-` means built-in; `format` is a
  `VertexFormat` name for vertex inputs and `None` elsewhere. `uses` lists resource names the entry
  point touches.

Layouts are the single buffer layout shared by all targets (see `BufferLayout`).
