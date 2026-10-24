using System;
using OneShader;
using OneShader.Diagnostics;
using OneShader.Lexing;
using OneShader.Preprocessing;

Console.WriteLine(OneShaderInfo.Name);

// Exercises the lexer and preprocessor so the trim and AOT analyzers see their code paths.
var diagnostics = new DiagnosticBag();
PreprocessResult result = Preprocessor.Preprocess(
    new SourceFile("smoke.hlsl", "#define SQR(x) ((x) * (x))\nfloat y = SQR(2);\n"),
    new PreprocessorOptions { TargetProfile = "ps_6_0" },
    diagnostics);
if (diagnostics.HasErrors || result.ToText().Trim() != "float y = ((2) * (2));")
{
    Console.Error.WriteLine("preprocessor smoke test failed");
    return 1;
}

return 0;
