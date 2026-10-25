using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using OneShader.Diagnostics;
using OneShader.Lexing;
using OneShader.Preprocessing;
using Xunit;

namespace OneShader.Tests;

/// <summary>
/// Black-box check against the released DXC: the token sequence we produce must equal the token
/// sequence of <c>dxc -P</c> for the same input. Skipped when DXC is not installed.
/// </summary>
public sealed partial class DxcPreprocessParityTests
{
    private sealed record Case(string Main, Dictionary<string, string>? Files = null, string[]? DxcArgs = null, Action<PreprocessorOptions>? Configure = null);

    private static readonly Dictionary<string, Case> Cases = new()
    {
        ["object-macros"] = new("""
            #define A 1
            #define B A + 2
            #define C (B * A)
            float x = C;
            """),
        ["function-macros-nested"] = new("""
            #define SQR(x) ((x) * (x))
            #define ADD(a, b) ((a) + (b))
            float y = ADD(SQR(2), SQR(ADD(1, 2)));
            float z = SQR (3);
            SQR
            """),
        ["stringize"] = new("""
            #define STR(x) #x
            #define XSTR(x) STR(x)
            #define V 42
            const char* a = STR(  a   +  "q\\n"   'c'  b );
            const char* b = XSTR(V) STR(V) STR() STR( x  /* c */ y );
            """),
        ["paste"] = new("""
            #define CAT(a, b) a##b
            #define XCAT(a, b) CAT(a, b)
            #define N 7
            CAT(foo, bar) CAT(1, 2) CAT(1., 5) CAT(+, +) CAT(x, 1) CAT(,) CAT(a,) CAT(,b)
            XCAT(v, N) CAT(v, N)
            #define OBJ x ## y ## z
            OBJ
            """),
        ["variadic"] = new("""
            #define LOG(fmt, ...) printf(fmt, __VA_ARGS__)
            #define G(...) g(__VA_ARGS__)
            #define F(a, ...) f(a, ## __VA_ARGS__)
            LOG("x", 1, 2, 3) G() G(1) G(1, (2, 3), 4) F(1) F(1, 2) F(1,)
            """),
        ["recursion"] = new("""
            #define AA BB
            #define BB AA
            AA BB
            #define f(x) x f
            #define g f(1)(2)
            g;
            #define foo foo + 1
            foo
            #define EMPTY
            #define OBJ EMPTY x
            OBJ OBJ
            """),
        ["conditionals"] = new("""
            #define LEVEL 3
            #if LEVEL == 1
            one
            #elif LEVEL == 2
            two
            #elif LEVEL >= 3 && LEVEL < 10
            three
            #else
            other
            #endif
            #if 0
            no
            #else
            yes
            #endif
            #if 1
            #if 0
            nested-no
            #elif 1
            nested-yes
            #endif
            #endif
            """),
        ["ifdef-undef"] = new("""
            #define X
            #ifdef X
            x-defined
            #endif
            #ifndef Y
            y-undefined
            #endif
            #undef X
            #ifdef X
            bad
            #else
            x-gone
            #endif
            #define Z 1
            #undef Z
            #define Z 2
            Z
            """),
        ["defined-operator"] = new("""
            #define A
            #define B 0
            #if defined(A) && defined B && !defined(C)
            ok1
            #endif
            #define HAS defined(A)
            #if defined A || defined(NOPE)
            ok2
            #endif
            #if !defined(A) || defined(B) && B
            ok3
            #else
            ok4
            #endif
            """),
        ["expressions"] = new("""
            #if (-1 < 0u)
            unsigned-true
            #else
            unsigned-false
            #endif
            #if 1 ? 2 : (1/0)
            ternary
            #endif
            #if (1 << 4) == 16 && (0x10 >> 2) == 4 && (7 & 3) == 3 && (4 | 1) == 5 && (6 ^ 3) == 5
            bits
            #endif
            #if 'a' == 97 && '\n' == 10
            chars
            #endif
            #if 010 == 8 && 0x1F == 31 && 5 % 3 == 2 && -7 / 2 == -3
            numbers
            #endif
            #if true && !false
            keywords
            #endif
            #if 1 || (1/0)
            shortcircuit
            #endif
            #if ~0 == -1 && !0 && +1
            unary
            #endif
            """),
        ["undefined-is-zero"] = new("""
            #if UNDEFINED_THING
            bad
            #elif UNDEFINED_THING == 0
            zero
            #endif
            """),
        ["include-quoted"] = new("""
            #include "inc/a.h"
            main-after
            """, new() { ["inc/a.h"] = "#define FROM_A 5\nfrom-a FROM_A\n" }),
        ["include-nested-guards"] = new("""
            #include "inc/a.h"
            #include "inc/b.h"
            #include "inc/a.h"
            end FROM_B
            """, new()
            {
                ["inc/a.h"] = "#ifndef A_H\n#define A_H\nin-a\n#include \"b.h\"\n#endif\n",
                ["inc/b.h"] = "#ifndef B_H\n#define B_H\nin-b\n#define FROM_B 9\n#endif\n",
            }),
        ["include-pragma-once"] = new("""
            #include "inc/o.h"
            #include "inc/o.h"
            tail
            """, new() { ["inc/o.h"] = "#pragma once\nonce-body\n" }),
        ["include-angle"] = new("""
            #include <sys.h>
            #include <sub/deep.h>
            after
            """, new() { ["sys/sys.h"] = "sys-body\n", ["sys/sub/deep.h"] = "deep-body\n" }, DxcArgs: ["-I", "sys"], Configure: null),
        ["include-macro-name"] = new("""
            #define HEADER "inc/m.h"
            #include HEADER
            #define STR(x) #x
            #define XSTR(x) STR(x)
            #define NAME inc/n.h
            #include XSTR(NAME)
            """, new() { ["inc/m.h"] = "m-body\n", ["inc/n.h"] = "n-body\n" }),
        ["include-relative-to-includer"] = new("""
            #include "inc/outer.h"
            """, new() { ["inc/outer.h"] = "outer\n#include \"inner.h\"\n", ["inc/inner.h"] = "inner\n" }),
        ["include-level"] = new("""
            level0 __INCLUDE_LEVEL__
            #include "inc/l.h"
            """, new() { ["inc/l.h"] = "level1 __INCLUDE_LEVEL__\n#include \"l2.h\"\n", ["inc/l2.h"] = "level2 __INCLUDE_LEVEL__\n" }),
        ["macro-defined-in-include-used-in-main"] = new("""
            #include "inc/m.h"
            SCALE(3) + LIMIT
            """, new() { ["inc/m.h"] = "#define SCALE(x) ((x) * LIMIT)\n#define LIMIT 16\n" }),
        ["line-continuation"] = new("""
            #define LONG(a, b) \
                (a) + \
                (b)
            LONG(1, \
                 2)
            #define S "abc\
            def"
            S
            """),
        ["multiline-arguments"] = new("""
            #define CALL(f, a, b) f(a, b)
            CALL(max,
                 1,
                 2) next
            CALL(min, (1,
               2), 3)
            """),
        ["empty-arguments"] = new("""
            #define F(x, y) [x|y]
            #define G(x) <x>
            #define H() h
            F(,) F(1,) F(,2) G() G( ) H()
            """),
        ["line-and-file"] = new("""
            a __LINE__
            #define L __LINE__
            L
            #line 100
            b __LINE__
            #line 200 "other.hlsl"
            c __LINE__ __FILE__
            #define E(x) [x]
            E(
             __LINE__
            ) __LINE__
            """),
        ["counter"] = new("""
            __COUNTER__ __COUNTER__ __COUNTER__
            #define C __COUNTER__
            C C
            """),
        ["std-example-3"] = new("""
            #define x 3
            #define f(a) f(x * (a))
            #undef x
            #define x 2
            #define g f
            #define z z[0]
            #define h g(~
            #define m(a) a(w)
            #define w 0,1
            #define t(a) a
            #define p() int
            #define q(x) x
            #define r(x,y) x ## y
            #define str(x) # x
            f(y+1) + f(f(z)) % t(t(g)(0) + t)(1);
            g(x+(3,4)-w) | h 5) & m
            (f)^m(m);
            p() i[q()] = { q(1), r(2,3), r(4,), r(,5), r(,) };
            char c[2][6] = { str(hello), str() };
            """),
        ["std-example-4"] = new("""
            #define str(s) # s
            #define xstr(s) str(s)
            #define debug(s, t) printf("x" # s "= %d, x" # t "= %s", \
            x ## s, x ## t)
            #define INCFILE(n) vers ## n
            #define glue(a, b) a ## b
            #define xglue(a, b) glue(a, b)
            #define HIGHLOW "hello"
            #define LOW LOW ", world"
            debug(1, 2);
            fputs(str(strncmp("abc\0d", "abc", '\4') // this goes away
            == 0) str(: @\n), s);
            xstr(INCFILE(2).h)
            glue(HIGH, LOW);
            xglue(HIGH, LOW)
            """),
        ["std-example-5"] = new("""
            #define t(x,y,z) x ## y ## z
            int j[] = { t(1,2,3), t(,4,5), t(6,,7), t(8,9,),
            t(10,,), t(,11,), t(,,12), t(,,) };
            """),
        ["std-example-7"] = new("""
            #define debug(...) fprintf(stderr, __VA_ARGS__)
            #define showlist(...) puts(#__VA_ARGS__)
            #define report(test, ...) ((test)?puts(#test):\
            printf(__VA_ARGS__))
            debug("Flag");
            debug("X = %d\n", x);
            showlist(The first, second, and third items.);
            report(x>y, "x is %d but y is %d", x, y);
            """),
        ["skipped-blocks-with-junk"] = new("""
            #if 0
            don't unbalanced ' quote and "string
            #garbage directive
            #include "does/not/exist.h"
            #error never
            #if 1
            nested
            #endif
            #else
            kept
            #endif
            after
            """),
        ["comments-and-spacing"] = new("""
            #define A /* comment */ 1 /* another */
            #define B(x) /* c */ (x) // trailing
            A B(2)/**/B(3)
            #if /* c */ 1 /* d */ // e
            yes /* inline */ yes2
            #endif
            """),
        ["hlsl-realistic"] = new("""
            #define REG(t, n) : register(t##n)
            #define BINDING(s, b) [[vk::binding(b, s)]]
            #define MAKE_FN(name, T) T name(T a, T b) { return a + b; }
            #pragma pack_matrix(row_major)
            BINDING(1, 2) Texture2D tex REG(t, 0);
            MAKE_FN(add_f, float)
            MAKE_FN(add_i, int)
            cbuffer Params : REG(b, 1) { float4x4 mvp; };
            #pragma pack_matrix(column_major)
            [numthreads(8, 8, 1)] void main(uint3 id : SV_DispatchThreadID) { a[id.x] = b[id.x]; }
            """),
        ["pragmas"] = new("""
            #pragma pack_matrix(row_major)
            #define PM pack_matrix
            #pragma PM(column_major)
            #pragma unknown_thing 1 2 3
            #pragma once
            body
            """),
        ["predefined-ps"] = new("""
            __HLSL_VERSION __SHADER_TARGET_STAGE __SHADER_TARGET_MAJOR __SHADER_TARGET_MINOR
            __SHADER_STAGE_VERTEX __SHADER_STAGE_PIXEL __SHADER_STAGE_COMPUTE
            #if __SHADER_TARGET_STAGE == __SHADER_STAGE_PIXEL
            is-pixel
            #endif
            #ifdef __spirv__
            spirv
            #else
            not-spirv
            #endif
            """, DxcArgs: ["-T", "ps_6_2"], Configure: o => o.TargetProfile = "ps_6_2"),
        ["predefined-cs-spirv"] = new("""
            __HLSL_VERSION __SHADER_TARGET_STAGE __SHADER_TARGET_MAJOR __SHADER_TARGET_MINOR
            __spirv__ __SPIRV_MAJOR_VERSION__ __SPIRV_MINOR_VERSION__
            #if __SHADER_TARGET_STAGE == __SHADER_STAGE_COMPUTE && defined(__spirv__)
            compute-spirv
            #endif
            """, DxcArgs: ["-T", "cs_6_6", "-spirv", "-fspv-target-env=vulkan1.2"], Configure: o => { o.TargetProfile = "cs_6_6"; o.SpirvMinorVersion = 5; }),
        ["command-line-defines"] = new("""
            #ifdef ENABLED
            enabled ENABLED
            #endif
            VALUE FLAG EMPTY_ONE SQR(4)
            """, DxcArgs: ["-D", "ENABLED", "-D", "VALUE=12", "-D", "FLAG=a+b", "-D", "EMPTY_ONE=", "-D", "SQR(x)=((x)*(x))"],
            Configure: o => o.Define("ENABLED").Define("VALUE", "12").Define("FLAG", "a+b").Define("EMPTY_ONE", "").Define("SQR(x)", "((x)*(x))")),
        ["has-include"] = new("""
            #if __has_include("inc/h.h")
            has-h
            #endif
            #if __has_include("inc/missing.h")
            has-missing
            #else
            no-missing
            #endif
            #ifdef __has_include
            defined-has-include
            #endif
            """, new() { ["inc/h.h"] = "x\n" }),
        ["function-macro-name-without-parens"] = new("""
            #define F(x) (x)
            #define OBJ F
            F + OBJ(1) OBJ F (2)
            F
            (3)
            """),
        ["commas-in-parens-and-brackets"] = new("""
            #define ID(x) x
            #define TWO(a, b) a|b
            ID(f(1, 2)) ID((1, 2)) TWO(g(1,2), h[1])
            """),
        ["redefinition-identical"] = new("""
            #define A 1
            #define A 1
            #define F(x) x
            #define F(x) x
            A F(2)
            """),
        ["nested-macro-in-args-rescan"] = new("""
            #define A(x) B(x) x
            #define B(x) [x]
            #define C A(A(1))
            C
            #define LPAREN (
            #define APPLY(m, a) m a
            APPLY(B, (3))
            """),
    };

