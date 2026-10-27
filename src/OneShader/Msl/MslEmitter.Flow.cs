using System;
using System.Collections.Generic;
using OneShader.IR;
using OneShader.Types;

namespace OneShader.Msl;

internal sealed partial class MslEmitter
{
    private const int MaxFlowDepth = 200;

    private enum Flow
    {
        /// <summary>Control reached the end of the region (a stop block).</summary>
        Fell,

        /// <summary>Control left through return, break, continue, discard or cannot arrive.</summary>
        Left,
    }

    private enum JumpKind
    {
        Next,
        Fell,
        Left,
    }

    private readonly record struct Next(bool Done, Flow Flow, BasicBlock? Block);

    private sealed class Construct
    {
        public bool IsLoop { get; init; }

        public BasicBlock? Merge { get; init; }

        public BasicBlock? Continue { get; init; }

        public BasicBlock? Header { get; init; }
    }

    private readonly List<Construct> _constructs = [];
    private int _blockBudget;
    private int _flowDepth;
    private BasicBlock? _lastStop;

    private Construct? InnermostBreakable() => _constructs.Count > 0 ? _constructs[^1] : null;

    private Construct? InnermostLoop()
    {
        for (int i = _constructs.Count - 1; i >= 0; i--)
        {
            if (_constructs[i].IsLoop)
            {
                return _constructs[i];
            }
        }

        return null;
    }

    private Flow EmitChain(BasicBlock start, List<BasicBlock> stops)
    {
        if (_flowDepth >= MaxFlowDepth)
        {
            Unsupported("Control flow is nested more than " + Inv(MaxFlowDepth) + " levels deep; simplify the shader.", _fn.Span);
            return Flow.Left;
        }

        _flowDepth++;
        try
        {
            return ChainCore(start, stops);
        }
        finally
        {
            _flowDepth--;
        }
    }

    private Flow Go(BasicBlock from, BasicBlock to, List<BasicBlock> stops)
    {
        switch (Jump(from, to, stops))
        {
            case JumpKind.Next:
                return EmitChain(to, stops);
            case JumpKind.Fell:
                return Flow.Fell;
            default:
                return Flow.Left;
        }
    }

    private Flow ChainCore(BasicBlock start, List<BasicBlock> stops)
    {
        var b = start;
        for (; ; )
        {
            if (++_blockBudget > MaxBlockEmissions)
            {
                Unsupported("The function '" + _fn.Name + "' is too large or too tangled to emit as structured MSL.", _fn.Span);
                return Flow.Left;
            }

            if (b.Structure == StructureKind.Loop && b.Terminator is { Op: Opcode.Branch })
            {
                EmitLoop(b);
                var after = b.MergeBlock;
                if (after is null || !_reachable.Contains(after))
                {
                    return Flow.Left;
                }

                if (stops.Contains(after))
                {
                    _lastStop = after;
                    return Flow.Fell;
                }

                b = after;
                continue;
            }

            foreach (var inst in b.Instructions)
            {
                if (inst.IsTerminator)
                {
                    break;
                }

                EmitInstruction(inst);
            }

            var t = b.Terminator;
            if (t is null)
            {
                Malformed("A block has no terminator.", _fn.Span);
                return Flow.Left;
            }

            Mark(t.Span);
            Next step;
            switch (t.Op)
            {
                case Opcode.Branch:
                {
                    var target = t.Targets[0];
                    switch (Jump(b, target, stops))
                    {
                        case JumpKind.Next:
                            b = target;
                            continue;
                        case JumpKind.Fell:
                            return Flow.Fell;
                        default:
                            return Flow.Left;
                    }
                }

                case Opcode.CondBranch:
                    step = b.Structure == StructureKind.Selection ? EmitSelection(b, t, b.MergeBlock, stops) : EmitConditional(b, t, stops);
                    break;
                case Opcode.Switch:
                    step = EmitSwitch(b, t, stops);
                    break;
                case Opcode.Return:
                    EmitReturn(t);
                    return Flow.Left;
                case Opcode.Kill:
                    // discard_fragment() is legal in any function called from a fragment function; it marks the fragment as
                    // discarded and later writes are suppressed, so a helper simply returns and the caller carries on.
                    W("discard_fragment();");
                    if (_isEntry)
                    {
                        EmitEntryReturn();
                    }
                    else
                    {
                        EmitReturn(null);
                    }

                    return Flow.Left;
                default:
                    if (_isEntry)
                    {
                        EmitEntryReturn();
                    }
                    else
                    {
                        EmitReturn(null);
                    }

                    return Flow.Left;
            }

            if (step.Done)
            {
                return step.Flow;
            }

            b = step.Block!;
        }
    }

