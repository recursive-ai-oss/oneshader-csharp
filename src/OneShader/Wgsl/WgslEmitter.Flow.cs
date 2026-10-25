using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using OneShader.Diagnostics;
using OneShader.IR;
using OneShader.Types;

namespace OneShader.Wgsl;

internal sealed partial class WgslEmitter
{
    private sealed class Scope(Scope? parent)
    {
        public Scope? Parent { get; } = parent;

        public static bool Encloses(Scope outer, Scope inner)
        {
            for (var s = inner; s is not null; s = s.Parent)
            {
                if (ReferenceEquals(s, outer))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>A value that was declared with <c>let</c> or <c>var</c>; moved to the top of the function when a use falls outside its scope.</summary>
    private sealed class DefInfo(string name, string typeText, Scope scope, bool isVar)
    {
        public string Name { get; } = name;

        public string TypeText { get; } = typeText;

        public Scope Scope { get; } = scope;

        public bool IsVar { get; } = isVar;

        public bool Hoisted { get; set; }
    }

    private sealed class Line(string text, int indent, DefInfo? def = null, string? expr = null)
    {
        public string Text { get; } = text;

        public int Indent { get; } = indent;

        public DefInfo? Def { get; } = def;

        public string? Expr { get; } = expr;

        public string Render()
        {
            string body = Text;
            if (Def is { } d)
            {
                body = d.Hoisted ? d.Name + " = " + Expr + ";"
                    : d.IsVar ? "var " + d.Name + ": " + d.TypeText + " = " + Expr + ";"
                    : "let " + d.Name + " = " + Expr + ";";
            }

            return new string(' ', Indent * 2) + body;
        }
    }

    private enum CtxKind
    {
        Loop,
        Switch,
        If,
    }

    private sealed class Ctx(CtxKind kind, BasicBlock? merge)
    {
        public CtxKind Kind { get; } = kind;

        public BasicBlock? Merge { get; } = merge;

        public BasicBlock? Continue { get; init; }

        public string? FlagName { get; set; }

        public Ctx? PendingLoop { get; set; }
    }

    // ---- per-function state ----------------------------------------------------------------------

    private IrFunction _fn = null!;
    private bool _isEntryFunction;
    private List<Line> _lines = [];
    private List<string> _prologue = [];
    private int _indent;
    private Scope _scope = new(null);
    private Scope _rootScope = new(null);
    private HashSet<string> _locals = new(StringComparer.Ordinal);
    private Dictionary<IrValue, string> _names = [];
    private Dictionary<IrValue, DefInfo> _defs = [];
    private Dictionary<IrValue, IrValue> _alias = [];
    private Dictionary<IrValue, Mem> _mems = [];
    private HashSet<BasicBlock> _reach = [];
    private readonly List<Ctx> _ctx = [];
    private readonly HashSet<BasicBlock> _activeHeaders = [];
    private bool _inContinuing;
    private int _unnamed;
    private int _flow;
    private SourceSpan _span;

    private void Put(string text) => _lines.Add(new Line(text, _indent));

    private void Open(string header)
    {
        Put(header + " {");
        _indent++;
        _scope = new Scope(_scope);
    }

    private void Close(string suffix = "")
    {
        _scope = _scope.Parent ?? _rootScope;
        _indent--;
        Put("}" + suffix);
    }

    private string NewLocal(string? hint)
    {
        string b = _o.KeepNames && !string.IsNullOrEmpty(hint)
            ? WgslNames.Sanitize(hint, "v")
            : "v" + (_unnamed++).ToString(CultureInfo.InvariantCulture);
        return WgslNames.Unique(_locals, b, _moduleNames);
    }

    private string Temp(string expr)
    {
        string name = WgslNames.Unique(_locals, "t" + (_unnamed++).ToString(CultureInfo.InvariantCulture), _moduleNames);
        _lines.Add(new Line("", _indent, new DefInfo(name, "", _scope, false), expr));
        return name;
    }

    /// <summary>Declares the result of an instruction: <c>let name = expr;</c> (or <c>var</c> when the value is modified afterwards).</summary>
    private string Define(Instruction i, string expr, bool isVar = false)
    {
        string name = NewLocal(i.Name);
        var info = new DefInfo(name, TypeName(i.Type), _scope, isVar);
        _lines.Add(new Line("", _indent, info, expr));
        _names[i] = name;
        _defs[i] = info;
        return name;
    }

    private void AliasTo(Instruction i, IrValue target) => _alias[i] = target;

    private static bool IsIdentifier(string s)
    {
        if (s.Length == 0)
        {
            return false;
        }

        foreach (char c in s)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_'))
            {
                return false;
            }
        }

        return !char.IsDigit(s[0]);
    }

    /// <summary>The expression for a value. Records that a declared value is used outside its scope so it can be hoisted.</summary>
    private string Val(IrValue v)
    {
        for (int guard = 0; v is Instruction && _alias.TryGetValue(v, out var next) && guard < 1000; guard++)
        {
            v = next;
        }

        switch (v)
        {
            case Constant c:
                return ConstExpr(c);
            case GlobalVariable g:
                if (_globalNames.TryGetValue(g, out var gn))
                {
                    return gn;
                }

                Fail(WgslCodes.MalformedIr, "The global '" + g.Name + "' is used but was not declared for this entry point.", g.Span);
                return "0";
            default:
                if (_names.TryGetValue(v, out var n))
                {
                    if (_defs.TryGetValue(v, out var d) && !d.Hoisted && !Scope.Encloses(d.Scope, _scope))
                    {
                        d.Hoisted = true;
                        _prologue.Add("var " + d.Name + ": " + d.TypeText + ";");
                    }

                    return n;
                }

                // After another error the instruction that would have defined the value was skipped; do not pile on.
                if (!_failed)
                {
                    Fail(WgslCodes.MalformedIr, "A value is used before it is defined; this is a compiler bug in the IR.", _span);
                }

                return "0";
        }
    }

    // ---- function emission -----------------------------------------------------------------------

    private static List<Instruction> LeadingPhis(BasicBlock b)
    {
        var list = new List<Instruction>();
        foreach (var i in b.Instructions)
        {
            if (i.Op != Opcode.Phi)
            {
                break;
            }

            list.Add(i);
        }

        return list;
    }

    private static HashSet<BasicBlock> ReachableBlocks(IrFunction f)
    {
        var seen = new HashSet<BasicBlock>();
        if (f.Entry is null)
        {
            return seen;
        }

        var stack = new Stack<BasicBlock>();
        stack.Push(f.Entry);
        seen.Add(f.Entry);
        while (stack.Count > 0)
        {
            var b = stack.Pop();
            foreach (var s in b.Successors)
            {
                if (seen.Add(s))
                {
                    stack.Push(s);
                }
            }
        }

        return seen;
    }

    private void BeginFunction(IrFunction f, bool isEntry)
    {
        _fn = f;
        _isEntryFunction = isEntry;
        _lines = [];
        _prologue = [];
        _indent = 1;
        _rootScope = new Scope(null);
        _scope = _rootScope;
        _locals = new HashSet<string>(StringComparer.Ordinal);
        _names = [];
        _defs = [];
        _alias = [];
        _mems = [];
        _ctx.Clear();
        _activeHeaders.Clear();
        _inContinuing = false;
        _unnamed = 0;
        _flow = 0;
        _reach = ReachableBlocks(f);
    }

    /// <summary>Emits a user function (or the entry wrapper) as WGSL text.</summary>
    private string EmitFunctionText(IrFunction f, bool isEntry, Func<string>? entrySetup)
    {
        BeginFunction(f, isEntry);
        _span = f.Span;
        var paramNames = new List<string>();
        foreach (var p in f.Parameters)
        {
            string name = NewLocal(p.Name);
            _names[p] = name;
            paramNames.Add(name);
        }

        // Pointer parameters become pointer-typed WGSL parameters; their memory is the dereferenced pointer.
        for (int k = 0; k < f.Parameters.Count; k++)
        {
            var p = f.Parameters[k];
            if (p.Type is PointerType pt)
            {
                _mems[p] = new PlainMem(pt.Pointee, "(*" + paramNames[k] + ")", null);
            }
        }

        string header;
        if (isEntry && entrySetup is not null)
        {
            header = entrySetup();
        }
        else
        {
            var sig = new StringBuilder("fn ").Append(_fnNames[f]).Append('(');
            for (int k = 0; k < f.Parameters.Count; k++)
            {
                if (k > 0)
                {
                    sig.Append(", ");
                }

                sig.Append(paramNames[k]).Append(": ").Append(TypeName(f.Parameters[k].Type));
            }

            sig.Append(')');
            if (!f.ReturnType.IsVoid)
            {
                sig.Append(" -> ").Append(TypeName(f.ReturnType));
            }

            header = sig.ToString();
        }

        // Phis are variables assigned on each incoming edge; they are declared at the top so every scope sees them.
        foreach (var b in f.Blocks)
        {
            if (!_reach.Contains(b))
            {
                continue;
            }

            foreach (var phi in LeadingPhis(b))
            {
                string name = NewLocal(phi.Name);
                var info = new DefInfo(name, TypeName(phi.Type), _rootScope, true) { Hoisted = true };
                _names[phi] = name;
                _defs[phi] = info;
                _prologue.Add("var " + name + ": " + info.TypeText + ";");
            }
        }

        if (f.Entry is not null)
        {
            EmitChain(f.Entry, null);
        }

        bool endsInReturn = _lines.Count > 0 && _lines[^1].Indent == 1 && _lines[^1].Text.StartsWith("return", StringComparison.Ordinal);
        if (!endsInReturn)
        {
            if (isEntry)
            {
                if (_entryReturnExpr is not null)
                {
                    Put("return " + _entryReturnExpr + ";");
                }
            }
            else if (!f.ReturnType.IsVoid)
            {
                Put("return " + TypeName(f.ReturnType) + "();");
            }
        }

        var sb = new StringBuilder();
        sb.Append(header).Append(" {\n");
        foreach (string p in _prologue)
        {
            sb.Append("  ").Append(p).Append('\n');
        }

        foreach (var line in _lines)
        {
            sb.Append(line.Render()).Append('\n');
        }

        sb.Append("}\n");
        return sb.ToString();
    }

    // ---- structured control flow -----------------------------------------------------------------

    private void EmitInstructions(BasicBlock b)
    {
        foreach (var i in b.Instructions)
        {
            if (i.IsTerminator)
            {
                break;
            }

            if (i.Op == Opcode.Phi)
            {
                continue;
            }

            _span = i.Span;
            EmitInstruction(i);
        }
    }

    private static List<(Instruction Phi, IrValue Value)> Incoming(BasicBlock from, BasicBlock to)
    {
        var list = new List<(Instruction, IrValue)>();
        foreach (var phi in LeadingPhis(to))
        {
            for (int k = 0; k < phi.Targets.Count; k++)
            {
                if (ReferenceEquals(phi.Targets[k], from))
                {
                    list.Add((phi, phi.Operands[k]));
                    break;
                }
            }
        }

        return list;
    }

    /// <summary>Assigns the phi variables of <paramref name="to"/> for the edge coming from <paramref name="from"/>, as one parallel copy.</summary>
    private void EmitEdge(BasicBlock from, BasicBlock to)
    {
        var list = Incoming(from, to);
        if (list.Count == 0)
        {
            return;
        }

        bool needTemps = false;
        if (list.Count > 1)
        {
            var targets = new HashSet<IrValue>();
            foreach (var (phi, _) in list)
            {
                targets.Add(phi);
            }

            foreach (var (_, v) in list)
            {
                if (targets.Contains(v))
                {
                    needTemps = true;
                }
            }
        }

        var values = new string[list.Count];
        for (int k = 0; k < list.Count; k++)
        {
            string e = Val(list[k].Value);
            values[k] = needTemps ? Temp(e) : e;
        }

        for (int k = 0; k < list.Count; k++)
        {
            string target = _names[list[k].Phi];
            if (values[k] != target)
            {
                Put(target + " = " + values[k] + ";");
            }
        }
    }

    private Ctx? InnermostLoop()
    {
        for (int k = _ctx.Count - 1; k >= 0; k--)
        {
            if (_ctx[k].Kind == CtxKind.Loop)
            {
                return _ctx[k];
            }
        }

        return null;
    }

    private void EmitLoopBreak(Ctx loop)
    {
        int idx = _ctx.IndexOf(loop);
        Ctx? crossed = null;
        for (int k = _ctx.Count - 1; k > idx; k--)
        {
            if (_ctx[k].Kind == CtxKind.Switch)
            {
                crossed = _ctx[k];
                break;
            }
        }

        if (crossed is null)
        {
            Put("break;");
            return;
        }

        // WGSL's break leaves only the innermost switch, so leave the loop through a flag checked after the switch.
        loop.FlagName ??= NewLocal("loop_exit");
        Put(loop.FlagName + " = true;");
        Put("break;");
        crossed.PendingLoop = loop;
    }

    /// <summary>
    /// Follows the edge <paramref name="from"/> to <paramref name="target"/>: assigns phis, then either emits the
    /// jump (<c>break</c>, <c>continue</c>) and returns null, or returns the block whose code comes next.
    /// </summary>
    private BasicBlock? Jump(BasicBlock from, BasicBlock target, BasicBlock? stop)
    {
        EmitEdge(from, target);
        if (ReferenceEquals(target, stop))
        {
            return null;
        }

        var inner = InnermostLoop();
        if (inner is not null && ReferenceEquals(target, inner.Continue))
        {
            if (_inContinuing)
            {
                Unstructured("A branch inside a loop's increment code jumps back into the loop.");
            }
            else
            {
                Put("continue;");
            }

            return null;
        }

        for (int k = _ctx.Count - 1; k >= 0; k--)
        {
            var c = _ctx[k];
            if (!ReferenceEquals(c.Merge, target))
            {
                continue;
            }

            switch (c.Kind)
            {
                case CtxKind.Loop:
                    if (_inContinuing)
                    {
                        Unstructured("A branch inside a loop's increment code leaves the loop.");
                    }
                    else
                    {
                        EmitLoopBreak(c);
                    }

                    return null;
                case CtxKind.Switch:
                {
                    bool loopBetween = false;
                    for (int j = k + 1; j < _ctx.Count; j++)
                    {
                        loopBetween |= _ctx[j].Kind == CtxKind.Loop;
                    }

                    if (loopBetween)
                    {
                        Unstructured("A branch leaves a loop and a switch at once.");
                    }
                    else
                    {
                        Put("break;");
                    }

                    return null;
                }

                default:
                    Unstructured("A branch skips the end of an enclosing if statement.");
                    return null;
            }
        }

        if (_activeHeaders.Contains(target))
        {
            Unstructured("A branch re-enters a loop header other than through its continue block.");
            return null;
        }

        return target;
    }

    private void Unstructured(string what) =>
        Unsupported("The control flow of this shader cannot be written as structured WGSL (" + what + ") Restructure the HLSL around this statement.");

    private void JumpInto(BasicBlock from, BasicBlock target, BasicBlock? stop)
    {
        var next = Jump(from, target, stop);
        if (next is not null)
        {
            EmitChain(next, stop);
        }
    }

    private bool IsExitEdge(BasicBlock target, BasicBlock? stop)
    {
        if (ReferenceEquals(target, stop))
        {
            return true;
        }

        var inner = InnermostLoop();
        if (inner is not null && ReferenceEquals(target, inner.Continue))
        {
            return true;
        }

        foreach (var c in _ctx)
        {
            if (ReferenceEquals(c.Merge, target))
            {
                return true;
            }
        }

        return false;
    }

    private static string Not(string e) => IsIdentifier(e) ? "!" + e : "!(" + e + ")";

    private void EmitChain(BasicBlock start, BasicBlock? stop)
    {
        if (_flow >= MaxDepth)
        {
            Fail(WgslCodes.Unsupported, "The shader nests control flow too deeply to emit as WGSL; flatten the nesting.", _span);
            return;
        }

        _flow++;
        try
        {
            var b = start;
            while (true)
            {
                if (ReferenceEquals(b, stop) || !_reach.Contains(b) || _failed)
                {
                    return;
                }

                if (b.Structure == StructureKind.Loop && !_activeHeaders.Contains(b))
                {
                    _activeHeaders.Add(b);
                    EmitLoop(b);
                    _activeHeaders.Remove(b);
                    if (b.MergeBlock is null)
                    {
                        return;
                    }

                    b = b.MergeBlock;
                    continue;
                }

                EmitInstructions(b);
                var t = b.Terminator;
                if (t is null)
                {
                    return;
                }

                _span = t.Span;
                switch (t.Op)
                {
                    case Opcode.Branch:
                    {
                        var next = Jump(b, t.Targets[0], stop);
                        if (next is null)
                        {
                            return;
                        }

                        b = next;
                        break;
                    }

                    case Opcode.CondBranch:
                    {
                        var next = EmitCondBranch(b, t, stop);
                        if (next is null)
                        {
                            return;
                        }

                        b = next;
                        break;
                    }

                    case Opcode.Switch:
                    {
                        var next = EmitSwitch(b, t);
                        if (next is null)
                        {
                            return;
                        }

                        b = next;
                        break;
                    }

                    case Opcode.Return:
                        EmitReturn(t);
                        return;
                    case Opcode.Kill:
                        Put("discard;");
                        EmitReturn(null);
                        return;
                    default:
                        return;
                }
            }
        }
        finally
        {
            _flow--;
        }
    }

    private void EmitReturn(Instruction? t)
    {
        if (_isEntryFunction)
        {
            Put(_entryReturnExpr is null ? "return;" : "return " + _entryReturnExpr + ";");
        }
        else if (t is not null && t.Operands.Count == 1)
        {
            Put("return " + Val(t.Operands[0]) + ";");
        }
        else if (_fn.ReturnType.IsVoid)
        {
            Put("return;");
        }
        else
        {
            Put("return " + TypeName(_fn.ReturnType) + "();");
        }
    }

    private BasicBlock? EmitCondBranch(BasicBlock b, Instruction t, BasicBlock? stop)
    {
        string cond = Val(t.Operands[0]);
        var t0 = t.Targets[0];
        var t1 = t.Targets[1];
        if (b.Structure == StructureKind.Selection && b.MergeBlock is { } merge)
        {
            _ctx.Add(new Ctx(CtxKind.If, merge));
            Open("if (" + cond + ")");
            JumpInto(b, t0, merge);
            Close();
            int mark = _lines.Count;
            Put("else {");
            _indent++;
            _scope = new Scope(_scope);
            int before = _lines.Count;
            JumpInto(b, t1, merge);
            if (_lines.Count == before)
            {
                _scope = _scope.Parent ?? _rootScope;
                _indent--;
                _lines.RemoveRange(mark, _lines.Count - mark);
            }
            else
            {
                Close();
            }

            _ctx.RemoveAt(_ctx.Count - 1);
            return _reach.Contains(merge) ? merge : null;
        }

        // A conditional jump out of the construct that holds it (the test of a loop, `if (c) break;`).
        bool exit0 = IsExitEdge(t0, stop);
        bool exit1 = IsExitEdge(t1, stop);
        if (exit1 && !exit0)
        {
            Open("if (" + Not(cond) + ")");
            JumpInto(b, t1, stop);
            Close();
            return Jump(b, t0, stop);
        }

        if (exit0 && !exit1)
        {
            Open("if (" + cond + ")");
            JumpInto(b, t0, stop);
            Close();
            return Jump(b, t1, stop);
        }

        if (exit0 && exit1)
        {
            Open("if (" + cond + ")");
            JumpInto(b, t0, stop);
            Close();
            JumpInto(b, t1, stop);
            return null;
        }

        Unstructured("a conditional branch has no join point.");
        return null;
    }

    private BasicBlock? EmitSwitch(BasicBlock b, Instruction t)
    {
        if (b.MergeBlock is not { } merge)
        {
            Fail(WgslCodes.MalformedIr, "A switch has no merge block.", t.Span);
            return null;
        }

        var selectorType = t.Operands[0].Type.ScalarElement ?? ScalarType.Int;
        bool isSigned = selectorType.IsSignedInteger;
        string selector = Val(t.Operands[0]);
        var defaultTarget = t.Targets[0];
        var order = new List<BasicBlock>();
        var values = new Dictionary<BasicBlock, List<string>>();
        for (int k = 0; k < t.Immediates.Count; k++)
        {
            var target = t.Targets[k + 1];
            if (!values.TryGetValue(target, out var list))
            {
                list = [];
                values[target] = list;
                order.Add(target);
            }

            int v = t.Immediates[k];
            list.Add(isSigned ? v.ToString(CultureInfo.InvariantCulture) + "i" : ((uint)v).ToString(CultureInfo.InvariantCulture) + "u");
        }

        var ctx = new Ctx(CtxKind.Switch, merge);
        _ctx.Add(ctx);
        Open("switch (" + selector + ")");
        bool defaultDone = false;
        foreach (var target in order)
        {
            var labels = new List<string>(values[target]);
            bool isDefault = ReferenceEquals(target, defaultTarget);
            if (isDefault)
            {
                labels.Add("default");
                defaultDone = true;
            }

            Open("case " + string.Join(", ", labels));
            JumpInto(b, target, merge);
            Close();
        }

        if (!defaultDone)
        {
            Open("default");
            JumpInto(b, defaultTarget, merge);
            Close();
        }

        Close();
        _ctx.RemoveAt(_ctx.Count - 1);
        if (ctx.PendingLoop is { } loop)
        {
            Open("if (" + loop.FlagName + ")");
            EmitLoopBreak(loop);
            Close();
        }

        return _reach.Contains(merge) ? merge : null;
    }

    private void EmitLoop(BasicBlock h)
    {
        if (h.MergeBlock is not { } merge || h.ContinueBlock is not { } cont || h.Terminator is not { Op: Opcode.Branch } term)
        {
            Fail(WgslCodes.MalformedIr, "A loop header has no merge or continue block.", _span);
            return;
        }

        var ctx = new Ctx(CtxKind.Loop, merge) { Continue = cont };
        int insertAt = _lines.Count;
        int outerIndent = _indent;
        _ctx.Add(ctx);
        Open("loop");
        EmitInstructions(h);
        var first = term.Targets[0];
        EmitEdge(h, first);
        if (!ReferenceEquals(first, cont))
        {
            EmitChain(first, cont);
        }

        if (_reach.Contains(cont) && !ReferenceEquals(cont, h))
        {
            Open("continuing");
            EmitContinuing(h, cont, merge);
            Close();
        }

        Close();
        _ctx.RemoveAt(_ctx.Count - 1);
        if (ctx.FlagName is { } flag)
        {
            _lines.Insert(insertAt, new Line("var " + flag + ": bool = false;", outerIndent));
        }
    }

    private void EmitContinuing(BasicBlock h, BasicBlock cont, BasicBlock merge)
    {
        bool saved = _inContinuing;
        _inContinuing = true;
        var t = cont.Terminator;
        bool doWhile = t is { Op: Opcode.CondBranch } && cont.Structure == StructureKind.None
            && ((ReferenceEquals(t.Targets[0], h) && ReferenceEquals(t.Targets[1], merge))
                || (ReferenceEquals(t.Targets[1], h) && ReferenceEquals(t.Targets[0], merge)));
        if (doWhile && t is not null)
        {
            // The loop test sits in the continue block: `break if` ends the loop, and the header's phis are only
            // updated when the loop goes round again, so code after the loop still sees the last iteration's values.
            EmitInstructions(cont);
            string cond = Val(t.Operands[0]);
            bool backIsTrue = ReferenceEquals(t.Targets[0], h);
            string again = backIsTrue ? cond : Not(cond);
            string leave = backIsTrue ? Not(cond) : cond;
            if (Incoming(cont, h).Count > 0)
            {
                Open("if (" + again + ")");
                EmitEdge(cont, h);
                Close();
            }

            if (Incoming(cont, merge).Count > 0)
            {
                Open("if (" + leave + ")");
                EmitEdge(cont, merge);
                Close();
            }

            Put("break if " + leave + ";");
        }
        else
        {
            EmitChain(cont, h);
        }

        _inContinuing = saved;
    }
}
