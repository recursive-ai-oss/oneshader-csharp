using System.Collections.Generic;
using System.Linq;
using System.Threading;
using OneShader.Diagnostics;
using OneShader.Lexing;
using OneShader.Preprocessing;
using Xunit;

namespace OneShader.Tests;

public class PreprocessorDiagnosticTests
{
    [Fact]
    public void UnclosedIfPointsAtTheDirective()
    {
        (_, DiagnosticBag d) = Pp.Run("float a;\n\n#if FOO\nfloat b;\n");
        Assert.Equal(
            ["a.hlsl(3,1): error OSH0110: '#if' is never closed; add a matching '#endif' before the end of a.hlsl"],
            Pp.Errors(d));
    }

    [Fact]
    public void MissingIncludeNamesFileAndSearchedDirectories()
    {
        (_, DiagnosticBag d) = Pp.Run("float a;\n#include \"missing.h\"\n", includeDirs: ["shared", "vendor/inc"]);
        Assert.Equal(
            ["a.hlsl(2,10): error OSH0120: cannot find include file 'missing.h'; searched: '.', 'shared', 'vendor/inc'. Check the spelling, or add the directory that contains it to the include paths"],
            Pp.Errors(d));
    }

    [Fact]
    public void ErrorInIncludedFileIsReportedInThatFile()
    {
        var files = new Dictionary<string, string> { ["inc.h"] = "// header\n\n\n#error boom\n" };
        (_, DiagnosticBag d) = Pp.Run("#include \"inc.h\"\n", files);
        Assert.Equal(["inc.h(4,1): error OSH0114: #error boom"], Pp.Errors(d));
    }

    [Fact]
    public void WrongArgumentCountSaysHowManyAreTaken()
    {
        const string src = "#define M(a,b) a+b\n\n\n\n    r = M(1);\n";
        (_, DiagnosticBag d) = Pp.Run(src);
        Assert.Equal(
            ["a.hlsl(5,9): error OSH0124: macro 'M' takes 2 arguments but 1 was given; pass the missing arguments"],
            Pp.Errors(d));
    }

    [Fact]
    public void IncompleteIfExpressionPointsAtTheDanglingOperator()
    {
        (_, DiagnosticBag d) = Pp.Run("#if 1 +\n#endif\n");
        Assert.Equal(
            ["a.hlsl(1,7): error OSH0116: the expression ends after '+' where a value was expected; add the missing operand"],
            Pp.Errors(d));
    }

    [Fact]
    public void SelfIncludeWithoutGuardNamesTheChainAndStops()
    {
        var files = new Dictionary<string, string> { ["loop.h"] = "x\n#include \"loop.h\"\n" };
        (PreprocessResult r, DiagnosticBag d) = Pp.Run("#include \"loop.h\"\n", files);
        string[] errs = Pp.Errors(d);
        Assert.Single(errs);
        Assert.StartsWith("loop.h(2,", errs[0]);
        Assert.Contains("OSH0122", errs[0]);
        Assert.Contains("include chain: a.hlsl -> loop.h -> loop.h (and so on", errs[0]);
        Assert.True(r.Tokens.Count < 200);
    }

    [Fact]
    public void MutualIncludeCycleShowsTheCycle()
    {
        var files = new Dictionary<string, string> { ["p.h"] = "#include \"q.h\"\n", ["q.h"] = "#include \"p.h\"\n" };
        (_, DiagnosticBag d) = Pp.Run("#include \"p.h\"\n", files);
        string[] errs = Pp.Errors(d);
        Assert.Single(errs);
        Assert.Contains("include chain: a.hlsl -> p.h -> q.h -> p.h", errs[0]);
        Assert.Contains("(and so on until the nesting limit)", errs[0]);
    }

    [Fact]
    public void MacroFromIncludeExpandsAtTheUseSite()
    {
        var files = new Dictionary<string, string> { ["inc.h"] = "#define VALUE (1 +\n#define WRAP(x) x\n#define TWO 2)\n#define FN(a) a\n" };
        const string src = "#include \"inc.h\"\n\n\n\n\n\nint a = VALUE TWO; int b = FN(3);\n";
        (PreprocessResult r, DiagnosticBag d) = Pp.Run(src, files);
        Assert.Empty(d.Items);
        Token[] fromMacro = r.Tokens.Where(t => (t.Flags & TokenTraits.FromMacro) != 0).ToArray();
        Assert.NotEmpty(fromMacro);
        Assert.All(fromMacro, t => Assert.Equal("a.hlsl", t.Span.Path));
        Assert.All(fromMacro, t => Assert.Equal(7, t.Span.Line));
        // The expansion of VALUE starts at the macro name's column, not where the body was written.
        Assert.Equal(9, r.Tokens.First(t => t.Text == "(").Span.Column);
    }

    [Fact]
    public void ArgumentsKeepTheirOwnLocations()
    {
        var files = new Dictionary<string, string> { ["inc.h"] = "#define PASS(x) x\n" };
        (PreprocessResult r, _) = Pp.Run("#include \"inc.h\"\nint a = PASS(\n  42);\n", files);
        Token t = r.Tokens.First(x => x.Text == "42");
        Assert.Equal(("a.hlsl", 3, 3), (t.Span.Path, t.Span.Line, t.Span.Column));
    }

