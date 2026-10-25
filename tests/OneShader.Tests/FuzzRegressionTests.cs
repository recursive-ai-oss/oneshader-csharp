using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OneShader.Diagnostics;
using System.Runtime.CompilerServices;
using Xunit;

namespace OneShader.Tests;

/// <summary>
/// Replays every input in fuzz/regressions: inputs the fuzzer found that crashed the compiler or produced output a validator rejected.
/// Each must compile or be rejected with ordinary diagnostics, never an internal error, and compile to the same bytes twice.
/// </summary>
public class FuzzRegressionTests
{
    private static string Dir([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "fuzz", "regressions"));

    public static IEnumerable<TheoryDataRow<string>> Inputs() =>
        Directory.GetFiles(Dir(), "*.hlsl").OrderBy(p => p, StringComparer.Ordinal).Select(p => new TheoryDataRow<string>(Path.GetFileName(p)));

    // As in the fuzz runner: U+0001 starts the next include file (f1.h, f2.h, ...).
    private static CompileResult Compile(string name, bool optimize)
    {
        string[] parts = File.ReadAllText(Path.Combine(Dir(), name)).Split('\u0001');
        return ShaderCompiler.Compile(parts[0], name, new CompileOptions
        {
            Targets = [CompileTarget.SpirV, CompileTarget.Msl, CompileTarget.Wgsl],
            Optimize = optimize,
            IncludeResolver = (requested, _, _) =>
            {
                for (int i = 1; i < parts.Length; i++)
                {
                    if (requested == "f" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".h")
                    {
                        return new ResolvedInclude(requested, parts[i]);
                    }
                }

                return null;
            },
        });
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void CompilesWithoutInternalErrorAndDeterministically(string name)
    {
        foreach (bool optimize in new[] { true, false })
        {
            var a = Compile(name, optimize);
            var b = Compile(name, optimize);
            Assert.DoesNotContain(a.Diagnostics, d => d.Code is "OSH7000" or "OSH6001");
            Assert.DoesNotContain(a.Diagnostics, d => d.Code == "OSH0399");
            Assert.Equal(a.Diagnostics.Select(d => d.ToString()), b.Diagnostics.Select(d => d.ToString()));
            Assert.Equal(a.Blobs.Count, b.Blobs.Count);
            for (int i = 0; i < a.Blobs.Count; i++)
            {
                Assert.True(a.Blobs[i].Data.SequenceEqual(b.Blobs[i].Data), name + ": blob " + i + " differs between two compiles");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void AcceptedOutputPassesTheValidators(string name)
    {
        string? spirvVal = Oracles.Find("spirv-val");
        string? naga = Oracles.Find("naga");
        Assert.SkipWhen(spirvVal is null || naga is null, "spirv-val and naga are not installed");
        string work = Path.Combine(Path.GetTempPath(), "osh-fuzzreg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            foreach (bool optimize in new[] { true, false })
            {
                var r = Compile(name, optimize);
                if (!r.Success)
                {
                    continue;
                }

                foreach (var blob in r.Blobs.Where(b => b.Target != CompileTarget.Msl))
                {
                    string file = Path.Combine(work, "out" + (blob.Target == CompileTarget.SpirV ? ".spv" : ".wgsl"));
                    File.WriteAllBytes(file, blob.ToArray());
                    var (code, output) = blob.Target == CompileTarget.SpirV
                        ? Oracles.Run(spirvVal!, ["--target-env", "vulkan1.2", file], work)
                        : Oracles.Run(naga!, [file], work);
                    Assert.True(code == 0, name + " " + blob.Target + " (optimize " + optimize + "):\n" + output);
                }
            }
        }
        finally
        {
            Directory.Delete(work, true);
        }
    }

    [Theory]
    [InlineData("wgsl-scalar-normalize.hlsl")]
    [InlineData("wgsl-integer-divide-by-constant-zero.hlsl")]
    [InlineData("wgsl-constant-index-past-array-end.hlsl")]
    [InlineData("wgsl-constant-index-overflow.hlsl")]
    [InlineData("spirv-negative-constant-index.hlsl")]
    [InlineData("compute-thread-id-with-fewer-components.hlsl")]
    [InlineData("compute-thread-id-one-component.hlsl")]
    [InlineData("matrix-componentwise-intrinsics.hlsl")]
    [InlineData("scalar-swizzle-assignment.hlsl")]
    [InlineData("conditional-selects-one-component-and-struct.hlsl")]
    public void ValidShadersWithUndefinedConstantsStillCompileOnEveryTarget(string name)
    {
        var r = Compile(name, true);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        Assert.Equal(3, r.Blobs.Count);
    }

    [Fact]
    public void TwoInputsOnOneVertexLocationAreRejected()
    {
        var r = Compile("duplicate-vertex-input-location.hlsl", true);
        Assert.Contains(r.Diagnostics, d => d.Code == "OSH0321" && d.Message.Contains("already uses", StringComparison.Ordinal));
    }

    [Fact]
    public void ALocationOutOfRangeIsRejected()
    {
        var r = Compile("vertex-location-out-of-range.hlsl", true);
        Assert.Contains(r.Diagnostics, d => d.Code == "OSH0321" && d.Message.Contains("out of range", StringComparison.Ordinal));
    }

    [Fact]
    public void ARenderTargetIndexOutOfRangeIsRejected()
    {
        var r = Compile("render-target-index-out-of-range.hlsl", true);
        Assert.Contains(r.Diagnostics, d => d.Code == "OSH0321" && d.Message.Contains("out of range", StringComparison.Ordinal));
    }

    [Fact]
    public void SvPositionThatIsNotAFloat4IsRejected()
    {
        var r = Compile("sv-position-not-float4.hlsl", true);
        Assert.Contains(r.Diagnostics, d => d.Code == "OSH0321" && d.Message.Contains("float4", StringComparison.Ordinal));
    }

    [Fact]
    public void AStructHoldingAnArrayOfItselfIsRejectedWithoutOverflowingTheStack()
    {
        var r = Compile("struct-contains-array-of-itself.hlsl", true);
        Assert.False(r.Success);
        Assert.DoesNotContain(r.Diagnostics, d => d.Code is "OSH7000" or "OSH0399");
    }

    [Fact]
    public void AnArrayInitializerOverTheLimitIsRejected()
    {
        var r = Compile("huge-array-initializer.hlsl", true);
        Assert.Contains(r.Diagnostics, d => d.Code == "OSH0353");
        Assert.DoesNotContain(r.Diagnostics, d => d.Code is "OSH7000" or "OSH0399");
    }

    [Fact]
    public void AnUnassignedLocalResourceIsOneErrorNotAnInternalFailure()
    {
        var r = Compile("unassigned-local-resource.hlsl", true);
        Assert.Contains(r.Diagnostics, d => d.Code == "OSH0331");
        Assert.DoesNotContain(r.Diagnostics, d => d.Code is "OSH7000" or "OSH0399");
    }

    [Fact]
    public void ARegisterNumberOutOfRangeIsRejected()
    {
        var r = Compile("register-number-out-of-range.hlsl", true);
        Assert.Contains(r.Diagnostics, d => d.Code == "OSH0331" && d.Message.Contains("between 0 and", StringComparison.Ordinal));
    }

    [Fact]
    public void AssigningAnElementOfASwizzleIsOneError()
    {
        var r = Compile("swizzle-element-assignment.hlsl", true);
        Assert.Contains(r.Diagnostics, d => d.Message.Contains("swizzle cannot be assigned", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Diagnostics, d => d.Code is "OSH7000" or "OSH0399");
    }

    [Fact]
    public void AMemberFunctionCannotBeAnEntryPoint()
    {
        var r = Compile("entry-point-is-member-function.hlsl", true);
        Assert.Contains(r.Diagnostics, d => d.Code == "OSH0320" && d.Message.Contains("member function", StringComparison.Ordinal));
    }

    // ---- generated inputs: sizes that cost memory, stack or time if any stage is worse than linear ----

    private static CompileResult CompileText(string text, params CompileTarget[] targets) =>
        ShaderCompiler.Compile(text, "gen.hlsl", new CompileOptions { Targets = targets.Length == 0 ? [CompileTarget.SpirV, CompileTarget.Msl, CompileTarget.Wgsl] : targets });

    private static void AssertNoInternalError(CompileResult r) =>
        Assert.DoesNotContain(r.Diagnostics, d => d.Code is "OSH7000" or "OSH6001" or "OSH0399");

    [Fact]
    public void ExponentialMacroStopsAtTheLimitWithOneError()
    {
        string text = string.Concat(Enumerable.Range(1, 39).Select(i => $"#define M{i} M{i - 1} M{i - 1}\n")) + "#define M0 1\nfloat f() { return M39; }";
        var r = CompileText(text);
        AssertNoInternalError(r);
        Assert.Contains(r.Diagnostics, d => d.Code == "OSH0128");
        Assert.Single(r.Diagnostics);
    }

    [Fact]
    public void AnArrayWithThousandsOfDimensionsIsRejectedWithoutOverflowingTheStack()
    {
        var r = CompileText("void f() { float x" + string.Concat(Enumerable.Repeat("[2]", 100_000)) + "; }");
        AssertNoInternalError(r);
        Assert.Contains(r.Diagnostics, d => d.Code == "OSH0353");
    }

    [Fact]
    public void ALongChainOfCallsCompilesInLinearTime()
    {
        const int n = 20_000;
        string text = "float f0(float x) { return x; }\n"
            + string.Concat(Enumerable.Range(1, n - 1).Select(i => $"float f{i}(float x) {{ return f{i - 1}(x); }}\n"))
            + $"RWStructuredBuffer<float> O;\n[shader(\"compute\")] [numthreads(1, 1, 1)] void m() {{ O[0] = f{n - 1}(1.0); }}";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var r = CompileText(text);
        watch.Stop();
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Take(3).Select(d => d.ToString())));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), "took " + watch.Elapsed);
    }

    [Fact]
    public void ThousandsOfErrorsAreCutOffAfterTheLimit()
    {
        string text = string.Concat(Enumerable.Range(0, 20_000).Select(i => $"float f{i}() {{ return y{i}; }}\n"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var r = CompileText(text);
        watch.Stop();
        AssertNoInternalError(r);
        Assert.Equal(DiagnosticBag.MaxErrors + 1, r.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Equal(DiagnosticBag.TooManyErrors, r.Diagnostics[^1].Code);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "took " + watch.Elapsed);
    }
}
