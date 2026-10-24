using System;
using OneShader;
using OneShader.IR;
using OneShader.Reflection;
using OneShader.Types;

Console.WriteLine(OneShaderInfo.Name);

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

var reflection = new ShaderReflection { SourceName = "smoke.hlsl", Resources = [new ResourceInfo { Name = "Frame", Kind = ResourceKind.ConstantBuffer, Layout = ReflectedType.FromLayout(layout!) }] };
if (!ReflectionFormat.TryRead(ReflectionFormat.Write(reflection), out var back, out _) || !ReflectionFormat.AreEqual(reflection, back!))
{
    return 2;
}

var result = ShaderCompiler.Compile("float4 main() : SV_Target { return 0; }", "smoke.hlsl");
Console.WriteLine(result.Diagnostics[0]);
return result.Success ? 3 : 0;
