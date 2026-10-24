using System.Collections.Generic;
using System.Text;
using OneShader.Syntax;

namespace OneShader.Semantics;

/// <summary>Whole-function checks that need control flow: falling off the end without a value, and call cycles.</summary>
internal sealed partial class Checker
{
    private const int FlowDepthLimit = 200;

    private static bool IsConstTrue(Expression? e) =>
        e is null || (e.Info?.Const is { } cv && !cv.IsZero);

    /// <summary>True when control can reach the end of <paramref name="s"/>. <paramref name="breaks"/> is set when a <c>break</c> leaves the innermost loop or switch.</summary>
    private static bool Completes(Statement s, ref bool breaks, int depth)
    {
        if (depth > FlowDepthLimit)
        {
            return true; // Too deep to judge; the parser's own limit reports such input.
        }

        switch (s)
        {
            case ReturnStatement:
            case ContinueStatement:
                return false;
            case BreakStatement:
                breaks = true;
                return false;
            case BlockStatement b:
                foreach (var inner in b.Statements)
                {
                    if (!Completes(inner, ref breaks, depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            case IfStatement i:
            {
                bool t = Completes(i.Then, ref breaks, depth + 1);
                bool e = i.Else is null || Completes(i.Else, ref breaks, depth + 1);
                return t || e;
            }

            case ForStatement f:
            {
                bool inner = false;
                Completes(f.Body, ref inner, depth + 1);
                return !IsConstTrue(f.Condition) || inner;
            }

            case WhileStatement w:
            {
                bool inner = false;
                Completes(w.Body, ref inner, depth + 1);
                return !IsConstTrue(w.Condition) || inner;
            }

            case DoWhileStatement d:
            {
                bool inner = false;
                Completes(d.Body, ref inner, depth + 1);
                return !IsConstTrue(d.Condition) || inner;
            }

            case SwitchStatement sw:
            {
                bool hasDefault = false;
                bool inner = false;
                bool lastCompletes = true;
                foreach (var sec in sw.Sections)
                {
                    foreach (var l in sec.Labels)
                    {
                        hasDefault |= l.IsDefault;
                    }

                    lastCompletes = true;
                    foreach (var st in sec.Statements)
                    {
                        if (!Completes(st, ref inner, depth + 1))
                        {
                            lastCompletes = false;
                            break;
                        }
                    }
                }

                return !hasDefault || inner || lastCompletes;
            }

            default:
                return true;
        }
    }

    private void CheckAllPathsReturn(FunctionSymbol f)
    {
        if (f.ReturnType.IsVoid || f.ReturnType.IsError || f.Decl.Body is not { } body)
        {
            return;
        }

        bool breaks = false;
        if (Completes(body, ref breaks, 0))
        {
            Err(SemanticCodes.MissingReturn, "Not all paths of the function '" + f.Name + "' return a value; add a 'return' at the end or in the missing branch.", f.Span);
        }
    }

    /// <summary>Reports call cycles that span several functions (a function that calls itself is reported at the call).</summary>
    private void DetectCallCycles()
    {
        var state = new Dictionary<FunctionSymbol, int>(); // 1 = on the stack, 2 = finished
        var stackFns = new List<FunctionSymbol>();
        var stackNext = new List<int>();
        foreach (var root in Functions)
        {
            if (state.ContainsKey(root))
            {
                continue;
            }

            state[root] = 1;
            stackFns.Add(root);
            stackNext.Add(0);
            while (stackFns.Count > 0)
            {
                int top = stackFns.Count - 1;
                var fn = stackFns[top];
                if (stackNext[top] >= fn.Callees.Count)
                {
                    state[fn] = 2;
                    stackFns.RemoveAt(top);
                    stackNext.RemoveAt(top);
                    continue;
                }

                var callee = fn.Callees[stackNext[top]++];
                callee = callee.Definition ?? callee;
                if (ReferenceEquals(callee, fn) || !callee.HasBody)
                {
                    continue;
                }

                if (!state.TryGetValue(callee, out int st))
                {
                    state[callee] = 1;
                    stackFns.Add(callee);
                    stackNext.Add(0);
                }
                else if (st == 1)
                {
                    int start = stackFns.IndexOf(callee);
                    var sb = new StringBuilder();
                    for (int i = start; i < stackFns.Count; i++)
                    {
                        sb.Append('\'').Append(stackFns[i].Name).Append("' calls ");
                    }

                    sb.Append('\'').Append(callee.Name).Append('\'');
                    Err(SemanticCodes.BadFunction, "The function '" + callee.Name + "' is part of a call cycle: " + sb + ". HLSL does not allow recursion; rewrite it as a loop.", callee.Span);
                    // One report per cycle: finish the nodes of this cycle so they are not reported again.
                    for (int i = start; i < stackFns.Count; i++)
                    {
                        state[stackFns[i]] = 2;
                    }

                    stackFns.RemoveRange(start, stackFns.Count - start);
                    stackNext.RemoveRange(start, stackNext.Count - start);
                }
            }
        }
    }
}
