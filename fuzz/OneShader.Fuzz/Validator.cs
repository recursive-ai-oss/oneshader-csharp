using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using OneShader;

/// <summary>Runs spirv-val and naga as black-box processes on accepted output, and keeps a sample of MSL for the Mac.</summary>
internal static class Validator
{
    private static string? _spirvVal;
    private static string? _naga;
    private static int _counter;

    public static string OutDir { get; set; } = ".";

    public static int SampleEvery { get; set; } = 1;

    public static int MaxMslSamples { get; set; } = 300;

    public static long ExternalTicks { get; set; }

    public static void Init(string toolsDir)
    {
        _spirvVal = Path.Combine(toolsDir, "bin", "spirv-val");
        _naga = Path.Combine(toolsDir, "bin", "naga");
        if (!File.Exists(_spirvVal) || !File.Exists(_naga))
        {
            throw new FileNotFoundException("spirv-val and naga are needed in " + toolsDir + "/bin (tools/fetch-oracles.sh)");
        }

        Directory.CreateDirectory(Path.Combine(OutDir, "tmp"));
    }

    public static void Check(CompileResult r, string source)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            bool sample = (++_counter % SampleEvery) == 0;
            foreach (ShaderBlob b in r.Blobs)
            {
                if (b.Target == CompileTarget.Msl)
                {
                    if (Targets.MslSamples < MaxMslSamples)
                    {
                        string name = Convert.ToHexString(SHA256.HashData(b.Data))[..12].ToLowerInvariant();
                        string dir = Path.Combine(OutDir, "msl");
                        Directory.CreateDirectory(dir);
                        string path = Path.Combine(dir, name + ".metal");
                        if (!File.Exists(path))
                        {
                            File.WriteAllBytes(path, b.ToArray());
                            Targets.MslSamples++;
                        }
                    }

                    continue;
                }

                if (!sample)
                {
                    continue;
                }

                string tmp = Path.Combine(OutDir, "tmp", b.Target == CompileTarget.SpirV ? "v.spv" : "v.wgsl");
                File.WriteAllBytes(tmp, b.ToArray());
                if (b.Target == CompileTarget.SpirV)
                {
                    Targets.ValidatedSpirv++;
                    (int code, string output) = Run(_spirvVal!, "--target-env", "vulkan1.2", tmp);
                    if (code != 0)
                    {
                        throw new FindingException("invalid-spirv", "spirv-val rejected " + b.EntryPoint + ":\n" + output);
                    }
                }
                else
                {
                    Targets.ValidatedWgsl++;
                    (int code, string output) = Run(_naga!, tmp);
                    if (code != 0)
                    {
                        throw new FindingException("invalid-wgsl", "naga rejected " + b.EntryPoint + ":\n" + output + "\n" + b.AsText());
                    }
                }
            }
        }
        finally
        {
            ExternalTicks += Stopwatch.GetTimestamp() - start;
        }
    }

    private static (int Code, string Output) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string o = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(30_000))
        {
            p.Kill(true);
            return (-1, "timeout");
        }

        var sb = new StringBuilder(o).Append(err.GetAwaiter().GetResult());
        return (p.ExitCode, sb.Length > 3000 ? sb.ToString(0, 3000) : sb.ToString());
    }
}