    public static TheoryData<string> CaseNames()
    {
        var d = new TheoryData<string>();
        foreach (string k in Cases.Keys)
        {
            d.Add(k);
        }

        return d;
    }

    [Fact]
    public void HasAtLeastTwentyFiveCases() => Assert.True(Cases.Count >= 25);

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void MatchesDxcTokens(string name)
    {
        string? tools = Environment.GetEnvironmentVariable("ONESHADER_TOOLS");
        string dxc = Path.Combine(tools ?? string.Empty, "bin", "dxc" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        if (string.IsNullOrEmpty(tools) || !File.Exists(dxc))
        {
            Assert.Skip("dxc not found under $ONESHADER_TOOLS/bin");
        }

        Case c = Cases[name];
        string dir = Path.Combine(Path.GetTempPath(), "osh-pp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "main.hlsl"), c.Main);
            foreach ((string p, string text) in c.Files ?? [])
            {
                string full = Path.Combine(dir, p);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }

            var args = new List<string> { "-P", "-Fi", "out.txt", "main.hlsl" };
            if (c.DxcArgs is null || !c.DxcArgs.Contains("-T"))
            {
                args.AddRange(["-T", "ps_6_0"]);
            }

            args.AddRange(["-E", "main"]);
            if (c.DxcArgs is not null)
            {
                args.AddRange(c.DxcArgs);
            }

            var psi = new ProcessStartInfo(dxc) { WorkingDirectory = dir, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using Process proc = Process.Start(psi)!;
            string stderr = proc.StandardError.ReadToEnd();
            proc.StandardOutput.ReadToEnd();
            Assert.True(proc.WaitForExit(60_000), "dxc timed out");
            Assert.True(proc.ExitCode == 0, $"dxc failed for {name}: {stderr}");

            string dxcText = File.ReadAllText(Path.Combine(dir, "out.txt"));
            dxcText = string.Join('\n', dxcText.Split('\n').Where(l => !LineMarker().IsMatch(l)));
            string[] expected = Lexer.Lex(new SourceFile("dxc.txt", dxcText)).Select(t => t.Text).ToArray();

            var options = new PreprocessorOptions();
            c.Configure?.Invoke(options);
            var includeDirs = c.DxcArgs is not null && c.DxcArgs.Contains("-I") ? new[] { "sys" } : null;
            (PreprocessResult result, DiagnosticBag diags) = Pp.Run(c.Main, c.Files, options, "main.hlsl", includeDirs);
            Assert.Empty(Pp.Errors(diags));

            string[] actual = Lexer.Lex(new SourceFile("ours.txt", result.ToText())).Select(t => t.Text).ToArray();
            Assert.Equal(expected, actual);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [GeneratedRegex(@"^#line \d+")]
    private static partial Regex LineMarker();
}
