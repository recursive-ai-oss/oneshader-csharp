using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

// Fuzz targets for the compiler. See fuzz/README.md.
//
//   dotnet OneShader.Fuzz.dll --file <path> [target]      replay one input through a target (default: all); exit 1 on a finding
//   dotnet OneShader.Fuzz.dll --diagnostics <path>        print the diagnostics for one input
//   dotnet OneShader.Fuzz.dll --time <path>               compile time per target and optimizer setting
//   dotnet OneShader.Fuzz.dll --run <target> --seconds N [--seed S] [--corpus dir] [--out dir] [--tools dir] [--sample N]
//                                                          mutation fuzzing in-process, coverage-guided when OneShader.dll is instrumented
//   dotnet OneShader.Fuzz.dll --libfuzzer <target>         SharpFuzz/libFuzzer loop on stdin (needs libfuzzer-dotnet)
//
// Targets: pp, frontend, optimizer, backends, validity (backends + spirv-val/naga on the output), all.
string Arg(string name, string fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

if (args.Length >= 2 && args[0] == "--file")
{
    string target = args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal) ? args[2] : "all";
    string tools = Arg("--tools", Environment.GetEnvironmentVariable("ONESHADER_TOOLS") ?? ".tools");
    if (target is "all" or "validity")
    {
        Validator.OutDir = Arg("--out", Path.Combine(Path.GetTempPath(), "osh-replay"));
        if (Directory.Exists(tools))
        {
            Validator.Init(tools);
            Targets.Validate = true;
        }
    }

    try
    {
        Targets.Get(target)(File.ReadAllBytes(args[1]));
    }
    catch (Exception ex)
    {
        Console.WriteLine("FINDING " + (ex is FindingException f ? f.Kind : ex.GetType().Name) + ": " + ex.Message);
        return 1;
    }

    Console.WriteLine("ok");
    return 0;
}

if (args.Length >= 2 && args[0] == "--diagnostics")
{
    // Prints what the compiler says about one input (all targets), for triage and for minimizing a reproducer.
    var input = new FuzzInput(File.ReadAllText(args[1]));
    foreach (var d in OneShader.ShaderCompiler.Compile(input.Main, "fuzz.hlsl", input.Options(true)).Diagnostics)
    {
        Console.WriteLine(d);
    }

    return 0;
}

if (args.Length >= 2 && args[0] == "--time")
{
    // Compile time of one input per target and optimizer setting, for finding the input that makes a stage slow.
    var input = new FuzzInput(File.ReadAllText(args[1]));
    foreach (bool optimize in new[] { false, true })
    {
        foreach (var target in new[] { OneShader.CompileTarget.SpirV, OneShader.CompileTarget.Msl, OneShader.CompileTarget.Wgsl })
        {
            var watch = Stopwatch.StartNew();
            var r = OneShader.ShaderCompiler.Compile(input.Main, "fuzz.hlsl", input.Options(optimize, target));
            Console.WriteLine($"optimize={optimize} {target}: {watch.ElapsedMilliseconds} ms, success={r.Success}");
        }
    }

    return 0;
}

if (args.Length >= 2 && args[0] == "--libfuzzer")
{
    var run = Targets.Get(args[1]);
    SharpFuzz.Fuzzer.LibFuzzer.Run(span => run(span));
    return 0;
}

if (args.Length >= 2 && args[0] == "--run")
{
    var c = new Campaign(
        args[1],
        TimeSpan.FromSeconds(double.Parse(Arg("--seconds", "60"), CultureInfo.InvariantCulture)),
        int.Parse(Arg("--seed", "1"), CultureInfo.InvariantCulture),
        Arg("--corpus", "corpus"),
        Arg("--out", "fuzz-out"));
    if (args[1] == "validity")
    {
        Validator.OutDir = c.Out;
        Validator.SampleEvery = int.Parse(Arg("--sample", "1"), CultureInfo.InvariantCulture);
        Validator.Init(Arg("--tools", Environment.GetEnvironmentVariable("ONESHADER_TOOLS") ?? ".tools"));
        Targets.Validate = true;
    }

    return c.Run();
}

Console.WriteLine("usage: --file <path> [target] | --run <target> --seconds N [--seed S] [--corpus dir] [--out dir] | --libfuzzer <target>");
return 2;