    [Theory]
    [InlineData("#else\n", "OSH0111")]
    [InlineData("#endif\n", "OSH0111")]
    [InlineData("#elif 1\n", "OSH0111")]
    [InlineData("#if 1\n#else\n#else\n#endif\n", "OSH0112")]
    [InlineData("#if 1\n#else\n#elif 1\n#endif\n", "OSH0112")]
    [InlineData("#frobnicate\n", "OSH0113")]
    [InlineData("#define\n", "OSH0117")]
    [InlineData("#define 3 x\n", "OSH0117")]
    [InlineData("#define defined 1\n", "OSH0117")]
    [InlineData("#define F(a,a) a\n", "OSH0118")]
    [InlineData("#define F(a b) a\n", "OSH0118")]
    [InlineData("#define F(a) #b\n", "OSH0132")]
    [InlineData("#define G ## x\n", "OSH0118")]
    [InlineData("#include\n", "OSH0121")]
    [InlineData("#include foo\n", "OSH0121")]
    [InlineData("#line x\n", "OSH0123")]
    [InlineData("#if 1 / 0\n#endif\n", "OSH0116")]
    [InlineData("#if (1\n#endif\n", "OSH0116")]
    [InlineData("#if 1.5\n#endif\n", "OSH0116")]
    [InlineData("#if\n#endif\n", "OSH0116")]
    [InlineData("#if 1 2\n#endif\n", "OSH0116")]
    [InlineData("#if defined\n#endif\n", "OSH0131")]
    [InlineData("#define F(x) x\nF(1\n", "OSH0125")]
    [InlineData("#define C(a,b) a##b\nC(+,-)\n", "OSH0126")]
    [InlineData("char* s = \"open\n", "OSH0100")]
    [InlineData("char c = 'x\n", "OSH0101")]
    [InlineData("/* never closed\n", "OSH0102")]
    [InlineData("int x = 12abc;\n", "OSH0103")]
    [InlineData("#define __LINE__ 3\n", "OSH0117")]
    public void ReportsExactlyOneErrorWithCode(string src, string code)
    {
        (_, DiagnosticBag d) = Pp.Run(src);
        string[] errs = Pp.Errors(d);
        Assert.Single(errs);
        Assert.Contains(code, errs[0]);
        Assert.StartsWith("a.hlsl(", errs[0]);
    }

    [Fact]
    public void RedefinitionWarnsOnlyWhenDifferent()
    {
        (_, DiagnosticBag d) = Pp.Run("#define A 1\n#define A 1\n#define A 2\n");
        Assert.Single(d.Items);
        Assert.Equal(DiagnosticSeverity.Warning, d.Items[0].Severity);
        Assert.Equal(3, d.Items[0].Span.Line);
    }

    [Fact]
    public void WarningDirectiveAndExtraTokens()
    {
        (_, DiagnosticBag d) = Pp.Run("#warning careful\n#if 1\n#endif junk\n");
        Assert.False(d.HasErrors);
        Assert.Equal(2, d.Items.Count);
    }

    [Fact]
    public void UnknownPragmaIsKeptAsATokenWithoutErrors()
    {
        (PreprocessResult r, DiagnosticBag d) = Pp.Run("#pragma pack_matrix(row_major)\n#pragma whatever 1 2\nint x;\n");
        Assert.Empty(d.Items);
        Token[] pragmas = r.Tokens.Where(t => t.Kind == TokenKind.Pragma).ToArray();
        Assert.Equal(["pack_matrix(row_major)", "whatever 1 2"], pragmas.Select(t => t.Text).ToArray());
        Assert.Equal(1, pragmas[0].Span.Line);
    }

    [Fact]
    public void ExponentialMacroStopsWithAnError()
    {
        var opts = new PreprocessorOptions { MaxExpansionSteps = 200_000, MaxOutputTokens = 100_000 };
        string src = "#define A0 x\n" + string.Concat(Enumerable.Range(1, 40).Select(i => $"#define A{i} A{i - 1} A{i - 1}\n")) + "A40\n";
        (_, DiagnosticBag d) = Pp.Run(src, options: opts);
        string[] errs = Pp.Errors(d);
        Assert.Single(errs);
        Assert.Contains("OSH0128", errs[0]);
    }

    [Fact]
    public void DeeplyNestedMacroCallsAreRefusedNotOverflowed()
    {
        string src = "#define F(x) x\n" + string.Concat(Enumerable.Repeat("F(", 5000)) + "1" + new string(')', 5000) + "\n";
        (_, DiagnosticBag d) = Pp.Run(src);
        Assert.Contains(Pp.Errors(d), e => e.Contains("OSH0127"));
    }

    [Fact]
    public void DeeplyNestedConditionIsRefusedNotOverflowed()
    {
        string src = "#if " + new string('(', 100_000) + "1" + new string(')', 100_000) + "\n#endif\n";
        (_, DiagnosticBag d) = Pp.Run(src);
        Assert.Single(Pp.Errors(d));
        string src2 = "#if " + string.Concat(Enumerable.Repeat("!", 100_000)) + "1\n#endif\n";
        (_, d) = Pp.Run(src2);
        Assert.Single(Pp.Errors(d));
    }

    [Fact]
    public void ExponentialIncludesStop()
    {
        var files = new Dictionary<string, string>();
        for (int i = 0; i < 30; i++)
        {
            files[$"f{i}.h"] = $"#include \"f{i + 1}.h\"\n#include \"f{i + 1}.h\"\n";
        }

        files["f30.h"] = "x\n";
        (_, DiagnosticBag d) = Pp.Run("#include \"f0.h\"\n", files);
        Assert.Single(Pp.Errors(d));
    }

    [Fact]
    public void CancellationIsHonored()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        string src = "#define A 1\n" + string.Concat(Enumerable.Repeat("A A A A A A A A\n", 20000));
        Assert.Throws<System.OperationCanceledException>(() =>
            Preprocessor.Preprocess(new SourceFile("a.hlsl", src), new PreprocessorOptions(), new DiagnosticBag(), cts.Token));
    }
}
