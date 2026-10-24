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

var result = ShaderCompiler.Compile("float4 main() : SV_Target { return 0; }", "smoke.hlsl");
Console.WriteLine(result.Diagnostics[0]);
return result.Success ? 3 : 0;