internal sealed unsafe class Campaign
{
    private const int MapSize = 65536;
    private const int MaxInputChars = 60_000;
    private const long HangMs = 60_000;

    private readonly string _target;
    private readonly TimeSpan _duration;
    private readonly int _seed;
    private readonly string _corpus;
    private readonly byte* _map;
    private readonly bool[] _seen = new bool[MapSize];
    private readonly Action<ReadOnlySpan<byte>> _run;
    private readonly HashSet<string> _kinds = new(StringComparer.Ordinal);
    private readonly List<string> _queue = [];
    private long _execStart;
    private byte[] _current = [];
    private int _features;

    public Campaign(string target, TimeSpan duration, int seed, string corpus, string outDir)
    {
        _target = target;
        _duration = duration;
        _seed = seed;
        _corpus = corpus;
        Out = outDir;
        _run = Targets.Get(target);
        Directory.CreateDirectory(Path.Combine(Out, "findings"));
        bool instrumented = typeof(OneShader.ShaderCompiler).Assembly.GetReferencedAssemblies().Any(a => a.Name == "SharpFuzz.Common");
        if (instrumented)
        {
            _map = (byte*)NativeMemory.AllocZeroed(MapSize);
            SharpFuzz.Common.Trace.SharedMem = _map;
        }
    }

    public string Out { get; }

    public int Findings { get; private set; }

    public int Run()
    {
        var rng = new Random(_seed);
        var mutator = new Mutator(rng);
        LoadSeeds();
        var total = Stopwatch.StartNew();
        var watchdog = new Thread(Watch) { IsBackground = true, Name = "watchdog" };
        watchdog.Start();
        Console.WriteLine($"target={_target} seed={_seed} seeds={_queue.Count} guided={(_map != null)} seconds={_duration.TotalSeconds:0}");
        int seedCount = _queue.Count;
        long execs = 0;
        long lastReport = 0;
        for (int i = 0; i < seedCount; i++)
        {
            Exec(_queue[i], false);
            execs++;
        }

        while (total.Elapsed < _duration)
        {
            string baseText = _queue[rng.Next(_queue.Count)];
            string text = rng.Next(20) == 0 && _target == "pp" ? RandomPp(rng) : mutator.Mutate(baseText, _queue[rng.Next(_queue.Count)]);
            if (text.Length <= MaxInputChars)
            {
                bool fresh = Exec(text, true);
                execs++;
                if (fresh && _queue.Count < 20_000)
                {
                    _queue.Add(text);
                }
            }

            if (total.ElapsedMilliseconds - lastReport >= 30_000)
            {
                lastReport = total.ElapsedMilliseconds;
                Console.WriteLine($"[{total.Elapsed.TotalSeconds:0}s] execs={execs} ({execs / total.Elapsed.TotalSeconds:0}/s) queue={_queue.Count} features={_features} findings={Findings} unique={_kinds.Count}");
            }
        }

        _execStart = 0;
        Console.WriteLine($"DONE target={_target} seed={_seed} seconds={total.Elapsed.TotalSeconds:0} execs={execs} execs/s={execs / total.Elapsed.TotalSeconds:0} queue={_queue.Count} features={_features} findings={Findings} unique={_kinds.Count}"
            + (Targets.Validate ? $" spirv-val={Targets.ValidatedSpirv} naga={Targets.ValidatedWgsl} msl-samples={Targets.MslSamples}" : string.Empty));
        return Findings == 0 ? 0 : 1;
    }

    private void Watch()
    {
        while (true)
        {
            Thread.Sleep(1000);
            long start = Volatile.Read(ref _execStart);
            if (start != 0 && (Stopwatch.GetTimestamp() - start) * 1000 / Stopwatch.Frequency > HangMs + (Targets.Validate ? 60_000 : 0))
            {
                string path = Save("hang", _current, "no result after " + HangMs + " ms");
                Console.WriteLine("HANG, input saved as " + path);
                Environment.Exit(3);
            }
        }
    }

    private bool Exec(string text, bool mutated)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        _current = bytes;
        // If the process dies (stack overflow, out of memory), the input that did it is the last one written here.
        File.WriteAllBytes(Path.Combine(Out, "current-" + _target + "-" + _seed + ".bin"), bytes);
        Validator.ExternalTicks = 0;
        long start = Stopwatch.GetTimestamp();
        Volatile.Write(ref _execStart, start);
        try
        {
            _run(bytes);
        }
        catch (Exception ex)
        {
            Record(ex, bytes);
        }
        finally
        {
            Volatile.Write(ref _execStart, 0);
        }

