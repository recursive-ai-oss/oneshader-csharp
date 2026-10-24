using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using OneShader;

/// <summary>Runs the whole front end (preprocess, parse, analyze, lower, reflect) on the bytes. Must never throw.</summary>
internal static class FrontendTarget
{
    public static void Run(ReadOnlySpan<byte> data)
    {
        string text = Encoding.UTF8.GetString(data);
        int cut = text.IndexOf('\u0001', StringComparison.Ordinal);
        if (cut >= 0)
        {
            text = text.Substring(0, cut);
        }

        var result = ShaderCompiler.Compile(text, "fuzz.hlsl", new CompileOptions { IncludeResolver = (_, _, _) => null });
        foreach (var d in result.Diagnostics)
        {
            _ = d.ToString();
        }
    }
}

/// <summary>Mutates the corpus shaders (token-level deletions, duplications, swaps and insertions) and feeds them to the front end.</summary>
internal static class CorpusRun
{
    private static readonly string[] Inserts =
    [
        "(", ")", "{", "}", "[", "]", "[[", "]]", ";", ",", "<", ">", ">>", "::", ":", "=", "+", "-", "*", "/", "?", "!", "0", "1.5", "0xFFFFFFFFu",
        "float4", "float3x3", "Texture2D<float4>", "SamplerState", "cbuffer", "struct", "return", "if", "else", "for", "while", "switch", "case", "discard",
        "register(t0)", "packoffset(c0)", "SV_Position", "SV_Target", "[shader(\"pixel\")]", "[numthreads(8,8,1)]", "static", "const", "x", "Frame", "mul",
    ];

    public static int Run(string dir, int count, int seed)
    {
        var files = Directory.GetFiles(dir, "*.hlsl", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).Select(File.ReadAllText).ToArray();
        var rng = new Random(seed);
        int failures = 0;
        for (int i = 0; i < count; i++)
        {
            string text = Mutate(files[rng.Next(files.Length)], rng);
            var bytes = Encoding.UTF8.GetBytes(text);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                FrontendTarget.Run(bytes);
            }
            catch (Exception ex)
            {
                failures++;
                string path = Path.Combine(Path.GetTempPath(), $"osh-fuzz-fe-{seed}-{i}.hlsl");
                File.WriteAllBytes(path, bytes);
                Console.WriteLine($"input {i}: {ex.GetType().Name}: {ex.Message} (saved {path})");
                if (failures >= 10)
                {
                    break;
                }
            }

            if (watch.ElapsedMilliseconds > 2000)
            {
                failures++;
                string path = Path.Combine(Path.GetTempPath(), $"osh-fuzz-slow-{seed}-{i}.hlsl");
                File.WriteAllBytes(path, bytes);
                Console.WriteLine($"input {i}: took {watch.ElapsedMilliseconds} ms (saved {path})");
            }
        }

        Console.WriteLine($"{count} inputs, {failures} failures");
        return failures == 0 ? 0 : 1;
    }

    private static string Mutate(string text, Random rng)
    {
        var sb = new StringBuilder(text);
        for (int k = rng.Next(1, 6); k > 0 && sb.Length > 2; k--)
        {
            int at = rng.Next(sb.Length);
            switch (rng.Next(6))
            {
                case 0:
                    sb.Remove(at, Math.Min(rng.Next(1, 40), sb.Length - at));
                    break;
                case 1:
                {
                    int len = Math.Min(rng.Next(1, 60), sb.Length - at);
                    sb.Insert(rng.Next(sb.Length), sb.ToString(at, len));
                    break;
                }

                case 2:
                    sb.Insert(at, " " + Inserts[rng.Next(Inserts.Length)] + " ");
                    break;
                case 3:
                {
                    int len = Math.Min(rng.Next(1, 30), sb.Length - at);
                    int to = rng.Next(sb.Length - len);
                    string chunk = sb.ToString(at, len);
                    sb.Remove(at, len);
                    sb.Insert(Math.Min(to, sb.Length), chunk);
                    break;
                }

                case 4:
                    sb.Length = Math.Max(1, at);
                    break;
                default:
                    sb[at] = (char)rng.Next(32, 127);
                    break;
            }
        }

        return sb.ToString();
    }
}
