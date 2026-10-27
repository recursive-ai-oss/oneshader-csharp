using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using OneShader;
using OneShader.Diagnostics;
using OneShader.IR;
using OneShader.Lexing;
using OneShader.Preprocessing;

/// <summary>One fuzz input: the main file's text plus include files, from one byte string (U+0001 starts the next include file, named f1.h, f2.h, ...).</summary>
internal sealed class FuzzInput
{
    private static readonly Dictionary<string, string> SeedHeaders = new(StringComparer.Ordinal);

    public FuzzInput(string text)
    {
        string[] parts = text.Split('\u0001');
        Main = parts[0];
        for (int i = 1; i < parts.Length && i <= 8; i++)
        {
            Files["f" + i.ToString(CultureInfo.InvariantCulture) + ".h"] = parts[i];
        }
    }

    public string Main { get; }

    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>Corpus headers (for example brdf.hlsli) that seeds include by name.</summary>
    public static void AddSeedHeader(string name, string text) => SeedHeaders[name] = text;

    public ResolvedInclude? Resolve(string requested, string includingFile, bool isSystem)
    {
        string name = Path.GetFileName(requested.Replace('\\', '/'));
        if (Files.TryGetValue(requested, out string? text) || Files.TryGetValue(name, out text) || SeedHeaders.TryGetValue(name, out text))
        {
            return new ResolvedInclude(name, text);
        }

        return null;
    }

    public CompileOptions Options(bool optimize, params CompileTarget[] targets) => Options(optimize, DebugInfoLevel.None, targets);

    public CompileOptions Options(bool optimize, DebugInfoLevel debugInfo, params CompileTarget[] targets) => new()
    {
        IncludeResolver = Resolve,
        Targets = targets.Length == 0 ? [CompileTarget.SpirV, CompileTarget.Msl, CompileTarget.Wgsl] : targets,
        Optimize = optimize,
        DebugInfo = debugInfo,
    };
}

/// <summary>A defect the fuzzer found (as opposed to the compiler rejecting the shader with diagnostics, which is fine).</summary>
internal sealed class FindingException(string kind, string message) : Exception(message)
{
    public string Kind { get; } = kind;
}

internal static class Targets
{
    public static readonly string[] Names = ["pp", "frontend", "optimizer", "backends", "validity"];

    public static bool Validate { get; set; }

    public static Action<ReadOnlySpan<byte>> Get(string name) => name switch
    {
        "pp" => Preprocess,
        "frontend" => Frontend,
        "optimizer" => OptimizerTarget,
        "backends" => data => Backends(new FuzzInput(Encoding.UTF8.GetString(data)), false),
        "validity" => data => Backends(new FuzzInput(Encoding.UTF8.GetString(data)), true),
        "all" => data =>
        {
            Preprocess(data);
            Frontend(data);
            OptimizerTarget(data);
            Backends(new FuzzInput(Encoding.UTF8.GetString(data)), true);
        },
        _ => throw new ArgumentException("unknown target " + name + "; use " + string.Join(", ", Names) + " or all"),
    };

    /// <summary>Codes that mean the compiler caught its own exception or fell back from a failed pass: always a bug.</summary>
    private static void CheckNoInternalError(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (Diagnostic d in diagnostics)
        {
            _ = d.ToString();
            if (d.Code is ApiDiagnosticCodes.InternalError or ApiDiagnosticCodes.OptimizerFallback or OneShader.Semantics.SemanticCodes.Internal)
            {
                string kind = d.Code == OneShader.Semantics.SemanticCodes.Internal && !d.Message.StartsWith("Internal compiler error", StringComparison.Ordinal) ? "lowering-gap" : "internal-" + d.Code;
                throw new FindingException(kind, d.ToString());
            }

            if (d.Span.IsKnown && (d.Span.Column < 1 || d.Span.Line < 1))
            {
                throw new FindingException("bad-location", "diagnostic with an invalid position: " + d);
            }
        }
    }

    private static void Preprocess(ReadOnlySpan<byte> data)
    {
        var input = new FuzzInput(Encoding.UTF8.GetString(data));
        var options = new PreprocessorOptions
        {
            IncludeResolver = new InMemoryIncludeResolver(input.Files),
            MaxExpansionSteps = 200_000,
            MaxOutputTokens = 200_000,
            MaxIncludeCount = 256,
        };
        options.Define("FUZZ");
        var bag = new DiagnosticBag();
        PreprocessResult r = Preprocessor.Preprocess(new SourceFile("main.hlsl", input.Main), options, bag);
        string first = r.ToText();
        foreach (Diagnostic d in bag.Items)
        {
            _ = d.ToString();
            if (d.Span.IsKnown && (d.Span.Column < 1 || d.Span.Line < 1))
            {
                throw new FindingException("bad-location", "diagnostic with an invalid position: " + d);
            }
        }

        // Same text and options must give the same output.
        var bag2 = new DiagnosticBag();
        var options2 = new PreprocessorOptions
        {
            IncludeResolver = new InMemoryIncludeResolver(input.Files),
            MaxExpansionSteps = 200_000,
            MaxOutputTokens = 200_000,
            MaxIncludeCount = 256,
        };
        options2.Define("FUZZ");
        string second = Preprocessor.Preprocess(new SourceFile("main.hlsl", input.Main), options2, bag2).ToText();
        if (first != second || bag.Items.Count != bag2.Items.Count)
        {
            throw new FindingException("nondeterministic-preprocessor", "two preprocessor runs differ");
        }
    }

