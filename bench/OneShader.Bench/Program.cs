using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using BenchmarkDotNet.Running;
using OneShader.Bench;

// OneShader.Bench inproc [BenchmarkDotNet arguments]   in-process compile benchmarks (BenchmarkDotNet); set ONESHADER_BENCH_OUT for side files
// OneShader.Bench process --dxc P --jit P --aot P [--warm N] [--out file.json] [--limit N]
//                                                      whole-process timing against DXC
// OneShader.Bench params                              writes the in-process parameter lists to ONESHADER_BENCH_OUT
// OneShader.Bench list                                 the (file, entry point) pairs that are measured
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: OneShader.Bench inproc|process|list ...");
    return 2;
}

switch (args[0])
{
    case "inproc":
        {
            using var sampler = new LoadSampler(5);
            double start = ReadLoad();
            BenchmarkSwitcher.FromTypes([typeof(SpirvOptimized), typeof(SpirvUnoptimized), typeof(MslOptimized), typeof(WgslOptimized)])
                .Run(args.Skip(1).ToArray());
            sampler.Sample();
            string? dir = Environment.GetEnvironmentVariable("ONESHADER_BENCH_OUT");
            if (dir is not null)
            {
                var doc = new { host = ProcessTiming.HostInfo(""), loadStart = start, loadDuring = sampler.Loads };
                File.WriteAllText(Path.Combine(dir, "load.json"), JsonSerializer.Serialize(doc));
            }

            return 0;
        }
    case "process":
        return ProcessTiming.Run(args.Skip(1).ToArray());
    case "params":
        CompileBenchmark.WriteParams();
        return 0;
    case "list":
        foreach (var u in Manifest.Load(Manifest.FindCorpus()))
        {
            Console.WriteLine(u.Id);
        }

        return 0;
    default:
        Console.Error.WriteLine("unknown mode " + args[0]);
        return 2;
}

static double ReadLoad()
{
    try
    {
        return double.Parse(File.ReadAllText("/proc/loadavg").Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
    }
    catch (IOException)
    {
        return double.NaN;
    }
}