    private void EmitReturn(Instruction? t)
    {
        if (_isEntry)
        {
            EmitEntryReturn();
        }
        else if (t is not null && t.Operands.Count == 1)
        {
            W("return " + V(t.Operands[0]) + ";");
        }
        else if (_fn.ReturnType.IsVoid)
        {
            W("return;");
        }
        else
        {
            W("return " + T(_fn.ReturnType) + "();");
        }
    }

    // ---- jumps and phi copies -------------------------------------------------------------------------

    /// <summary>
    /// Handles the edge <paramref name="from"/> to <paramref name="to"/>: copies phi values, then either ends the region (a stop block),
    /// writes break or continue, or says emission goes on at <paramref name="to"/>.
    /// </summary>
    private JumpKind Jump(BasicBlock from, BasicBlock to, List<BasicBlock> stops)
    {
        EdgeCopies(from, to);
        if (stops.Contains(to))
        {
            _lastStop = to;
            return JumpKind.Fell;
        }

        var brk = InnermostBreakable();
        var loop = InnermostLoop();
        if (brk is not null && to == brk.Merge)
        {
            W("break;");
            return JumpKind.Left;
        }

        if (loop is not null && to == loop.Continue)
        {
            EmitContinue(loop);
            return JumpKind.Left;
        }

        foreach (var c in _constructs)
        {
            if (c != brk && c.Merge == to || c.IsLoop && c != loop && c.Continue == to)
            {
                Unsupported("A break or continue leaves several nested loops or switches at once, which MSL cannot express without labels; restructure the shader.", _fn.Span);
                return JumpKind.Left;
            }
        }

        return JumpKind.Next;
    }

    private void EmitContinue(Construct loop)
    {
        // The continue block (usually the loop increment) is written out again here rather than shared, because C++ has no
        // way to run it before jumping back; its values are either local to it or declared at function scope.
        if (loop.Continue is { } cont && loop.Header is { } header)
        {
            var flow = EmitChain(cont, [header]);
            if (flow == Flow.Left)
            {
                return;
            }
        }

        W("continue;");
    }

    private void EdgeCopies(BasicBlock from, BasicBlock to)
    {
        List<(Instruction Phi, string Value, bool OperandIsPhi)>? pairs = null;
        foreach (var inst in to.Instructions)
        {
            if (inst.Op != Opcode.Phi)
            {
                break;
            }

            int k = inst.Targets.IndexOf(from);
            if (k < 0 || k >= inst.Operands.Count)
            {
                continue;
            }

            var operand = inst.Operands[k];
            string value = V(operand);
            if (value == NameOf(inst))
            {
                continue;
            }

            pairs ??= [];
            pairs.Add((inst, value, operand is Instruction { Op: Opcode.Phi } p && p.Parent == to));
        }

        if (pairs is null)
        {
            return;
        }

        bool useTemps = false;
        if (pairs.Count > 1)
        {
            foreach (var p in pairs)
            {
                if (p.OperandIsPhi)
                {
                    useTemps = true;
                    break;
                }
            }
        }

        if (!useTemps)
        {
            foreach (var p in pairs)
            {
                W(NameOf(p.Phi) + " = " + p.Value + ";");
            }

            return;
        }

        var temps = new string[pairs.Count];
        for (int i = 0; i < pairs.Count; i++)
        {
            temps[i] = Tmp("p");
            W(T(pairs[i].Phi.Type) + " " + temps[i] + " = " + pairs[i].Value + ";");
        }

        for (int i = 0; i < pairs.Count; i++)
        {
            W(NameOf(pairs[i].Phi) + " = " + temps[i] + ";");
        }
    }

