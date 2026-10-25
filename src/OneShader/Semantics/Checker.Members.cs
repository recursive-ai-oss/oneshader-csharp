using System;
using System.Collections.Generic;
using System.Text;
using OneShader.Diagnostics;
using OneShader.Syntax;
using OneShader.Types;

namespace OneShader.Semantics;

// Member functions, inheritance, operator overloads and overload resolution shared with templates.
internal sealed partial class Checker
{
    // ------------------------------------------------------------ fields and methods through bases

    private bool FindField(StructType st, string name, out StructType holder, out int index)
    {
        for (var s = st; s is not null; s = s.BaseStruct)
        {
            if (_structInfos.TryGetValue(s, out var si) && si.FieldIndex.TryGetValue(name, out index))
            {
                holder = s;
                return true;
            }
        }

        holder = st;
        index = -1;
        return false;
    }

    private FunctionGroup? FindMethodGroup(StructType st, string name, out StructType holder)
    {
        // A name declared in a derived struct hides the base's overloads of it, as in C++.
        for (var s = st; s is not null; s = s.BaseStruct)
        {
            if (_structInfos.TryGetValue(s, out var si) && si.Methods.TryGetValue(name, out var g))
            {
                holder = s;
                return g;
            }
        }

        holder = st;
        return null;
    }

    /// <summary>The object <paramref name="target"/> viewed as its ancestor <paramref name="holder"/>: <c>d.__base_B.__base_A</c>.</summary>
    private Expression BuildBaseAccess(Expression target, StructType from, StructType holder, SourceSpan span)
    {
        var cur = target;
        var curType = from;
        while (!ReferenceEquals(curType, holder) && curType.BaseStruct is { } b)
        {
            var ma = new MemberAccessExpression(span, cur, new Identifier(span, BaseFieldName(b)));
            CheckExpr(ma);
            cur = ma;
            curType = b;
        }

        return cur;
    }

    private string NoMemberFunctionMessage(StructType st, string name)
    {
        var names = new List<string>();
        for (var s = st; s is not null; s = s.BaseStruct)
        {
            if (_structInfos.TryGetValue(s, out var si))
            {
                foreach (var n in si.MethodOrder)
                {
                    if (!n.StartsWith("operator", StringComparison.Ordinal) && !names.Contains(n))
                    {
                        names.Add(n);
                    }
                }
            }
        }

        var sb = new StringBuilder();
        sb.Append("The struct '").Append(st.Name).Append("' has no member function '").Append(name).Append("'.");
        if (FindField(st, name, out _, out _))
        {
            sb.Append(" '").Append(name).Append("' is a data member; remove the parentheses to read it.");
            return sb.ToString();
        }

        string? hint = Suggest(name, names);
        if (hint is not null)
        {
            sb.Append(" Did you mean '").Append(hint).Append("'?");
        }
        else if (names.Count > 0)
        {
            sb.Append(" Its member functions are: ").Append(string.Join(", ", names)).Append('.');
        }
        else
        {
            sb.Append(" It declares no member functions; add one inside the struct.");
        }

        return sb.ToString();
    }

    private HlslType CheckThis(ThisExpression t)
    {
        var info = InfoOf(t);
        if (_fn?.ThisSymbol is not { } ts)
        {
            Err(SemanticCodes.BadOperator, _fn?.Owner is not null
                ? "'this' cannot be used in a static member function; it has no object."
                : "'this' can only be used inside a member function of a struct.", t.Span);
            return HlslType.Error;
        }

        info.Symbol = ts;
        info.IsLValue = true;
        return ts.Type;
    }

    /// <summary>A name used inside a member function that is not a local: a member of the struct, reached through <c>this</c>.</summary>
    private bool TryImplicitMember(NameExpression n, ExprInfo info, out HlslType type)
    {
        type = HlslType.Error;
        if (_fn?.Owner is not { } owner || _fnScope is null || n.Name.Parts.Count != 1 || n.Name.IsGlobal)
        {
            return false;
        }

        string name = n.Name.Last.Text;
        if (_scope.LookupUpTo(name, _fnScope) is not null || !FindField(owner, name, out _, out _))
        {
            return false;
        }

        if (_fn.ThisSymbol is null)
        {
            Err(SemanticCodes.BadMember, "'" + name + "' is a member of '" + owner.Name + "' and cannot be used in a static member function, which has no object.", n.Span);
            type = HlslType.Error;
            return true;
        }

        var self = new ThisExpression(n.Span);
        var access = new MemberAccessExpression(n.Span, self, n.Name.Last);
        type = CheckExpr(access);
        info.Rewritten = access;
        info.IsLValue = true;
        return true;
    }

