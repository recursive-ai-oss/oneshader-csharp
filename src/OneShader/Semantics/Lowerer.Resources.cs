using System;
using System.Collections.Generic;
using OneShader.Diagnostics;
using OneShader.IR;
using OneShader.Syntax;
using OneShader.Types;

namespace OneShader.Semantics;

// Resource-typed local variables. A resource has no run-time value to store, so a local is only a name for one resource
// that the compiler must be able to determine statically: each assignment rebinds the name, and where control flow joins
// the branches must agree.
internal sealed partial class Lowerer
{
    private readonly List<VariableSymbol> _resLocals = [];
    private readonly Dictionary<VariableSymbol, SourceSpan> _resSpan = [];

    private sealed class ResSnap
    {
        public required IrValue?[] Values { get; init; }

        public required SourceSpan[] Spans { get; init; }
    }

    private ResSnap? SnapRes()
    {
        if (_resLocals.Count == 0)
        {
            return null;
        }

        var snap = new ResSnap { Values = new IrValue?[_resLocals.Count], Spans = new SourceSpan[_resLocals.Count] };
        for (int j = 0; j < _resLocals.Count; j++)
        {
            snap.Values[j] = _resLocals[j].Ir;
            snap.Spans[j] = _resSpan.TryGetValue(_resLocals[j], out var sp) ? sp : default;
        }

        return snap;
    }

    private void RestoreRes(ResSnap? snap)
    {
        if (snap is null)
        {
            return;
        }

        for (int j = 0; j < snap.Values.Length; j++)
        {
            _resLocals[j].Ir = snap.Values[j];
            _resSpan[_resLocals[j]] = snap.Spans[j];
        }
    }

    private void BindResourceLocal(VariableSymbol v, Expression value, SourceSpan span)
    {
        var ptr = ResourcePointer(value);
        if (ptr is null)
        {
            _diag.Error(SemanticCodes.BadResource, "The resource variable '" + v.Name + "' is given something that is not a named resource, so the compiler cannot tell which resource it stands for. Assign a global resource, a resource parameter or an element of a resource array.", span);
            return;
        }

        v.Ir = ptr;
        _resSpan[v] = span;
    }

    private static string ResName(IrValue? v) => v switch
    {
        GlobalVariable g => g.Name ?? "a resource",
        Argument a => a.Name ?? "a parameter",
        Instruction { Op: Opcode.AccessChain } i when i.Operands.Count > 0 => ResName(i.Operands[0]) + "[...]",
        _ => "a resource",
    };

    private static bool SameResource(IrValue? a, IrValue? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        switch (a, b)
        {
            case (Constant ca, Constant cb):
                return ca.Bits == cb.Bits && ca.Type.Equals(cb.Type);
            case (Instruction { Op: Opcode.AccessChain } ia, Instruction { Op: Opcode.AccessChain } ib):
            {
                if (ia.Operands.Count != ib.Operands.Count)
                {
                    return false;
                }

                for (int k = 0; k < ia.Operands.Count; k++)
                {
                    if (!SameResource(ia.Operands[k], ib.Operands[k]))
                    {
                        return false;
                    }
                }

                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>Joins the states at the end of the branches of one statement; <paramref name="n"/> is the number of resource locals declared before it.</summary>
    private void MergeRes(IReadOnlyList<ResSnap?> ends, int n)
    {
        if (n == 0)
        {
            return;
        }

        var live = new List<ResSnap>();
        foreach (var e in ends)
        {
            if (e is not null)
            {
                live.Add(e);
            }
        }

        if (live.Count == 0)
        {
            return;
        }

        for (int j = 0; j < n; j++)
        {
            IrValue? chosen = null;
            var chosenSpan = default(SourceSpan);
            foreach (var e in live)
            {
                var v = e.Values[j];
                if (v is null)
                {
                    continue;
                }

                if (chosen is null)
                {
                    chosen = v;
                    chosenSpan = e.Spans[j];
                }
                else if (!SameResource(chosen, v))
                {
                    var at = e.Spans[j].IsKnown ? e.Spans[j] : chosenSpan;
                    _diag.Error(SemanticCodes.BadResource, "The resource variable '" + _resLocals[j].Name + "' refers to '" + ResName(chosen) + "' on one path and to '" + ResName(v) + "' on another, so its uses after the paths join cannot be resolved at compile time. Assign the same resource on every path, or pass the resource to a function that takes it as a parameter.", at);
                    break;
                }
            }

            _resLocals[j].Ir = chosen;
            _resSpan[_resLocals[j]] = chosenSpan;
        }
    }

    /// <summary>A loop body must leave every resource local as it found it.</summary>
    private void CheckLoopRes(ResSnap? before, int n)
    {
        if (n == 0 || before is null)
        {
            return;
        }

        var after = SnapRes()!;
        for (int j = 0; j < n; j++)
        {
            if (after.Values[j] is { } now && before.Values[j] is { } was && !SameResource(now, was))
            {
                _diag.Error(SemanticCodes.BadResource, "The resource variable '" + _resLocals[j].Name + "' is changed inside a loop from '" + ResName(was) + "' to '" + ResName(now) + "', so it would name a different resource on each pass. Assign it before the loop.", after.Spans[j].IsKnown ? after.Spans[j] : default);
            }
        }

        RestoreRes(before);
    }
}
