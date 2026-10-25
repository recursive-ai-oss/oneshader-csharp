using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    private static CompileResult Compile(string name, bool optimize) =>
        ShaderCompiler.Compile(File.ReadAllText(Path.Combine(Dir(), name)), name, new CompileOptions
        {
            Targets = [CompileTarget.SpirV, CompileTarget.Msl, CompileTarget.Wgsl],
            Optimize = optimize,
            IncludeResolver = (_, _, _) => null,
        });

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
    public void AMemberFunctionCannotBeAnEntryPoint()
    {
        var r = Compile("entry-point-is-member-function.hlsl", true);
        Assert.Contains(r.Diagnostics, d => d.Code == "OSH0320" && d.Message.Contains("member function", StringComparison.Ordinal));
    }
}