    private HlslType CheckStructMethodCall(CallExpression c, MemberAccessExpression ma, StructType st, HlslType[] at, ExprInfo info)
    {
        string name = ma.Member.Text;
        var g = FindMethodGroup(st, name, out var holder);
        if (g is null)
        {
            Err(SemanticCodes.BadMember, NoMemberFunctionMessage(st, name), ma.Member.Span);
            return HlslType.Error;
        }

        var self = BuildBaseAccess(ma.Target, st, holder, ma.Span);
        return CallOverloaded(c.Span, name, g, c.Arguments, at, info, c.TemplateArguments, self);
    }

    // ------------------------------------------------------------ operators

    private HlslType CheckOperatorCall(BinaryExpression b, HlslType lt, HlslType rt, ExprInfo info)
    {
        string op = OperatorText(b.Operator);
        string opName = "operator" + op;
        if (lt is StructType ls)
        {
            var g = FindMethodGroup(ls, opName, out var holder);
            if (g is not null)
            {
                var self = BuildBaseAccess(b.Left, ls, holder, b.Span);
                return CallOverloaded(b.Span, opName, g, [b.Right], [rt], info, null, self);
            }

            Err(SemanticCodes.BadOperator, "The operator '" + op + "' does not apply to '" + lt + "' and '" + rt + "': the struct '" + ls.Name + "' declares no '" + opName + "'. Declare it inside the struct, for example 'bool " + opName + "(" + rt + " rhs)'.", b.Span);
            return HlslType.Error;
        }

        Err(SemanticCodes.BadOperator, "The operator '" + op + "' does not apply to '" + lt + "' and '" + rt + "': HLSL 2021 only overloads operators declared in the struct of the left operand, so write '" + rt + " " + op + " " + lt + "' the other way round or call a function.", b.Span);
        return HlslType.Error;
    }

    private HlslType CheckStructIndex(IndexExpression ix, StructType st, HlslType it, ExprInfo info)
    {
        var g = FindMethodGroup(st, "operator[]", out var holder);
        if (g is null)
        {
            Err(SemanticCodes.BadOperator, "The struct '" + st.Name + "' cannot be indexed with []: declare 'operator[]' inside it, for example 'float operator[](int i)'.", ix.Span);
            return HlslType.Error;
        }

        var self = BuildBaseAccess(ix.Target, st, holder, ix.Span);
        return CallOverloaded(ix.Span, "operator[]", g, [ix.Index], [it], info, null, self);
    }

    // ------------------------------------------------------------ overload resolution

    /// <summary>One way a call could be resolved: a declared function or a template with its arguments worked out.</summary>
    private sealed class Candidate
    {
        public FunctionSymbol? Fn { get; set; }

        public TemplateMatch? Template { get; set; }

        /// <summary>The parameters after the hidden <c>this</c>.</summary>
        public List<ParamInfo> Params { get; } = [];

        public StructType? ThisOwner { get; set; }
    }

