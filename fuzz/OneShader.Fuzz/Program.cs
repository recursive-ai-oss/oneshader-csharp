using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OneShader;
using OneShader.Diagnostics;
using OneShader.Lexing;
using OneShader.Preprocessing;

// Fuzz targets for the compiler front end.
//
//   OneShader.Fuzz                    libFuzzer/SharpFuzz loop on stdin (instrument OneShader.dll first)
//   OneShader.Fuzz --file <path>      run one input, for reproducing a crash
//   OneShader.Fuzz --random N [seed]  run N generated inputs in-process; exits 1 on any exception
//   OneShader.Fuzz --corpus <dir> N [seed]  run N mutated corpus shaders through the whole front end
//
// An input is the main file's text; a U+0001 character starts the next include file (named
// f1.h, f2.h, ...), so one byte string covers #include behavior too.
if (args.Length >= 2 && args[0] == "--file")
{
    byte[] input = File.ReadAllBytes(args[1]);
    PreprocessorTarget.Run(input);
    FrontendTarget.Run(input);
    Console.WriteLine("ok");
    return 0;
}

if (args.Length >= 3 && args[0] == "--corpus")
{
    // --corpus <dir> <count> [seed]: mutated corpus shaders through the whole front end.
    int n = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
    int sd = args.Length > 3 ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 1;
    return CorpusRun.Run(args[1], n, sd);
}

if (args.Length >= 2 && args[0] == "--random")
{
    int count = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
    int seed = args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 1;
    return RandomRun.Run(count, seed);
}

SharpFuzz.Fuzzer.LibFuzzer.Run(span =>
{
    PreprocessorTarget.Run(span);
    FrontendTarget.Run(span);
});
return 0;

internal static class PreprocessorTarget
{
    /// <summary>Preprocesses the bytes as a shader. Must never throw: any exception is a compiler bug.</summary>
    public static void Run(ReadOnlySpan<byte> data)
    {
        string all = Encoding.UTF8.GetString(data);
        string[] parts = all.Split('\u0001');
        var files = new Dictionary<string, string>();
        for (int i = 1; i < parts.Length && i <= 8; i++)
        {
            files[$"f{i}.h"] = parts[i];
        }

        var options = new PreprocessorOptions
        {
            IncludeResolver = new InMemoryIncludeResolver(files),
            MaxExpansionSteps = 200_000,
            MaxOutputTokens = 200_000,
            MaxIncludeCount = 256,
        };
        options.Define("FUZZ");
        var bag = new DiagnosticBag();
        PreprocessResult r = Preprocessor.Preprocess(new SourceFile("main.hlsl", parts[0]), options, bag);
        _ = r.ToText();
        foreach (Diagnostic d in bag.Items)
        {
            _ = d.ToString();
        }
    }
}

internal static class RandomRun
{
    private static readonly string[] Pieces =
    [
        "#define ", "#undef ", "#if ", "#ifdef ", "#ifndef ", "#elif ", "#else", "#endif", "#include ", "#line ", "#pragma ", "#error ",
        "#pragma once", "\"f1.h\"", "<f2.h>", "\"main.hlsl\"", "A", "B", "F(x)", "G(x, y)", "H(...)", "A(", "F(", ")", "(", ",", "#", "##", "#x",
        "__VA_ARGS__", "__LINE__", "__FILE__", "__COUNTER__", "defined", "defined(A)", "__has_include", "1", "0", "0x7fffffffffffffff", "1u", "1.5f",
        "+", "-", "*", "/", "%", "<<", ">>", "&&", "||", "?", ":", "!", "~", "==", "<", ">", "...", "[[", "]]", "{", "}", ";", "'a'", "'", "\"", "\"str\"",
        "/*", "*/", "//", "\\\n", "\n", "\n", "\n", " ", " ", "\t", "\u0001", "x", "y", "float4", "return", "\r\n", "\u00e9", "\ud83d\ude00",
    ];

    public static int Run(int count, int seed)
    {
        var rng = new Random(seed);
        int failures = 0;
        for (int i = 0; i < count; i++)
        {
            string input = Generate(rng);
            byte[] bytes = Encoding.UTF8.GetBytes(input);
            if (rng.Next(8) == 0 && bytes.Length > 0)
            {
                for (int k = rng.Next(1, 4); k > 0; k--)
                {
                    bytes[rng.Next(bytes.Length)] = (byte)rng.Next(256);
                }
            }

            try
            {
                PreprocessorTarget.Run(bytes);
            }
            catch (Exception ex)
            {
                failures++;
                string path = Path.Combine(Path.GetTempPath(), $"osh-fuzz-crash-{seed}-{i}.bin");
                File.WriteAllBytes(path, bytes);
                Console.WriteLine($"input {i}: {ex.GetType().Name}: {ex.Message} (saved {path})");
                if (failures >= 10)
                {
                    break;
                }
            }
        }

        Console.WriteLine($"{count} inputs, {failures} failures");
        return failures == 0 ? 0 : 1;
    }

    private static string Generate(Random rng)
    {
        var sb = new StringBuilder();
        int n = rng.Next(1, 120);
        for (int i = 0; i < n; i++)
        {
            sb.Append(Pieces[rng.Next(Pieces.Length)]);
            if (rng.Next(3) == 0)
            {
                sb.Append(' ');
            }
        }

        return sb.ToString();
    }
}