    // ---- loops -----------------------------------------------------------------------------------------

    private void EmitLoop(BasicBlock h)
    {
        var term = h.Terminator!;
        var body = term.Targets[0];
        if (h.ContinueBlock is null || h.MergeBlock is null)
        {
            Malformed("A loop header has no merge or continue block.", _fn.Span);
            return;
        }

        var cont = h.ContinueBlock;
        W("for (;;)");
        W("{");
        _indent++;
        _constructs.Add(new Construct { IsLoop = true, Merge = h.MergeBlock, Continue = cont, Header = h });
        foreach (var inst in h.Instructions)
        {
            if (inst.IsTerminator)
            {
                break;
            }

            EmitInstruction(inst);
        }

        var flow = Go(h, body, [cont]);
        if (flow == Flow.Fell && _lastStop == cont && _reachable.Contains(cont))
        {
            // Fell into the continue block: run it, then the loop goes round again.
            EmitChain(cont, [h]);
        }

        _constructs.RemoveAt(_constructs.Count - 1);
        _indent--;
        W("}");
    }

    // ---- conditionals -------------------------------------------------------------------------------------

    private Next Continue(BasicBlock? merge, Flow a, Flow b, List<BasicBlock> stops)
    {
        if (merge is null || (a == Flow.Left && b == Flow.Left) || !_reachable.Contains(merge))
        {
            return new Next(true, Flow.Left, null);
        }

        if (stops.Contains(merge))
        {
            _lastStop = merge;
            return new Next(true, Flow.Fell, null);
        }

        return new Next(false, Flow.Fell, merge);
    }

    private void RenderIf(string cond, string then, string otherwise)
    {
        if (then.Length == 0 && otherwise.Length == 0)
        {
            return;
        }

        if (then.Length == 0)
        {
            W("if (!(" + cond + "))");
            W("{");
            Raw(otherwise);
            W("}");
            return;
        }

        W("if (" + cond + ")");
        W("{");
        Raw(then);
        if (otherwise.Length > 0)
        {
            W("}");
            W("else");
            W("{");
            Raw(otherwise);
        }

        W("}");
    }

    private Next EmitSelection(BasicBlock b, Instruction t, BasicBlock? merge, List<BasicBlock> stops)
    {
        string cond = V(t.Operands[0]);
        var armStops = new List<BasicBlock>(stops);
        if (merge is not null)
        {
            armStops.Add(merge);
        }

        var f0 = Flow.Left;
        var f1 = Flow.Left;
        string then = Capture(() => f0 = Go(b, t.Targets[0], armStops));
        string otherwise = Capture(() => f1 = Go(b, t.Targets[1], armStops));
        RenderIf(cond, then, otherwise);
        return Continue(merge, f0, f1, stops);
    }

    private bool IsJumpOut(BasicBlock t, List<BasicBlock> stops)
    {
        if (stops.Contains(t))
        {
            return false;
        }

        return InnermostBreakable() is { } brk && t == brk.Merge || InnermostLoop() is { } loop && t == loop.Continue;
    }

    /// <summary>A conditional branch with no merge marker: a loop exit test, a short-circuit, or an if whose join is found from the CFG.</summary>
    private Next EmitConditional(BasicBlock b, Instruction t, List<BasicBlock> stops)
    {
        string cond = V(t.Operands[0]);
        var t0 = t.Targets[0];
        var t1 = t.Targets[1];
        bool j0 = IsJumpOut(t0, stops);
        bool j1 = IsJumpOut(t1, stops);
        if (j0 || j1)
        {
            if (j0 && j1)
            {
                string a0 = Capture(() => Jump(b, t0, stops));
                string a1 = Capture(() => Jump(b, t1, stops));
                RenderIf(cond, a0, a1);
                return new Next(true, Flow.Left, null);
            }

            var jumpTo = j0 ? t0 : t1;
            var other = j0 ? t1 : t0;
            string arm = Capture(() => Jump(b, jumpTo, stops));
            W("if (" + (j0 ? cond : "!(" + cond + ")") + ")");
            W("{");
            Raw(arm);
            W("}");
            switch (Jump(b, other, stops))
            {
                case JumpKind.Next:
                    return new Next(false, Flow.Fell, other);
                case JumpKind.Fell:
                    return new Next(true, Flow.Fell, null);
                default:
                    return new Next(true, Flow.Left, null);
            }
        }

        return EmitSelection(b, t, ImmediatePostDominator(b), stops);
    }

