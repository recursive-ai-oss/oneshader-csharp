using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OneShader.Lexing;
using OneShader.Preprocessing;
using OneShader.Semantics;

namespace OneShader;

/// <summary>
/// Remembers compile results so a host that compiles many variants of the same shaders (a hot-reload service, a tool, a material
/// system building permutations on demand) does not compile identical inputs twice. A result is reused when the source text, the
/// path, every option (targets, defines, include directories, entry points, packing, 16-bit types, optimization, names) and the
/// contents of every file the earlier compile included are the same. Included files are looked up again on every call, so editing
/// an include makes the next call compile; a file appearing earlier on the include search path does too.
/// </summary>
/// <remarks>
/// <para>
/// Thread-safe. Callers asking for the same key at the same time share one compile: the first compiles, the others wait and get
/// its result. Different keys compile in parallel. The cache holds at most <c>maxEntries</c> results and drops the least recently
/// used one beyond that; <see cref="Clear"/> empties it.
/// </para>
/// <para>
/// A cached call returns the same <see cref="CompileResult"/> object each time (and the same bytes a direct
/// <see cref="ShaderCompiler.Compile"/> would produce), so treat results as read-only. Compile errors are cached like successes;
/// a cancelled call caches nothing.
/// </para>
/// </remarks>
public sealed class ShaderCompileCache
{
    /// <summary>The number of results kept when no limit is given.</summary>
    public const int DefaultMaxEntries = 256;

    private readonly ConcurrentDictionary<string, Slot> _slots = new ConcurrentDictionary<string, Slot>(StringComparer.Ordinal);
    private readonly object _trimLock = new object();
    private readonly int _maxEntries;
    private long _clock;
    private long _hits;
    private long _misses;

