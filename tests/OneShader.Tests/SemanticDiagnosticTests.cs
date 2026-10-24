using System.Linq;
using Xunit;

namespace OneShader.Tests;

public class SemanticDiagnosticTests
{
    private static string[] All(string text, string name = "s.hlsl") =>
        Log(FrontendCorpusTests.Run(text, name).Diagnostics.Items.Select(d => d.ToString()).ToArray());

    private static string[] Log(string[] lines)
    {
        if (System.Environment.GetEnvironmentVariable("ONESHADER_DUMP") is { Length: > 0 } dir)
        {
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllLines(System.IO.Path.Combine(dir, "diagnostics.txt"), lines);
        }

        return lines;
    }

    private const string Entry = "\n[shader(\"pixel\")] float4 PS() : SV_Target { return 0; }\n";

    [Fact]
    public void UndeclaredIdentifierSuggestsTheClosestName()
    {
        var d = All("Texture2D<float4> Atlas;\nfloat4 f(float2 uv) {\n    return Atlass.Load(int3(0, 0, 0));\n}" + Entry);
        Assert.Equal(["s.hlsl(3,12): error OSH0300: 'Atlass' is not declared. Did you mean 'Atlas'?"], d);
    }

    [Fact]
    public void WrongArgumentCountNamesTheFunctionAndExpectedCount()
    {
        var d = All("float Add(float a, float b) { return a + b; }\nfloat g() { return Add(1.0, 2.0, 3.0); }" + Entry);
        Assert.Equal(["s.hlsl(2,20): error OSH0303: The function 'Add' expects 2 arguments but the call passes 3."], d);
    }

    [Fact]
    public void ImplicitVectorTruncationIsAWarningAtTheInitializer()
    {
        var d = All("float4 src;\nfloat3 g() {\n    float3 x = float4(1, 2, 3, 4);\n    return x;\n}" + Entry);
        Assert.Single(d);
        Assert.StartsWith("s.hlsl(3,16): warning OSH0306: Implicit truncation of 'float4' to 'float3' drops components.", d[0]);
    }

    [Fact]
    public void AssigningToAConstantBufferMemberIsAnError()
    {
        var d = All("cbuffer Frame { float4 Tint; };\nvoid g() {\n    Tint = float4(1, 1, 1, 1);\n}" + Entry);
        Assert.Single(d);
        Assert.Equal("s.hlsl(3,5): error OSH0307: Cannot assign here: 'Tint' is a member of the constant buffer 'Frame', which shaders cannot write. Copy it into a local variable first.", d[0]);
    }

    [Fact]
    public void TextureAndSamplerOnTheSameRegisterNumberCollide()
    {
        var d = All("Texture2D<float4> Atlas : register(t0);\nSamplerState Samp : register(s0);\n[shader(\"pixel\")] float4 PS(float2 uv : TEXCOORD0) : SV_Target { return Atlas.Sample(Samp, uv); }");
        Assert.Single(d);
        Assert.StartsWith("s.hlsl(2,14): error OSH0330: The resources 'Atlas' and 'Samp' both use set 0, binding 0. Add [[vk::binding(binding, set)]] to one of them", d[0]);
    }

    [Fact]
    public void VertexEntryWithoutSvPositionIsAnErrorAtTheFunction()
    {
        var d = All("struct Out { float2 Uv : TEXCOORD0; };\n[shader(\"vertex\")]\nOut VS(float2 p : POSITION) {\n    Out o; o.Uv = p; return o;\n}");
        Assert.Single(d);
        Assert.StartsWith("s.hlsl(3,5): error OSH0320: The vertex shader 'VS' does not output SV_Position.", d[0]);
    }

    [Fact]
    public void MissingSemicolonReportsOnceAndLaterErrorsStillAppear()
    {
        var d = All("float f(float a) {\n    float b = a * 2.0\n    return b + missing;\n}");
        Assert.Equal(2, d.Length);
        Assert.StartsWith("s.hlsl(2,22): error OSH0200: Expected ';' after the declaration but found 'return'.", d[0]);
        Assert.StartsWith("s.hlsl(3,16): error OSH0300: 'missing' is not declared.", d[1]);
    }

    [Fact]
    public void BadInputNeverThrows()
    {
        foreach (string text in new[] { "", "}", "float", "float f(", "struct", "cbuffer", "[[", "float4 x = ;", "void f() { for (;;", "template<" })
        {
            var r = FrontendCorpusTests.Run(text);
            Assert.NotNull(r.Diagnostics);
        }
    }
}
