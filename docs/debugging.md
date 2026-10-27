# Debugging shaders

GPU debuggers should show and step through your HLSL, not our IR. `CompileOptions.DebugInfo` asks for it; it is off by default and
changes nothing when off (every blob is byte-for-byte what it was).

| Level | SPIR-V | MSL | WGSL |
|---|---|---|---|
| `DebugInfoLevel.None` (default) | as before | as before | as before |
| `DebugInfoLevel.Lines` | `OpString` and `OpSource` with the text of the main file and of each included file, `OpLine`/`OpNoLine` (DXC's `-Zi`) | `#line N "file"` directives | unchanged |
| `DebugInfoLevel.Full` | the `NonSemantic.Shader.DebugInfo.100` instruction set (DXC's `-fspv-debug=vulkan-with-source`), see [spirv-backend.md](spirv-backend.md#debug-information) | `#line` directives | unchanged |

| Where | Switch |
|---|---|
| API | `new CompileOptions { DebugInfo = DebugInfoLevel.Full, DebugSourceRoot = projectDirectory }` |
| Command line | `-Zi` (Lines), `-fspv-debug=vulkan-with-source` or `vulkan` (Full), `-fspv-debug=file\|source\|line\|tool` (Lines), `--debug-root <dir>`; `-Od` turns the optimizer off |
| MSBuild | `<OneShaderDebugInfo>true</OneShaderDebugInfo>` (or `full`, `lines`, `false`), `<OneShaderOptimize>false</OneShaderOptimize>`; item metadata `DebugInfo` and `Optimize` override them per file |

## In Debug builds only

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Debug'">
  <OneShaderDebugInfo>true</OneShaderDebugInfo>
  <OneShaderOptimize>false</OneShaderOptimize>
</PropertyGroup>
```

Release builds keep the small, optimized blobs. Turning the property on or off recompiles the shaders; the generated C# does not change
apart from the bytes of the blobs.

## Paths

The files embedded in debug information are named relative to `DebugSourceRoot` with `/` separators (the build package uses the project
directory). A path outside the root is reduced to its file name, and with no root a relative path keeps its spelling and an absolute
path becomes its file name, so no machine-specific path reaches a binary. Two checkouts in different folders give identical blobs.

## Optimization and debug information

The optimizer keeps running when debug information is on (as DXC does under `-Zi` unless `-Od`). Every instruction carries the position of the
HLSL it came from, and inlining, promotion of locals, folding, common-subexpression elimination and control-flow simplification all keep
that position on what they produce, so line information survives every pass; code that is removed takes its position with it. Inlined
calls are recorded (`DebugInlinedAt`) so a debugger shows the call stack of the HLSL.

What an optimized shader loses is variables: a local that lives in a register after optimization has no `DebugDeclare`, and only
parameters of functions that were not inlined and locals that stayed in memory are described. For stepping with every variable
visible compile with the optimizer off (`Optimize = false`, `-Od`, `OneShaderOptimize=false`): then each statement is one step and every local
and parameter has a `DebugDeclare`. The optimizer-off output is slower and larger, which is fine for a debugging session.

## Macros and includes

A statement that comes out of a macro is attributed to the line where the macro is used, with the column of the macro's name, as DXC does;
the `#define` line is not stepped through. A statement in an `#include`d file is attributed to that file and line, and the file's text is embedded
as its own source.

## RenderDoc

1. Build the shaders with debug information and without the optimizer (above), or compile with
   `oneshader -T ps_6_0 -E PSMain -fspv-debug=vulkan-with-source -Od shader.hlsl -Fo shader.spv`.
2. Capture a frame of your application with RenderDoc.
3. Select a draw, open the pixel or vertex shader in the pipeline state, and use **Debug this Pixel** / **Debug this Vertex** (or the
   compute thread debugger). RenderDoc reads `NonSemantic.Shader.DebugInfo.100`, so the source view shows your `.hlsl` text (and the included
   files) and stepping follows the HLSL lines; locals and parameters appear in the variables view when the optimizer was off.
4. Do not strip the debug records from the SPIR-V your application passes to Vulkan (some pipeline caches and `spirv-opt --strip-debug`
   do); RenderDoc reads the same binary the driver got.

Nsight Graphics reads the same records. For Metal, Xcode's shader debugger and `metal -gline-tables-only -frecord-sources` map statements
through the `#line` directives to the HLSL file; compile the MSL with those flags and open the capture in Xcode.

## Validation

`spirv-val --target-env vulkan1.2` checks the debug instructions too (operand kinds, that line numbers are 32-bit constants, that sources are
`DebugSource` records). Debug output of the whole corpus, optimized and not, is validated in the test suite.