        long ms = (Stopwatch.GetTimestamp() - start - Validator.ExternalTicks) * 1000 / Stopwatch.Frequency;
        if (ms > 5000)
        {
            Record(new FindingException("slow", "took " + ms + " ms"), bytes);
        }

        _ = mutated;
        return Harvest();
    }

    private bool Harvest()
    {
        if (_map == null)
        {
            return false;
        }

        bool fresh = false;
        for (int i = 0; i < MapSize; i++)
        {
            if (_map[i] != 0)
            {
                if (!_seen[i])
                {
                    _seen[i] = true;
                    _features++;
                    fresh = true;
                }

                _map[i] = 0;
            }
        }

        return fresh;
    }

    private void Record(Exception ex, byte[] bytes)
    {
        Findings++;
        string kind = ex is FindingException f ? f.Kind : "exception-" + ex.GetType().Name;
        string first = ex.Message.Split('\n')[0];
        string key = kind + "|" + (first.Length > 160 ? first[..160] : first) + (ex is FindingException ? string.Empty : "|" + (ex.StackTrace ?? string.Empty).Split('\n').FirstOrDefault());
        if (_kinds.Add(key))
        {
            string path = Save(kind, bytes, ex is FindingException ? ex.Message : ex.ToString());
            Console.WriteLine($"FINDING {kind}: {first} (saved {path})");
        }
    }

    private string Save(string kind, byte[] bytes, string detail)
    {
        string hash = Convert.ToHexString(SHA256.HashData(bytes))[..8].ToLowerInvariant();
        string path = Path.Combine(Out, "findings", $"{_target}-{kind}-{hash}.hlsl");
        File.WriteAllBytes(path, bytes);
        File.WriteAllText(path + ".txt", detail);
        return path;
    }

    private void LoadSeeds()
    {
        if (Directory.Exists(_corpus))
        {
            foreach (string h in Directory.GetFiles(_corpus, "*.hlsli", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                FuzzInput.AddSeedHeader(Path.GetFileName(h), File.ReadAllText(h));
            }

            foreach (string p in Directory.GetFiles(_corpus, "*.hlsl", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                _queue.Add(File.ReadAllText(p));
            }
        }

        if (_target == "pp")
        {
            var rng = new Random(_seed);
            for (int i = 0; i < 50; i++)
            {
                _queue.Add(RandomPp(rng));
            }
        }

        if (_queue.Count == 0)
        {
            _queue.Add("[shader(\"compute\")] [numthreads(1,1,1)] void main() {}");
        }
    }

    private static readonly string[] PpPieces =
    [
        "#define ", "#undef ", "#if ", "#ifdef ", "#ifndef ", "#elif ", "#else", "#endif", "#include ", "#line ", "#pragma ", "#error ",
        "#pragma once", "\"f1.h\"", "<f2.h>", "\"main.hlsl\"", "A", "B", "F(x)", "G(x, y)", "H(...)", "A(", "F(", ")", "(", ",", "#", "##", "#x",
        "__VA_ARGS__", "__LINE__", "__FILE__", "__COUNTER__", "defined", "defined(A)", "__has_include", "1", "0", "0x7fffffffffffffff", "1u", "1.5f",
        "+", "-", "*", "/", "%", "<<", ">>", "&&", "||", "?", ":", "!", "~", "==", "<", ">", "...", "[[", "]]", "{", "}", ";", "'a'", "'", "\"", "\"str\"",
        "/*", "*/", "//", "\\\n", "\n", "\n", "\n", " ", " ", "\t", "\u0001", "x", "y", "float4", "return", "\r\n", "\u00e9", "\ud83d\ude00",
    ];

    private static string RandomPp(Random rng)
    {
        var sb = new StringBuilder();
        for (int i = rng.Next(1, 120); i > 0; i--)
        {
            sb.Append(PpPieces[rng.Next(PpPieces.Length)]);
            if (rng.Next(3) == 0)
            {
                sb.Append(' ');
            }
        }

        return sb.ToString();
    }
}
