using System;
using System.Collections.Generic;
using System.Threading;
using OneShader.IR;

namespace OneShader.Optimization;

/// <summary>
/// The optimizer pipeline: inline everything, then per function promote locals to SSA, fold, share
/// common subexpressions, drop dead code and simplify the block graph, until nothing changes.
/// It never throws: a bug in a pass is reported as a message and the caller keeps the unoptimized module.
/// </summary>
internal static class Optimizer
{
    private const int MaxRounds = 8;

    /// <summary>
    /// When true, the IR verifier runs after every pass and a failure names the pass. On in debug builds and
    /// in tests; the final whole-module verification always runs.
    /// </summary>
#if DEBUG
    internal static bool VerifyEachPass { get; set; } = true;
#else
    internal static bool VerifyEachPass { get; set; }
#endif

    /// <summary>Optimizes the module in place. Returns null on success, otherwise a description of the internal failure (the module may be half-rewritten and must be discarded).</summary>
    public static string? Run(IrModule module, CancellationToken cancellationToken = default)
    {
        string current = "setup";
        try
        {
            current = "inline";
            Inliner.Run(module);
            Inliner.RemoveUnreachable(module);
            Verify(module, current);
            foreach (var f in module.Functions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int round = 0; round < MaxRounds; round++)
                {
                    bool changed = false;
                    foreach (var (name, pass) in Passes)
                    {
                        current = name + " in @" + f.Name;
                        changed |= pass(f);
                        if (VerifyEachPass)
                        {
                            Verify(f, current);
                        }
                    }

                    if (!changed)
                    {
                        break;
                    }
                }
            }

            current = "unused globals";
            if (DeadGlobals.Run(module))
            {
                foreach (var f in module.Functions)
                {
                    Dce.Run(f);
                }
            }

            current = "final verification";
            Verify(module, current);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return "optimizer failed in " + current + ": " + ex.Message;
        }
    }

    private static readonly (string Name, Func<IrFunction, bool> Run)[] Passes =
    [
        ("sroa", Sroa.Run),
        ("mem2reg", Mem2Reg.Run),
        ("combine", InstCombine.Run),
        ("cse", Cse.Run),
        ("dce", Dce.Run),
        ("simplify-cfg", SimplifyCfg.Run),
    ];

    private static void Verify(IrModule module, string after)
    {
        var errors = IrVerifier.Verify(module);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("IR is invalid after " + after + ": " + errors[0] + (errors.Count > 1 ? " (+" + (errors.Count - 1) + " more)" : ""));
        }
    }

    private static void Verify(IrFunction f, string after)
    {
        var errors = IrVerifier.Verify(f);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("IR is invalid after " + after + ": " + errors[0] + (errors.Count > 1 ? " (+" + (errors.Count - 1) + " more)" : ""));
        }
    }
}
