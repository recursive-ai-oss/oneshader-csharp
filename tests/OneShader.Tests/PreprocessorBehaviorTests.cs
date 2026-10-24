using System.Collections.Generic;
using OneShader.Diagnostics;
using OneShader.Lexing;
using OneShader.Preprocessing;
using Xunit;

namespace OneShader.Tests;

public class PreprocessorBehaviorTests
{
    [Fact]
    public void PragmaOnceRecognizesTheSameFileUnderDifferentSpellings()
    {
        var files = new Dictionary<string, string> { ["inc/o.h"] = "#pragma once\nbody\n" };
        (PreprocessResult r, DiagnosticBag d) = Pp.Run("#include \"inc/o.h\"\n#include \"./inc/o.h\"\n#include \"inc/../inc/o.h\"\n", files);
        Assert.Empty(d.Items);
        Assert.Equal("body", Pp.Joined(r));
    }

    [Fact]
    public void IncludeGuardedFileIsReadOnce()
    {
        var counting = new CountingResolver(new Dictionary<string, string> { ["g.h"] = "#ifndef G_H\n#define G_H\nbody\n#endif\n" });
        var opts = new PreprocessorOptions { IncludeResolver = counting };
        (PreprocessResult r, _) = Pp.Run("#include \"g.h\"\n#include \"g.h\"\n#include \"g.h\"\n", options: opts);
        Assert.Equal("body", Pp.Joined(r));
        Assert.Equal(3, counting.Calls); // the resolver is asked each time; the guard skips lexing and walking
    }

    [Fact]
    public void FileAndLineFollowLineDirectivesButSpansStayPhysical()
    {
        (PreprocessResult r, _) = Pp.Run("#line 50 \"gen.hlsl\"\n__FILE__ __LINE__ x\n");
        Assert.Equal("\"gen.hlsl\" 50 x", Pp.Joined(r));
        Token x = r.Tokens[2];
        Assert.Equal(("a.hlsl", 2), (x.Span.Path, x.Span.Line));
    }

    [Fact]
    public void ToTextRelexesToTheSameTokens()
    {
        (PreprocessResult r, _) = Pp.Run("#define P(a,b) a##b\n#define M -\nint x = M-1; P(1,.5) P(+,=) a+ +b M M\n");
        string text = r.ToText();
        string[] again = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(
            System.Linq.Enumerable.Where(Lexer.Lex(new SourceFile("t", text)), t => t.Kind != TokenKind.EndOfFile), t => t.Text));
        Assert.Equal(Pp.Texts(r), again);
    }

    [Fact]
    public void OutputIsDeterministic()
    {
        const string src = "#define A(x) x x\n#define B A(A(1))\nB __COUNTER__ __LINE__\n";
        Assert.Equal(Pp.Run(src).Result.ToText(), Pp.Run(src).Result.ToText());
    }

    [Fact]
    public void FilesListsEveryFileReadMainFirst()
    {
        var files = new Dictionary<string, string> { ["x/a.h"] = "#include \"b.h\"\n", ["x/b.h"] = "b\n" };
        (PreprocessResult r, _) = Pp.Run("#include \"x/a.h\"\n#include \"x/b.h\"\n", files);
        Assert.Equal(["a.hlsl", "x/a.h", "x/b.h"], r.Files);
    }

    [Fact]
    public void ResolverExceptionBecomesADiagnostic()
    {
        var opts = new PreprocessorOptions { IncludeResolver = new ThrowingResolver() };
        (_, DiagnosticBag d) = Pp.Run("#include \"x.h\"\n", options: opts);
        string[] errs = Pp.Errors(d);
        Assert.Single(errs);
        Assert.Contains("OSH0133", errs[0]);
    }

    [Fact]
    public void FileSystemResolverFindsRelativeAndSearchPathFiles()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "osh-res-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, "lib"));
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "near.h"), "near\n");
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "lib", "far.h"), "far\n");
            var opts = new PreprocessorOptions { IncludeResolver = new FileSystemIncludeResolver([System.IO.Path.Combine(dir, "lib")]) };
            var bag = new DiagnosticBag();
            PreprocessResult r = Preprocessor.Preprocess(
                new SourceFile(System.IO.Path.Combine(dir, "main.hlsl"), "#include \"near.h\"\n#include <far.h>\n#include \"far.h\"\n"), opts, bag, TestContext.Current.CancellationToken);
            Assert.Empty(bag.Items);
            Assert.Equal("near far far", Pp.Joined(r));
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class CountingResolver(Dictionary<string, string> files) : IIncludeResolver
    {
        public int Calls { get; private set; }

        public SourceFile? Resolve(string name, bool isAngled, SourceFile includer, IList<string> searched)
        {
            Calls++;
            return files.TryGetValue(name, out string? t) ? new SourceFile(name, t) : null;
        }
    }

    private sealed class ThrowingResolver : IIncludeResolver
    {
        public SourceFile? Resolve(string name, bool isAngled, SourceFile includer, IList<string> searched) =>
            throw new System.InvalidOperationException("disk on fire");
    }
}