    // ---- switch ----------------------------------------------------------------------------------------------

    private Next EmitSwitch(BasicBlock b, Instruction t, List<BasicBlock> stops)
    {
        var merge = b.MergeBlock;
        if (merge is null)
        {
            Malformed("A switch has no merge block.", t.Span);
            return new Next(true, Flow.Left, null);
        }

        var selector = t.Operands[0];
        bool unsigned = selector.Type.ScalarElement is { IsSignedInteger: false };
        var targets = new List<BasicBlock>();
        var labels = new Dictionary<BasicBlock, List<string>>();
        void Add(BasicBlock target, string label)
        {
            if (!labels.TryGetValue(target, out var l))
            {
                l = [];
                labels[target] = l;
                targets.Add(target);
            }

            l.Add(label);
        }

        Add(t.Targets[0], "default");
        for (int k = 0; k < t.Immediates.Count && k + 1 < t.Targets.Count; k++)
        {
            int v = t.Immediates[k];
            Add(t.Targets[k + 1], "case " + (unsigned ? Inv((uint)v) + "u" : v < 0 ? "-" + Inv(-(long)v) : Inv(v)));
        }

        // Source order decides which case falls through into which.
        var fnBlocks = _fn.Blocks;
        targets.Sort((x, y) => fnBlocks.IndexOf(x).CompareTo(fnBlocks.IndexOf(y)));

        // A case that other cases fall into must not repeat the switch edge's phi copies in its body: a body entered by falling through
        // already holds the previous case's values. The switch edge's values are known before the switch, so they are copied there.
        var copiedBefore = new HashSet<BasicBlock>();
        foreach (var target in targets)
        {
            if (target != merge && HasOtherPredecessorEdge(target, b))
            {
                EdgeCopies(b, target);
                copiedBefore.Add(target);
            }
        }

        var enclosingContinue = InnermostLoop()?.Continue;
        W("switch (" + V(selector) + ")");
        W("{");
        _constructs.Add(new Construct { Merge = merge });
        for (int i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            foreach (var label in labels[target])
            {
                W(label + ":");
            }

            if (target == merge)
            {
                EdgeCopies(b, merge);
                _indent++;
                W("break;");
                _indent--;
                continue;
            }

            BasicBlock? following = i + 1 < targets.Count && targets[i + 1] != merge ? targets[i + 1] : null;

            // The switch's own merge and the enclosing loop's continue block are not stops here: reaching one, even from inside a nested
            // if, must write `break;` or `continue;` because the case body is the only code that runs between the jump and its target (see Jump).
            var caseStops = new List<BasicBlock>(stops.Count + 1);
            foreach (var s in stops)
            {
                if (s != merge && s != enclosingContinue)
                {
                    caseStops.Add(s);
                }
            }

            if (following is not null)
            {
                caseStops.Add(following);
            }

            var flow = Flow.Left;
            _lastStop = null;
            string body = Capture(() => flow = copiedBefore.Contains(target) ? EmitChain(target, caseStops) : Go(b, target, caseStops));
            _indent++;
            W("{");
            Raw(body);
            if (flow == Flow.Fell && (_lastStop is null || _lastStop != following))
            {
                Unsupported("A switch case falls into a block that is not the next case.", t.Span);
            }

            W("}");
            _indent--;
        }

        _constructs.RemoveAt(_constructs.Count - 1);
        W("}");
        if (!_reachable.Contains(merge))
        {
            return new Next(true, Flow.Left, null);
        }

        if (stops.Contains(merge))
        {
            _lastStop = merge;
            return new Next(true, Flow.Fell, null);
        }

        return new Next(false, Flow.Fell, merge);
    }