    private static string DescribeArgs(HlslType[] at)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < at.Length; i++)
        {
            sb.Append(i > 0 ? ", " : "").Append(at[i]);
        }

        return sb.ToString();
    }

    /// <summary>Resolves a call to one of the overloads in <paramref name="g"/>, records it in <paramref name="info"/> and returns the result type.</summary>
    private HlslType CallOverloaded(SourceSpan span, string name, FunctionGroup g, IReadOnlyList<Expression> args, HlslType[] at, ExprInfo info, IReadOnlyList<TemplateArgument>? targs, Expression? thisArg)
    {
        if (AnyError(at))
        {
            return HlslType.Error;
        }

        bool explicitTemplate = targs is { Count: > 0 };
        var cands = new List<Candidate>();
        var notes = new List<string>();
        if (!explicitTemplate)
        {
            foreach (var f in g.Overloads)
            {
                var cand = new Candidate { Fn = f };
                int first = f.HasThis ? 1 : 0;
                for (int i = first; i < f.Params.Count; i++)
                {
                    cand.Params.Add(f.Params[i]);
                }

                cand.ThisOwner = f.HasThis ? f.Owner : null;
                cands.Add(cand);
            }
        }

        foreach (var t in g.Templates)
        {
            var m = BindTemplate(t, name, targs, args, at, span, thisArg?.Type as StructType, out string why);
            if (m is null)
            {
                notes.Add(why);
                continue;
            }

            var cand = new Candidate { Template = m };
            foreach (var p in m.Params)
            {
                cand.Params.Add(p);
            }

            cand.ThisOwner = m.Owner is not null && !m.IsStatic ? m.Owner : null;
            cands.Add(cand);
        }

        Candidate? best = null;
        long bestCost = long.MaxValue;
        bool tie = false;
        bool anyCountMatch = false;
        bool needsObject = false;
        foreach (var cand in cands)
        {
            int required = 0;
            foreach (var p in cand.Params)
            {
                if (p.Default is null)
                {
                    required++;
                }
            }

            if (at.Length < required || at.Length > cand.Params.Count)
            {
                continue;
            }

            anyCountMatch = true;
            long cost = 0;
            if (cand.ThisOwner is not null)
            {
                if (thisArg?.Type is not { } tt)
                {
                    needsObject = true;
                    continue;
                }

                int tc = TypeOps.Cost(tt, cand.ThisOwner);
                if (tc == int.MaxValue)
                {
                    continue;
                }

                cost += tc;
            }

            for (int i = 0; i < at.Length && cost != long.MaxValue; i++)
            {
                var p = cand.Params[i];
                int pc = p.IsOut && !p.IsIn
                    ? TypeOps.Cost(p.Type, at[i])
                    : TypeOps.Cost(at[i], p.Type);
                if (p.IsOut && p.IsIn && TypeOps.Cost(p.Type, at[i]) == int.MaxValue)
                {
                    pc = int.MaxValue;
                }

                cost = pc == int.MaxValue ? long.MaxValue : cost + pc;
            }

            if (cost == long.MaxValue)
            {
                continue;
            }

            // A declared function beats a template that matches equally well.
            long ranked = cost * 2 + (cand.Template is not null ? 1 : 0);
            if (ranked < bestCost)
            {
                best = cand;
                bestCost = ranked;
                tie = false;
            }
            else if (ranked == bestCost)
            {
                tie = true;
            }
        }

        if (best is null)
        {
            ReportNoOverload(span, name, g, at, cands, notes, anyCountMatch, needsObject, explicitTemplate);
            return HlslType.Error;
        }

        if (tie)
        {
            Err(SemanticCodes.AmbiguousCall, "The call to '" + name + "' is ambiguous: more than one overload converts the arguments equally well. Cast an argument to the exact parameter type.", span);
        }

        var fn = best.Fn;
        if (best.Template is not null)
        {
            fn = InstantiateFunction(best.Template, span);
            if (fn is null)
            {
                return HlslType.Error;
            }

            best.Params.Clear();
            int first = fn.HasThis ? 1 : 0;
            for (int i = first; i < fn.Params.Count; i++)
            {
                best.Params.Add(fn.Params[i]);
            }
        }

        var target = fn!.Definition ?? fn;
        var ptypes = new HlslType[at.Length];
        for (int i = 0; i < at.Length; i++)
        {
            var p = best.Params[i];
            ptypes[i] = p.Type;
            if (p.IsOut)
            {
                string why = WriteBlocker(args[i]);
                if (why.Length > 0)
                {
                    Err(SemanticCodes.NotAssignable, "Argument " + Num(i + 1) + " of '" + name + "' is an " + ((p.Mods & Modifiers.In) != 0 ? "inout" : "out") + " parameter and must be a variable: " + why, args[i].Span);
                }
            }

            if (p.IsIn && !(p.IsOut && !p.IsIn))
            {
                CheckConvertible(args[i], p.Type);
            }
        }

        if (target.InProgress || ReferenceEquals(target, _fn))
        {
            Err(SemanticCodes.BadFunction, "The function '" + name + "' calls itself; HLSL does not allow recursion. Rewrite it as a loop.", span);
        }

        info.Function = fn;
        info.ParamTypes = ptypes;
        info.Args = args;
        info.ThisArg = fn.HasThis ? thisArg : null;
        fn.Called = true;
        _fn?.Callees.Add(fn);
        return fn.ReturnType;
    }

    private void ReportNoOverload(SourceSpan span, string name, FunctionGroup g, HlslType[] at, List<Candidate> cands, List<string> notes, bool anyCountMatch, bool needsObject, bool explicitTemplate)
    {
        if (needsObject)
        {
            Err(SemanticCodes.BadFunction, "The member function '" + name + "' needs an object: call it as object." + name + "(...) or from a non-static member function of the same struct.", span);
            return;
        }

        if (cands.Count == 0 && notes.Count > 0)
        {
            Err(SemanticCodes.NoOverload, "No instance of the function template '" + name + "' accepts (" + DescribeArgs(at) + "): " + string.Join("; ", notes) + ".", span);
            return;
        }

        if (cands.Count == 0)
        {
            Err(SemanticCodes.NoOverload, "'" + name + "' has no overload" + (explicitTemplate ? " that is a template" : "") + " to call.", span);
            return;
        }

        if (!anyCountMatch)
        {
            var counts = new List<string>();
            foreach (var c in cands)
            {
                counts.Add(Num(c.Params.Count));
            }

            string expected = cands.Count == 1
                ? "expects " + Num(cands[0].Params.Count) + " argument" + (cands[0].Params.Count == 1 ? "" : "s")
                : "takes " + string.Join(" or ", counts) + " arguments";
            Err(SemanticCodes.ArgumentCount, "The function '" + name + "' " + expected + " but the call passes " + Num(at.Length) + ".", span);
            return;
        }

        var sb = new StringBuilder();
        sb.Append("No overload of '").Append(name).Append("' accepts (").Append(DescribeArgs(at)).Append("). Candidates: ");
        for (int i = 0; i < cands.Count; i++)
        {
            sb.Append(i > 0 ? "; " : "").Append(CandidateSignature(name, cands[i]));
        }

        sb.Append('.');
        if (notes.Count > 0)
        {
            sb.Append(" Templates skipped: ").Append(string.Join("; ", notes)).Append('.');
        }

        Err(SemanticCodes.NoOverload, sb.ToString(), span);
        _ = g;
    }

    private static string CandidateSignature(string name, Candidate c)
    {
        var sb = new StringBuilder();
        sb.Append(c.Fn?.ReturnType.ToString() ?? c.Template!.ReturnType.ToString()).Append(' ').Append(name).Append('(');
        for (int i = 0; i < c.Params.Count; i++)
        {
            sb.Append(i > 0 ? ", " : "").Append(c.Params[i].Type);
        }

        return sb.Append(')').ToString();
    }

    // ------------------------------------------------------------ sizeof

    /// <summary>sizeof in bytes as DXC reports it: members packed one after another with no padding, half as 4 bytes unless 16-bit types are on.</summary>
    private static long SizeOfType(HlslType t, int depth)
    {
        if (depth > 64)
        {
            return -1;
        }

        switch (t)
        {
            case ScalarType s:
                return s.ScalarKind == ScalarKind.Bool ? 4 : TypeOps.BitWidth(s) / 8;
            case VectorType v:
                return v.Count * SizeOfType(v.Element, depth + 1);
            case MatrixType m:
                return (long)m.Rows * m.Columns * SizeOfType(m.Element, depth + 1);
            case EnumType e:
                return SizeOfType(e.Underlying, depth + 1);
            case ArrayType a:
            {
                long el = SizeOfType(a.Element, depth + 1);
                return el < 0 || a.Length <= 0 ? -1 : el * a.Length;
            }

            case StructType st:
            {
                long sum = 0;
                foreach (var f in st.Fields)
                {
                    long fs = SizeOfType(f.Type, depth + 1);
                    if (fs < 0)
                    {
                        return -1;
                    }

                    sum += fs;
                }

                return sum;
            }

            default:
                return -1;
        }
    }

    private HlslType CheckSizeOf(SizeOfExpression s)
    {
        var info = InfoOf(s);
        HlslType t = s.TypeOperand is { } to ? ResolveType(to) : CheckExpr(s.ExpressionOperand!);
        if (t.IsError)
        {
            return HlslType.Error;
        }

        long size = SizeOfType(t, 0);
        if (size < 0)
        {
            Err(SemanticCodes.BadType, "sizeof needs a type with a known size, but '" + t + "' has none (resources, samplers and unsized arrays do not).", s.Span);
            return HlslType.Error;
        }

        info.Const = ConstValue.FromInt(ScalarType.UInt, size);
        return ScalarType.UInt;
    }

    // ------------------------------------------------------------ barriers

    private readonly HashSet<FunctionSymbol> _syncReported = [];

    /// <summary>Barriers that wait for a thread group exist only in compute shaders; find the ones a vertex or pixel entry point can reach.</summary>
    private void CheckGroupSyncStages()
    {
        foreach (var e in Entries)
        {
            if (e.Stage == ShaderStage.Compute)
            {
                continue;
            }

            var seen = new HashSet<FunctionSymbol>();
            var stack = new Stack<FunctionSymbol>();
            stack.Push(e.Function);
            while (stack.Count > 0)
            {
                var f = stack.Pop();
                f = f.Definition ?? f;
                if (!seen.Add(f))
                {
                    continue;
                }

                if (f.GroupSyncSpan is { } where && _syncReported.Add(f))
                {
                    Err(SemanticCodes.BadAtomic, "'" + f.GroupSyncName + "' synchronizes a thread group, which only compute shaders have, but the " + e.Stage.ToString().ToLowerInvariant() + " shader '" + e.Function.Name + "' reaches it. Remove the call, or keep it in code that only compute entry points run.", where);
                }

                for (int i = f.Callees.Count - 1; i >= 0; i--)
                {
                    stack.Push(f.Callees[i]);
                }
            }
        }
    }
}