    private static void Frontend(ReadOnlySpan<byte> data)
    {
        var input = new FuzzInput(Encoding.UTF8.GetString(data));
        var result = OneShader.Semantics.Frontend.Run(input.Main, "fuzz.hlsl", input.Options(true));
        CheckNoInternalError(result.Diagnostics.Items);
        if (result.Module is not null && result.Reflection is null)
        {
            throw new FindingException("frontend-no-reflection", "lowering succeeded without reflection");
        }

        if (result.Module is null && !result.Diagnostics.HasErrors)
        {
            throw new FindingException("frontend-silent-failure", "no module and no error diagnostic");
        }
    }

    private static void OptimizerTarget(ReadOnlySpan<byte> data)
    {
        var input = new FuzzInput(Encoding.UTF8.GetString(data));
        var options = input.Options(false);
        var a = OneShader.Semantics.Frontend.Run(input.Main, "fuzz.hlsl", options);
        CheckNoInternalError(a.Diagnostics.Items);
        if (a.Module is not { } module)
        {
            return;
        }

        OneShader.Optimization.Optimizer.VerifyEachPass = true;
        CheckVerified(IrVerifier.Verify(module), "lowering");
        string before = Signature(module);
        string? failure = OneShader.Optimization.Optimizer.Run(module);
        if (failure is not null)
        {
            throw new FindingException("optimizer-failure", failure);
        }

        CheckVerified(IrVerifier.Verify(module), "optimizer");
        string after = Signature(module);
        if (before != after)
        {
            throw new FindingException("optimizer-changed-interface", "entry points differ:\n" + before + "\nvs\n" + after);
        }

        // A second lowering and optimization must print the same IR.
        var b = OneShader.Semantics.Frontend.Run(input.Main, "fuzz.hlsl", options);
        if (b.Module is not { } module2 || OneShader.Optimization.Optimizer.Run(module2) is not null)
        {
            throw new FindingException("nondeterministic-lowering", "second lowering or optimization failed where the first worked");
        }

        if (IrPrinter.Print(module) != IrPrinter.Print(module2))
        {
            throw new FindingException("nondeterministic-optimizer", "two runs print different IR");
        }
    }

    private static void CheckVerified(IReadOnlyList<string> problems, string stage)
    {
        if (problems.Count > 0)
        {
            throw new FindingException("ir-" + stage + "-invalid", "IR verifier after " + stage + ": " + string.Join("; ", problems));
        }
    }

    private static string Signature(IrModule module)
    {
        var sb = new StringBuilder();
        foreach (EntryPoint e in module.EntryPoints)
        {
            sb.Append(e.Stage).Append(' ').Append(e.Function.Name).Append(' ').Append(e.WorkgroupSize).Append(' ')
              .Append(e.Inputs.Count).Append('/').Append(e.Outputs.Count).Append(e.WritesDepth).Append('\n');
        }

        return sb.ToString();
    }

    public static int ValidatedSpirv;
    public static int ValidatedWgsl;
    public static int MslSamples;

    private static void Backends(FuzzInput input, bool validate)
    {
        // The unoptimized and optimized paths are both reachable from user code (Optimize = false).
        bool optimize = (input.Main.Length & 1) == 0;

        // Debug information is a third axis: the emitters must stay valid and deterministic with it on, whatever the input.
        var debugInfo = (DebugInfoLevel)((input.Main.Length >> 1) % 3);
        var options = input.Options(optimize, debugInfo);
        var r1 = ShaderCompiler.Compile(input.Main, "fuzz.hlsl", options);
        CheckNoInternalError(r1.Diagnostics);
        var r2 = ShaderCompiler.Compile(input.Main, "fuzz.hlsl", options);
        if (r1.Success != r2.Success || r1.Diagnostics.Count != r2.Diagnostics.Count || r1.Blobs.Count != r2.Blobs.Count)
        {
            throw new FindingException("nondeterministic-compile", "two compiles differ in outcome");
        }

        for (int i = 0; i < r1.Diagnostics.Count; i++)
        {
            if (r1.Diagnostics[i].ToString() != r2.Diagnostics[i].ToString())
            {
                throw new FindingException("nondeterministic-diagnostics", r1.Diagnostics[i] + "\nvs\n" + r2.Diagnostics[i]);
            }
        }

        for (int i = 0; i < r1.Blobs.Count; i++)
        {
            if (!r1.Blobs[i].Data.SequenceEqual(r2.Blobs[i].Data))
            {
                throw new FindingException("nondeterministic-blob", r1.Blobs[i].Target + " " + r1.Blobs[i].EntryPoint + " differs between two compiles");
            }
        }

        if (!r1.Success && r1.Blobs.Count != 0)
        {
            throw new FindingException("blobs-on-failure", "blobs returned with errors");
        }

        if (validate && Validate && r1.Success)
        {
            Validator.Check(r1, input.Main);
        }
    }
}
