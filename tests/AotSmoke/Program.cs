using System;
using OneShader;
using OneShader.Diagnostics;
using OneShader.IR;
using OneShader.Lexing;
using OneShader.Preprocessing;
using OneShader.Reflection;
using OneShader.Types;

Console.WriteLine(OneShaderInfo.Name);

// Exercises the lexer and preprocessor so the trim and AOT analyzers see their code paths.
var diagnostics = new DiagnosticBag();
PreprocessResult pp = Preprocessor.Preprocess(
    new SourceFile("smoke.hlsl", "#define SQR(x) ((x) * (x))\nfloat y = SQR(2);\n"),
    new PreprocessorOptions { TargetProfile = "ps_6_0" },
    diagnostics);
if (diagnostics.HasErrors || pp.ToText().Trim() != "float y = ((2) * (2));")
{
    Console.Error.WriteLine("preprocessor smoke test failed");
    return 1;
}

// Touch the public model so the trimmer and AOT analysis see it: layout, IR printing, reflection
// serialization and the compile entry point.
var block = new StructType("Frame", [new StructField("Tint", HlslType.Vector(ScalarType.Float, 4))]);
if (!BufferLayout.TryCompute(block, BufferLayoutRules.ConstantBuffer, null, out var layout, out _))
{
    return 1;
}

var module = new IrModule { Name = "smoke" };
var f = new IrFunction("main", HlslType.Void);
module.Functions.Add(f);
new IrBuilder(f) { Block = f.AddBlock() }.Return();
Console.WriteLine(IrPrinter.Print(module).Length > 0 ? "ir ok" : "ir empty");

module.EntryPoints.Add(new EntryPoint(f, ShaderStage.Compute) { WorkgroupSize = (1, 1, 1) });
byte[]? spirv = OneShader.SpirV.SpirvBackend.Emit(module, "main", null, new DiagnosticBag());
if (spirv is null || spirv.Length < 20 || spirv[0] != 0x03 || spirv[1] != 0x02)
{
    Console.Error.WriteLine("SPIR-V smoke test failed");
    return 4;
}

var reflection = new ShaderReflection { SourceName = "smoke.hlsl", Resources = [new ResourceInfo { Name = "Frame", Kind = ResourceKind.ConstantBuffer, Layout = ReflectedType.FromLayout(layout!) }] };
if (!ReflectionFormat.TryRead(ReflectionFormat.Write(reflection), out var back, out _) || !ReflectionFormat.AreEqual(reflection, back!))
{
    return 2;
}

const string Shader = "cbuffer Frame { float4x4 M; };\nTexture2D<float4> T; SamplerState S;\n"
    + "struct VO { float4 P : SV_Position; float2 Uv : TEXCOORD0; };\n"
    + "[shader(\"vertex\")] VO VS(float2 p : POSITION) { VO o; o.P = mul(M, float4(p, 0, 1)); o.Uv = p; return o; }\n"
    + "[shader(\"pixel\")] float4 PS(VO i) : SV_Target { return T.Sample(S, i.Uv); }\n";
var result = ShaderCompiler.Compile(Shader, "smoke.hlsl");
if (!result.Success || result.Reflection is null || result.Reflection.EntryPoints.Count != 2 || result.Reflection.Resources.Count != 3
    || result.Blobs.Count != 2 || result.Find("PS", CompileTarget.SpirV) is not { } ps || ps.Data.Length < 20 || ps.Data[0] != 0x03)
{
    foreach (var d in result.Diagnostics)
    {
        Console.Error.WriteLine(d);
    }

    return 3;
}

// The same shader through all three backends.
var all = ShaderCompiler.Compile(Shader, "smoke.hlsl", new CompileOptions { Targets = [CompileTarget.SpirV, CompileTarget.Msl, CompileTarget.Wgsl] });
if (!all.Success || all.Blobs.Count != 6
    || all.Find("PS", CompileTarget.Msl) is not { } msl || !msl.AsText().Contains("fragment", StringComparison.Ordinal)
    || all.Find("PS", CompileTarget.Wgsl) is not { } wgsl || !wgsl.AsText().Contains("@fragment", StringComparison.Ordinal))
{
    foreach (var d in all.Diagnostics)
    {
        Console.Error.WriteLine(d);
    }

    Console.Error.WriteLine("multi-target smoke test failed");
    return 8;
}

// Pipeline layouts for every target.
if (!OneShader.Layouts.EntryLayout.TryCreate(all.Reflection!, "VS", out var vsLayout, out _)
    || !OneShader.Layouts.EntryLayout.TryCreate(all.Reflection!, "PS", out var psLayout, out _)
    || !OneShader.Layouts.PipelineLayout.TryCreate([vsLayout!, psLayout!], out var pipelineLayout, out _)
    || pipelineLayout!.ToVulkan().SetLayouts[0].Bindings.Count != 3
    || !pipelineLayout.TryToMetal(out var metalLayout, out _) || metalLayout!.Stages.Count != 2
    || !pipelineLayout.TryToWebGpu(out var webLayout, out _) || webLayout!.Groups[0].Entries.Count != 3
    || vsLayout!.Vertex is not { } vertexLayout || vertexLayout.ToWebGpu().Attributes.Count != 1)
{
    Console.Error.WriteLine("pipeline layout smoke test failed");
    return 9;
}

// Hot-reload path: an edit that only touches a body is compatible, one that moves a constant-buffer member is not.
var edited = ShaderCompiler.Compile(Shader.Replace("i.Uv);", "i.Uv) * 0.5;", StringComparison.Ordinal), "smoke.hlsl");
var moved = ShaderCompiler.Compile(Shader.Replace("float4x4 M;", "float4 Pad; float4x4 M;", StringComparison.Ordinal), "smoke.hlsl");
if (!edited.Success || !moved.Success || !InterfaceCompatibility.Compare(result.Reflection, edited.Reflection!).IsCompatible)
{
    return 5;
}

var report = InterfaceCompatibility.Compare(result.Reflection, moved.Reflection!);
if (report.IsCompatible || report.Differences[0].Message != "Frame.M moved from offset 0 to 16" || result.Files.Count != 1)
{
    Console.Error.WriteLine(report);
    return 6;
}

var missing = ShaderCompiler.CompileFile("does-not-exist.hlsl");
if (missing.Success || missing.Diagnostics[0].Code != ApiDiagnosticCodes.FileNotReadable)
{
    return 7;
}

Console.WriteLine("compiled " + result.Reflection.EntryPoints.Count + " entry points");
return 0;