    /// <summary>Creates an empty cache.</summary>
    /// <param name="maxEntries">The most results to keep; at least 1.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxEntries"/> is less than 1.</exception>
    public ShaderCompileCache(int maxEntries = DefaultMaxEntries)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        _maxEntries = maxEntries;
    }

    /// <summary>The number of results currently held (including a compile in progress).</summary>
    public int Count => _slots.Count;

    /// <summary>How many calls were answered from the cache.</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>How many calls compiled.</summary>
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>Drops every result. A call in progress finishes and may store its result again.</summary>
    public void Clear() => _slots.Clear();

    /// <summary>
    /// Like <see cref="ShaderCompiler.Compile"/>, answered from the cache when the same inputs were compiled before.
    /// Throws <see cref="ArgumentNullException"/> for null arguments and <see cref="OperationCanceledException"/> when cancelled
    /// (also while waiting for another caller's compile of the same key).
    /// </summary>
    /// <param name="source">The HLSL text.</param>
    /// <param name="path">The file path as the user knows it; used in diagnostics and to resolve includes.</param>
    /// <param name="options">Settings; defaults apply when null.</param>
    /// <param name="cancellationToken">Cancels the compile or the wait.</param>
    public CompileResult Compile(string source, string path, CompileOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);
        return Run(source, path, null, options ?? new CompileOptions(), cancellationToken);
    }

    /// <summary>
    /// Like <see cref="ShaderCompiler.CompileFile"/>, answered from the cache when the file's text and everything else is unchanged.
    /// The file is read on every call (a changed file compiles again); a file that cannot be read is error
    /// <see cref="ApiDiagnosticCodes.FileNotReadable"/> and is not cached.
    /// </summary>
    /// <param name="path">The file to compile; relative paths are relative to the current directory.</param>
    /// <param name="options">Settings; defaults apply when null.</param>
    /// <param name="cancellationToken">Cancels the compile or the wait.</param>
    public CompileResult CompileFile(string path, CompileOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();
        string full;
        string text;
        try
        {
            full = System.IO.Path.GetFullPath(path);
            text = System.IO.File.ReadAllText(full);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // Not cacheable: the answer is about the file system, not about the text. The direct call builds the diagnostic.
            return ShaderCompiler.CompileFile(path, options, cancellationToken);
        }

        return Run(text, path, full, options ?? new CompileOptions(), cancellationToken);
    }

    private CompileResult Run(string source, string path, string? fullPath, CompileOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string key = KeyOf(source, path, fullPath, options);
        var inner = Frontend.CreateResolver(options);
        var slot = _slots.GetOrAdd(key, static _ => new Slot());
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            CompileResult? cached;
            List<Probe> probes;
            lock (slot)
            {
                cached = slot.Result;
                probes = slot.Probes;
            }

            if (cached is not null && StillValid(probes, inner))
            {
                slot.Stamp = Interlocked.Increment(ref _clock);
                Interlocked.Increment(ref _hits);
                return cached;
            }

            // One caller per key compiles; the others wait on its task (and can give up through their token).
            TaskCompletionSource<CompileResult>? running;
            TaskCompletionSource<CompileResult>? mine = null;
            lock (slot)
            {
                running = slot.Running;
                if (running is null)
                {
                    mine = slot.Running = new TaskCompletionSource<CompileResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }

            if (mine is null)
            {
                CompileResult shared;
                try
                {
                    shared = running!.Task.WaitAsync(ct).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    continue; // the compiling caller was cancelled; try again, possibly compiling ourselves
                }

                Interlocked.Increment(ref _hits);
                return shared;
            }

            try
            {
                var recorder = new RecordingResolver(inner);
                var result = ShaderCompiler.Compile(source, path, WithResolver(options, recorder), ct);
                if (fullPath is not null)
                {
                    // As CompileFile does: the watched list names the shader by its full path.
                    result.Files = new List<string>(result.Files) { [0] = fullPath };
                }

                lock (slot)
                {
                    slot.Result = result;
                    slot.Probes = recorder.Probes;
                    slot.Running = null;
                }

                slot.Stamp = Interlocked.Increment(ref _clock);
                Interlocked.Increment(ref _misses);
                _slots[key] = slot;
                Trim();
                mine.SetResult(result);
                return result;
            }
            catch (OperationCanceledException)
            {
                lock (slot)
                {
                    slot.Running = null;
                }

                mine.SetCanceled(ct);
                throw;
            }
            catch (Exception ex)
            {
                lock (slot)
                {
                    slot.Running = null;
                }

                mine.SetException(ex);
                throw;
            }
        }
    }

    /// <summary>True when every include lookup of the earlier compile still finds the same file with the same text.</summary>
    private static bool StillValid(List<Probe> probes, IIncludeResolver resolver)
    {
        foreach (var p in probes)
        {
            SourceFile? now;
            try
            {
                now = resolver.Resolve(p.Name, p.IsAngled, new SourceFile(p.Includer, string.Empty), new List<string>());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return false;
            }

            if (now is null)
            {
                if (p.Identity is not null)
                {
                    return false;
                }

                continue;
            }

            if (p.Identity is null
                || !string.Equals(now.Identity, p.Identity, StringComparison.Ordinal)
                || !string.Equals(now.Path, p.Path, StringComparison.Ordinal)
                || p.Hash is not { } expected
                || !HashOf(now.Text).AsSpan().SequenceEqual(expected))
            {
                return false;
            }
        }

        return true;
    }

    private void Trim()
    {
        if (_slots.Count <= _maxEntries)
        {
            return;
        }

        lock (_trimLock)
        {
            while (_slots.Count > _maxEntries)
            {
                string? oldest = null;
                long oldestStamp = long.MaxValue;
                foreach (var pair in _slots)
                {
                    long stamp = Interlocked.Read(ref pair.Value.Stamp);
                    if (stamp < oldestStamp)
                    {
                        oldestStamp = stamp;
                        oldest = pair.Key;
                    }
                }

                if (oldest is null || !_slots.TryRemove(oldest, out _))
                {
                    return;
                }
            }
        }
    }

    // Every option that can change a compile's output. A test fails when CompileOptions gains a property this does not cover.
    private static CompileOptions WithResolver(CompileOptions o, IIncludeResolver resolver) => new CompileOptions
    {
        Targets = o.Targets,
        Defines = o.Defines,
        IncludeDirectories = o.IncludeDirectories,
        IncludeResolver = o.IncludeResolver,
        EntryPoints = o.EntryPoints,
        DefaultMatrixPacking = o.DefaultMatrixPacking,
        Enable16BitTypes = o.Enable16BitTypes,
        Optimize = o.Optimize,
        KeepNames = o.KeepNames,
        SpecializationOverrides = o.SpecializationOverrides,
        ResourceHeapBinding = o.ResourceHeapBinding,
        SamplerHeapBinding = o.SamplerHeapBinding,
        ResolverOverride = resolver,
    };

    private static string KeyOf(string source, string path, string? fullPath, CompileOptions o)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Put(h, fullPath is null ? "text" : "file");
        Put(h, path);
        Put(h, fullPath ?? string.Empty);
        Put(h, source);
        Put(h, o.Targets.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var t in o.Targets)
        {
            Put(h, ((int)t).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Put(h, o.Defines.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var d in o.Defines)
        {
            Put(h, d.Key);
            Put(h, d.Value is null ? "\u0001" : "=" + d.Value);
        }

        Put(h, o.IncludeDirectories.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (string dir in o.IncludeDirectories)
        {
            Put(h, dir);
        }

        Put(h, o.EntryPoints.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var e in o.EntryPoints)
        {
            Put(h, e.Name);
            Put(h, ((int)e.Stage).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Put(h, ((int)o.DefaultMatrixPacking).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Put(h, o.Enable16BitTypes ? "16" : "32");
        Put(h, o.Optimize ? "opt" : "noopt");
        Put(h, o.KeepNames ? "names" : "nonames");
        Put(h, o.SpecializationOverrides.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var s in o.SpecializationOverrides)
        {
            Put(h, s.Key);
            Put(h, BitConverter.DoubleToInt64Bits(s.Value).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Put(h, o.ResourceHeapBinding is { } rh ? rh.Set.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + rh.Binding.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-");
        Put(h, o.SamplerHeapBinding is { } sh ? sh.Set.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + sh.Binding.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-");
        return Convert.ToHexString(h.GetHashAndReset());
    }

    // Length-prefixed, so "ab"+"c" and "a"+"bc" differ.
    private static void Put(IncrementalHash h, string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        h.AppendData(BitConverter.GetBytes(bytes.Length));
        h.AppendData(bytes);
    }

    private static byte[] HashOf(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    /// <summary>One include lookup a compile made and what it found.</summary>
    private sealed class Probe(string name, bool isAngled, string includer, string? path, string? identity, byte[]? hash)
    {
        public string Name { get; } = name;

        public bool IsAngled { get; } = isAngled;

        public string Includer { get; } = includer;

        public string? Path { get; } = path;

        public string? Identity { get; } = identity;

        public byte[]? Hash { get; } = hash;
    }

    /// <summary>Wraps the real resolver and remembers each distinct lookup with the content it returned.</summary>
    private sealed class RecordingResolver(IIncludeResolver inner) : IIncludeResolver
    {
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);

        public List<Probe> Probes { get; } = [];

        public SourceFile? Resolve(string name, bool isAngled, SourceFile includer, IList<string> searched)
        {
            var file = inner.Resolve(name, isAngled, includer, searched);
            if (_seen.Add((isAngled ? "<" : "\"") + name + "\u0001" + includer.Path))
            {
                Probes.Add(new Probe(name, isAngled, includer.Path, file?.Path, file?.Identity, file is null ? null : HashOf(file.Text)));
            }

            return file;
        }
    }

    private sealed class Slot
    {
        public CompileResult? Result;
        public List<Probe> Probes = [];
        public TaskCompletionSource<CompileResult>? Running;
        public long Stamp;
    }
}
