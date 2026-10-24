using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using OneShader.Diagnostics;
using OneShader.Lexing;
using OneShader.Preprocessing;
using OneShader.Syntax;
using Xunit;

namespace OneShader.Tests;

internal static class CorpusFiles
{
    public static string Root([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "corpus"));

    public static IEnumerable<string> Shaders(string subdir) =>
        Directory.GetFiles(Path.Combine(Root(), subdir), "*.hlsl").OrderBy(p => p, StringComparer.Ordinal);
}

public class ParserTests
{
    internal static (CompilationUnit Unit, DiagnosticBag Diag) Parse(string text, string path = "a.hlsl")
    {
        var (pp, bag) = Pp.Run(text, path: path);
        var unit = Parser.Parse(pp.Tokens, bag, path);
        return (unit, bag);
    }

    private static string[] Errors(DiagnosticBag bag) => Pp.Errors(bag);

    public static IEnumerable<object[]> CorpusFileNames() =>
        CorpusFiles.Shaders("2d").Concat(CorpusFiles.Shaders("compute")).Select(p => new object[] { p });

    [Theory]
    [MemberData(nameof(CorpusFileNames))]
    public void CorpusFilesParseWithoutDiagnostics(string path)
    {
        var (unit, bag) = Parse(File.ReadAllText(path), Path.GetFileName(path));
        Assert.Empty(bag.Items);
        Assert.NotEmpty(unit.Declarations);
    }

    [Fact]
    public void ParsesDeclarationsAttributesAndAnnotations()
    {
        var (unit, bag) = Parse("""
            [[vk::binding(1, 2)]] Texture2D<float4> Tex : register(t3, space1);
            cbuffer C : register(b0) { float4x4 M : packoffset(c0); float a : packoffset(c4.y); };
            struct S { float2 p : POSITION; nointerpolation uint i : TEXCOORD1; };
            [shader("compute")] [numthreads(8, 8, 1)]
            void CS(uint3 id : SV_DispatchThreadID) { }
            """);
        Assert.Empty(bag.Items);
        var tex = Assert.IsType<VariableDeclaration>(unit.Declarations[0]);
        Assert.Equal("vk::binding", tex.Attributes[0].Name.ToString());
        var reg = Assert.IsType<RegisterAnnotation>(tex.Declarators[0].Annotations[0]);
        Assert.Equal(('t', 3u, 1u), (reg.RegisterClass, reg.Index, reg.Space));
        var cb = Assert.IsType<ConstantBufferDeclaration>(unit.Declarations[1]);
        Assert.Equal(2, cb.Members.Count);
        Assert.Equal(68u, Assert.IsType<PackOffsetAnnotation>(cb.Members[1].Declarators[0].Annotations[0]).ByteOffset);
        var fn = Assert.IsType<FunctionDeclaration>(unit.Declarations[3]);
        Assert.Equal(2, fn.Attributes.Count);
    }

    [Fact]
    public void NestedTemplateClosesWithShiftToken()
    {
        var (unit, bag) = Parse("StructuredBuffer<vector<float, 4>> B; Texture2D<float4> T; float f = 4 >> 1;");
        Assert.Empty(bag.Items);
        Assert.Equal(3, unit.Declarations.Count);
    }

    [Fact]
    public void CastsAndConstructorsAndConditionals()
    {
        var (unit, bag) = Parse("float4 f(uint i, float3 v) { float4 a = float4((float3)v, (float)i); return i > 2u ? a : (a < 1.0 ? a : a.wzyx); }");
        Assert.Empty(bag.Items);
        var fn = Assert.IsType<FunctionDeclaration>(unit.Declarations[0]);
        var decl = Assert.IsType<DeclarationStatement>(fn.Body!.Statements[0]);
        var init = Assert.IsType<VariableDeclaration>(decl.Declaration).Declarators[0].Initializer;
        var ctor = Assert.IsType<ConstructExpression>(init);
        Assert.IsType<CastExpression>(ctor.Arguments[0]);
    }

    [Fact]
    public void MissingSemicolonIsOneErrorAtTheEndOfThePreviousToken()
    {
        var (_, bag) = Parse("void f()\n{\n    int a = 1\n    int b = 2;\n}\n");
        var errors = Errors(bag);
        Assert.Single(errors);
        Assert.StartsWith("a.hlsl(3,14): error OSH0200: Expected ';' after the declaration but found 'int'.", errors[0]);
    }

    [Fact]
    public void RecoversAtStatementBoundariesAndReportsLaterErrors()
    {
        var (_, bag) = Parse("void f()\n{\n    int a = ;\n    int b = 2 +;\n}\nvoid g() { int ok = 1; }\n}\n");
        Assert.Equal(3, Errors(bag).Length);
    }

    [Fact]
    public void DeepNestingIsADiagnosticNotAStackOverflow()
    {
        string deep = "float f = " + new string('(', 100000) + "1" + new string(')', 100000) + ";";
        var (_, bag) = Parse(deep);
        Assert.Contains(bag.Items, d => d.Code == SyntaxCodes.TooDeep);
        string chain = "float f = " + string.Join(" + ", Enumerable.Repeat("1", 50000)) + ";";
        var (_, bag2) = Parse(chain);
        Assert.Contains(bag2.Items, d => d.Code == SyntaxCodes.TooDeep);
        string blocks = "void f() " + new string('{', 50000) + new string('}', 50000);
        var (_, bag3) = Parse(blocks);
        Assert.Contains(bag3.Items, d => d.Code == SyntaxCodes.TooDeep);
    }

    [Fact]
    public void ComparisonsAreNotMistakenForTemplates()
    {
        var (_, bag) = Parse("bool f(int a, int b, int c) { return a < b && b > c; }");
        Assert.Empty(bag.Items);
    }

    [Fact]
    public void ModifierWordsCanBeVariableNames()
    {
        var (_, bag) = Parse("float f(float sample) { sample = sample * 2.0; float linear = sample; return linear; }");
        Assert.Empty(bag.Items);
    }

    [Fact]
    public void SwitchAndLoopsParse()
    {
        var (_, bag) = Parse("""
            int f(int x) {
                [unroll] for (int i = 0; i < 4; i++) { if (i == 2) continue; x += i; }
                do { x--; } while (x > 10);
                while (x > 0) x -= 3;
                switch (x) { case 0: case 1: x = 5; break; default: x = 2; }
                return x;
            }
            """);
        Assert.Empty(bag.Items);
    }

    [Fact]
    public void TemplatesEnumsNamespacesTypedefs()
    {
        var (_, bag) = Parse("""
            template<typename T> T twice(T v) { return v + v; }
            enum Mode : uint { Off, On = 4 };
            namespace Math { static const float Pi = 3.14159; float Sq(float x) { return x * x; } }
            typedef float3 Vec;
            struct Light { float3 dir; float Len() { return length(dir); } };
            float g(Vec v) { return twice<float>(Math::Sq(v.x)) + Math::Pi + (float)Mode::On; }
            """);
        Assert.Empty(bag.Items);
    }
}