    /// <summary>True when a phi at the start of <paramref name="target"/> takes a value from a block other than <paramref name="from"/>, that is, another case falls into it.</summary>
    private static bool HasOtherPredecessorEdge(BasicBlock target, BasicBlock from)
    {
        foreach (var inst in target.Instructions)
        {
            if (inst.Op != Opcode.Phi)
            {
                break;
            }

            foreach (var p in inst.Targets)
            {
                if (p != from)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // ---- post-dominators -------------------------------------------------------------------------------------

    private Dictionary<BasicBlock, BasicBlock?>? _pdom;

    private BasicBlock? ImmediatePostDominator(BasicBlock b)
    {
        _pdom ??= ComputePostDominators(_fn);
        return _pdom.TryGetValue(b, out var p) ? p : null;
    }

    /// <summary>Immediate post-dominators of the reachable blocks (null when a block's paths never rejoin). Cooper, Harvey and Kennedy's iterative algorithm on the reversed graph.</summary>
    private Dictionary<BasicBlock, BasicBlock?> ComputePostDominators(IrFunction f)
    {
        var nodes = new List<BasicBlock>();
        var index = new Dictionary<BasicBlock, int>();
        foreach (var b in f.Blocks)
        {
            if (_reachable.Contains(b))
            {
                index[b] = nodes.Count;
                nodes.Add(b);
            }
        }

        int n = nodes.Count;
        int exit = n;
        var succ = new List<int>[n + 1];
        var pred = new List<int>[n + 1];
        for (int i = 0; i <= n; i++)
        {
            succ[i] = [];
            pred[i] = [];
        }

        for (int i = 0; i < n; i++)
        {
            var s = nodes[i].Successors;
            if (s.Count == 0)
            {
                succ[i].Add(exit);
                pred[exit].Add(i);
            }

            foreach (var x in s)
            {
                if (index.TryGetValue(x, out int j))
                {
                    succ[i].Add(j);
                    pred[j].Add(i);
                }
            }
        }

        // Post-order of the reversed graph from the exit.
        var post = new List<int>();
        var po = new int[n + 1];
        Array.Fill(po, -1);
        var visited = new bool[n + 1];
        var stack = new Stack<(int Node, int Next)>();
        stack.Push((exit, 0));
        visited[exit] = true;
        while (stack.Count > 0)
        {
            var (node, k) = stack.Pop();
            if (k < pred[node].Count)
            {
                stack.Push((node, k + 1));
                int p = pred[node][k];
                if (!visited[p])
                {
                    visited[p] = true;
                    stack.Push((p, 0));
                }
            }
            else
            {
                po[node] = post.Count;
                post.Add(node);
            }
        }

        var idom = new int[n + 1];
        Array.Fill(idom, -1);
        idom[exit] = exit;
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int r = post.Count - 2; r >= 0; r--)
            {
                int node = post[r];
                int best = -1;
                foreach (int p in succ[node])
                {
                    if (idom[p] == -1)
                    {
                        continue;
                    }

                    best = best == -1 ? p : Intersect(p, best, idom, po);
                }

                if (best != -1 && idom[node] != best)
                {
                    idom[node] = best;
                    changed = true;
                }
            }
        }

        var result = new Dictionary<BasicBlock, BasicBlock?>();
        for (int i = 0; i < n; i++)
        {
            int d = idom[i];
            result[nodes[i]] = d >= 0 && d < n ? nodes[d] : null;
        }

        return result;
    }

    private static int Intersect(int a, int b, int[] idom, int[] po)
    {
        while (a != b)
        {
            while (po[a] < po[b])
            {
                a = idom[a];
            }

            while (po[b] < po[a])
            {
                b = idom[b];
            }
        }

        return a;
    }
}
