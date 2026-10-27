using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using OneShader.Types;
using Xunit;

namespace OneShader.Tests;

/// <summary><see cref="ShaderCompileCache"/>: hits and misses, includes, concurrency, bounds, options.</summary>
public sealed class ShaderCompileCacheTests : IDisposable
{
    private const string Pixel = "cbuffer Frame : register(b0) { float4 Tint; };\n#ifdef FAST\nstatic const float K = 0.5;\n#else\nstatic const float K = 1.0;\n#endif\n"
        + "[shader(\"pixel\")] float4 PS() : SV_Target { return Tint * K; }\n";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osh-cache-" + Guid.NewGuid().ToString("N"));

    public ShaderCompileCacheTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    // Every wait in these tests ends: a stuck cache fails the test with a message instead of hanging the run.
    private static async Task<T> Bounded<T>(Task<T> task, string what)
    {
        try
        {
            return await task.WaitAsync(Limit, Ct);
        }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException(what + " did not finish within " + Limit.TotalSeconds + " s");
        }
    }

    private static CompileOptions Defines(params string[] names) =>
        new() { Defines = names.Select(n => new KeyValuePair<string, string?>(n, null)).ToList() };

    [Fact]
    public void TheSameInputsAreCompiledOnceAndGiveTheBytesOfADirectCompile()
    {
        var cache = new ShaderCompileCache();
        var first = cache.Compile(Pixel, "a.hlsl", Defines("FAST"), Ct);
        var second = cache.Compile(Pixel, "a.hlsl", Defines("FAST"), Ct);
        Assert.Same(first, second);
        Assert.Equal((1L, 1L), (cache.Hits, cache.Misses));
        var direct = ShaderCompiler.Compile(Pixel, "a.hlsl", Defines("FAST"), Ct);
        Assert.Equal(direct.Find("PS", CompileTarget.SpirV)!.ToArray(), first.Find("PS", CompileTarget.SpirV)!.ToArray());

        // Any difference in the key is a miss.
        cache.Compile(Pixel, "a.hlsl", Defines(), Ct);
        cache.Compile(Pixel, "b.hlsl", Defines("FAST"), Ct);
        cache.Compile(Pixel + " ", "a.hlsl", Defines("FAST"), Ct);
        Assert.Equal((1L, 4L), (cache.Hits, cache.Misses));
        Assert.Equal(4, cache.Count);
    }

    [Fact]
    public void EveryOptionIsPartOfTheKey()
    {
        var cache = new ShaderCompileCache();
        var variants = new CompileOptions[]
        {
            new(),
            new() { Targets = [CompileTarget.SpirV, CompileTarget.Wgsl] },
            new() { Targets = [CompileTarget.Wgsl, CompileTarget.SpirV] },
            new() { Defines = [new("A", "1")] },
            new() { Defines = [new("A", "2")] },
            new() { Defines = [new("A", null)] },
            new() { IncludeDirectories = [_dir] },
            new() { EntryPoints = [new EntryPointRequest("PS", ShaderStage.Pixel)] },
            new() { DefaultMatrixPacking = MatrixPacking.RowMajor },
            new() { Enable16BitTypes = true },
            new() { Optimize = false },
            new() { KeepNames = false },
            new() { SpecializationOverrides = [new("A", 1)] },
            new() { SpecializationOverrides = [new("A", 2)] },
        };
        foreach (var o in variants)
        {
            cache.Compile(Pixel, "a.hlsl", o, Ct);
        }

        Assert.Equal((0L, (long)variants.Length), (cache.Hits, cache.Misses));
        foreach (var o in variants)
        {
            cache.Compile(Pixel, "a.hlsl", o, Ct);
        }

        Assert.Equal(((long)variants.Length, (long)variants.Length), (cache.Hits, cache.Misses));
    }

    [Fact]
    public void ANewOptionOnCompileOptionsMustBeAddedToTheKey()
    {
        // The cache copies and hashes each option by name. If this list changes, update ShaderCompileCache.KeyOf and WithResolver, then this list.
        string[] known = ["DefaultMatrixPacking", "Defines", "Enable16BitTypes", "EntryPoints", "IncludeDirectories", "IncludeResolver", "KeepNames", "Optimize", "SpecializationOverrides", "Targets"];
        string[] actual = typeof(CompileOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(known, actual);
    }

    [Fact]
    public void ChangingAnIncludeMissesAndAnUnchangedOneHits()
    {
        var files = new Dictionary<string, string> { ["common.hlsli"] = "#define SCALE 1.0\n" };
        ResolvedInclude? Resolve(string name, string including, bool system) =>
            files.TryGetValue(name, out string? text) ? new ResolvedInclude(name, text) : null;
        var options = new CompileOptions { IncludeResolver = Resolve };
        const string source = "#include \"common.hlsli\"\n[shader(\"pixel\")] float4 PS() : SV_Target { return SCALE; }\n";

        var cache = new ShaderCompileCache();
        var first = cache.Compile(source, "a.hlsl", options, Ct);
        Assert.True(first.Success);
        Assert.Same(first, cache.Compile(source, "a.hlsl", options, Ct));
        Assert.Equal((1L, 1L), (cache.Hits, cache.Misses));

        files["common.hlsli"] = "#define SCALE 2.0\n";
        var changed = cache.Compile(source, "a.hlsl", options, Ct);
        Assert.NotSame(first, changed);
        Assert.NotEqual(first.Find("PS", CompileTarget.SpirV)!.ToArray(), changed.Find("PS", CompileTarget.SpirV)!.ToArray());
        Assert.Equal((1L, 2L), (cache.Hits, cache.Misses));
        Assert.Same(changed, cache.Compile(source, "a.hlsl", options, Ct));

        // An include that disappears is a miss, and its error is reported (and then cached).
        files.Remove("common.hlsli");
        var missing = cache.Compile(source, "a.hlsl", options, Ct);
        Assert.False(missing.Success);
        Assert.Same(missing, cache.Compile(source, "a.hlsl", options, Ct));
        Assert.Equal((3L, 3L), (cache.Hits, cache.Misses));
    }

    [Fact]
    public void FileSystemIncludesAreWatchedIncludingOnesThatAppearEarlierOnTheSearchPath()
    {
        string first = Path.Combine(_dir, "first");
        string second = Path.Combine(_dir, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(second, "lib.hlsli"), "#define SCALE 1.0\n");
        string shader = Path.Combine(_dir, "main.hlsl");
        File.WriteAllText(shader, "#include <lib.hlsli>\n[shader(\"pixel\")] float4 PS() : SV_Target { return SCALE; }\n");
        var options = new CompileOptions { IncludeDirectories = [first, second] };

        var cache = new ShaderCompileCache();
        var a = cache.CompileFile(shader, options, Ct);
        Assert.True(a.Success, string.Join("\n", a.Diagnostics));
        Assert.Equal(Path.GetFullPath(shader), a.Files[0]);
        Assert.Same(a, cache.CompileFile(shader, options, Ct));

        // A file in an earlier directory now shadows the one the compile used.
        File.WriteAllText(Path.Combine(first, "lib.hlsli"), "#define SCALE 3.0\n");
        var b = cache.CompileFile(shader, options, Ct);
        Assert.NotSame(a, b);
        Assert.NotEqual(a.Find("PS", CompileTarget.SpirV)!.ToArray(), b.Find("PS", CompileTarget.SpirV)!.ToArray());

        // Editing the shader itself is a miss too, and the result equals a direct compile of the file.
        File.WriteAllText(shader, File.ReadAllText(shader).Replace("return SCALE", "return SCALE * 2", StringComparison.Ordinal));
        var c = cache.CompileFile(shader, options, Ct);
        Assert.NotSame(b, c);
        var direct = ShaderCompiler.CompileFile(shader, options, Ct);
        Assert.Equal(direct.Find("PS", CompileTarget.SpirV)!.ToArray(), c.Find("PS", CompileTarget.SpirV)!.ToArray());
        Assert.Equal(direct.Files, c.Files);
        Assert.Equal((1L, 3L), (cache.Hits, cache.Misses));

        // A missing file is not cached.
        var gone = cache.CompileFile(Path.Combine(_dir, "nope.hlsl"), null, Ct);
        Assert.False(gone.Success);
        Assert.Equal(2, cache.Count);   // one per distinct shader text; a stale result is replaced in its slot
    }

    [Fact]
    public async Task ConcurrentCallsForOneKeyCompileOnce()
    {
        var cache = new ShaderCompileCache();
        int resolverCalls = 0;
        var options = new CompileOptions
        {
            IncludeResolver = (name, including, system) =>
            {
                // Slow enough that the other callers are certainly waiting while the first compiles.
                if (Interlocked.Increment(ref resolverCalls) == 1)
                {
                    Thread.Sleep(200);
                }

                return new ResolvedInclude(name, "#define SCALE 1.0\n");
            },
        };
        const string source = "#include \"x.hlsli\"\n[shader(\"pixel\")] float4 PS() : SV_Target { return SCALE; }\n";
        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() => cache.Compile(source, "a.hlsl", options, Ct), Ct)).ToArray();
        var results = await Bounded(Task.WhenAll(tasks), "16 concurrent compiles of one key");
        Assert.All(results, r => Assert.Same(results[0], r));
        Assert.Equal(1L, cache.Misses);
        Assert.Equal(15L, cache.Hits);

        // Different keys compile independently.
        var many = await Bounded(Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => cache.Compile(Pixel, "k" + i + ".hlsl", null, Ct), Ct))), "8 compiles of different keys");
        Assert.Equal(8, many.Distinct().Count());
        Assert.Equal(9L, cache.Misses);
    }

    [Fact]
    public void ClearEmptiesTheCacheAndTheNextCallCompiles()
    {
        var cache = new ShaderCompileCache();
        var a = cache.Compile(Pixel, "a.hlsl", null, Ct);
        Assert.Equal(1, cache.Count);
        cache.Clear();
        Assert.Equal(0, cache.Count);
        var b = cache.Compile(Pixel, "a.hlsl", null, Ct);
        Assert.NotSame(a, b);
        Assert.Equal(a.Find("PS", CompileTarget.SpirV)!.ToArray(), b.Find("PS", CompileTarget.SpirV)!.ToArray());
        Assert.Equal((0L, 2L), (cache.Hits, cache.Misses));
    }

    [Fact]
    public void TheCacheIsBoundedAndDropsTheLeastRecentlyUsed()
    {
        var cache = new ShaderCompileCache(2);
        var a = cache.Compile(Pixel, "a.hlsl", null, Ct);
        cache.Compile(Pixel, "b.hlsl", null, Ct);
        Assert.Same(a, cache.Compile(Pixel, "a.hlsl", null, Ct));   // a is now newer than b
        cache.Compile(Pixel, "c.hlsl", null, Ct);                   // evicts b
        Assert.Equal(2, cache.Count);
        Assert.Same(a, cache.Compile(Pixel, "a.hlsl", null, Ct));
        long misses = cache.Misses;
        cache.Compile(Pixel, "b.hlsl", null, Ct);
        Assert.Equal(misses + 1, cache.Misses);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShaderCompileCache(0));
    }

    [Fact]
    public async Task ErrorsAreCachedAndCancellationIsNot()
    {
        var cache = new ShaderCompileCache();
        const string broken = "[shader(\"pixel\")] float4 PS() : SV_Target { return missing; }\n";
        var a = cache.Compile(broken, "a.hlsl", null, Ct);
        Assert.False(a.Success);
        Assert.Same(a, cache.Compile(broken, "a.hlsl", null, Ct));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => cache.Compile(Pixel, "z.hlsl", null, cancelled.Token));
        Assert.Equal(1, cache.Count);

        // A waiter can give up while another caller compiles. The compiling caller signals when it is inside the compile,
        // so the waiter certainly arrives second; the gate wait is bounded.
        using var gate = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var slow = new CompileOptions
        {
            IncludeResolver = (name, including, system) =>
            {
                started.Set();
                Assert.True(gate.Wait(Limit, Ct), "the test did not release the compile");
                return new ResolvedInclude(name, string.Empty);
            },
        };
        const string withInclude = "#include \"x\"\n[shader(\"pixel\")] float4 PS() : SV_Target { return 0; }\n";
        var running = Task.Run(() => cache.Compile(withInclude, "s.hlsl", slow, Ct), Ct);
        try
        {
            Assert.True(started.Wait(Limit, Ct), "the first compile never started");
            using var impatient = new CancellationTokenSource(100);
            Assert.ThrowsAny<OperationCanceledException>(() => cache.Compile(withInclude, "s.hlsl", slow, impatient.Token));
        }
        finally
        {
            gate.Set();
        }

        Assert.True((await Bounded(running, "the first compile")).Success);

        Assert.Throws<ArgumentNullException>(() => cache.Compile(null!, "a.hlsl", null, Ct));
        Assert.Throws<ArgumentNullException>(() => cache.CompileFile(null!, null, Ct));
    }
}
