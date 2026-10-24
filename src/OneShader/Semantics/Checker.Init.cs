using System;
using System.Collections.Generic;
using OneShader.Syntax;
using OneShader.Types;

namespace OneShader.Semantics;

/// <summary>
/// Initializer lists. HLSL fills aggregates from a flat run of values: <c>float2x2 m = {1,2,3,4}</c> and a struct of
/// vectors from scalars both work, a braced sub-list starts a new aggregate, and a vector-valued expression supplies
/// several scalars at once. The checker and the lowering walk the list with the same cursor rules.
/// </summary>
internal sealed partial class Checker
{
    private sealed class InitCursor(IReadOnlyList<Expression> elems)
    {
        public IReadOnlyList<Expression> Elems { get; } = elems;

        public int Pos { get; set; }

        /// <summary>Scalars left over from a vector or matrix expression already consumed.</summary>
        public int Pending { get; set; }

        /// <summary>Set when a scalar was needed but the list had run out.</summary>
        public bool Short { get; set; }

        public bool Exhausted => Pos >= Elems.Count && Pending == 0;
    }

    private static int FlatCount(HlslType t) => t switch
    {
        ArrayType a => a.Length * FlatCount(a.Element),
        StructType st => FlatSum(st),
        _ => Math.Max(1, TypeOps.Count(t)),
    };

    private static int FlatSum(StructType st)
    {
        int n = 0;
        foreach (var f in st.Fields)
        {
            n += FlatCount(f.Type);
        }

        return n;
    }

    /// <summary>Length of an unsized array initialized from a list: flat values fill elements in order, so the count is not the number of list items.</summary>
    private int InferListLength(InitializerListExpression il, HlslType element)
    {
        int per = FlatCount(element);
        int slots = 0;
        int partial = 0;
        foreach (var e in il.Elements)
        {
            if (e is InitializerListExpression)
            {
                slots += partial > 0 ? 2 : 1;
                partial = 0;
                continue;
            }

            var t = e.Type ?? CheckExpr(e);
            if (t.IsError)
            {
                continue;
            }

            if (partial == 0 && element is not ScalarType && t.Equals(element))
            {
                slots++;
                continue;
            }

            partial += Math.Max(1, TypeOps.Count(t));
            while (partial >= per)
            {
                slots++;
                partial -= per;
            }
        }

        return partial > 0 ? slots + 1 : slots;
    }

    private void CheckInitializerList(InitializerListExpression il, HlslType target)
    {
        il.Type = target;
        InfoOf(il);
        if (target.IsError)
        {
            foreach (var el in il.Elements)
            {
                if (el is InitializerListExpression sub)
                {
                    CheckInitializerList(sub, HlslType.Error);
                }
                else
                {
                    CheckExpr(el);
                }
            }

            return;
        }

        if (!Enter(il.Span))
        {
            Leave();
            return;
        }

        try
        {
            switch (target)
            {
                case ScalarType:
                    if (il.Elements.Count != 1)
                    {
                        Err(SemanticCodes.TypeMismatch, "A '" + target + "' needs exactly one value in its initializer.", il.Span);
                    }

                    foreach (var el in il.Elements)
                    {
                        CheckInitializer(el, target);
                    }

                    break;
                case ArrayType or StructType or VectorType or MatrixType:
                {
                    var c = new InitCursor(il.Elements);
                    CheckAggregate(c, target);
                    if (target is VectorType or MatrixType && c.Short)
                    {
                        Err(SemanticCodes.TypeMismatch, "'" + target + "' needs " + Num(TypeOps.Count(target)) + " values in its initializer but fewer were given.", il.Span);
                    }
                    else if (!c.Exhausted)
                    {
                        Err(SemanticCodes.TypeMismatch, "The initializer has more values than '" + target + "' holds; remove the extra values.", il.Span);
                        for (; c.Pos < il.Elements.Count; c.Pos++)
                        {
                            if (il.Elements[c.Pos] is not InitializerListExpression)
                            {
                                CheckExpr(il.Elements[c.Pos]);
                            }
                        }
                    }

                    break;
                }

                default:
                    Err(SemanticCodes.TypeMismatch, "'" + target + "' cannot be initialized with a list.", il.Span);
                    foreach (var el in il.Elements)
                    {
                        CheckExpr(el);
                    }

                    break;
            }
        }
        finally
        {
            Leave();
        }
    }

    private void CheckAggregate(InitCursor c, HlslType s)
    {
        switch (s)
        {
            case StructType st:
                foreach (var f in st.Fields)
                {
                    if (c.Exhausted)
                    {
                        break;
                    }

                    CheckInitSlot(c, f.Type);
                }

                break;
            case ArrayType at:
                for (int i = 0; (at.Length == 0 || i < at.Length) && !c.Exhausted; i++)
                {
                    CheckInitSlot(c, at.Element);
                }

                break;
            case VectorType vt:
                TakeInitScalars(c, vt.Count, vt.Element);
                break;
            case MatrixType mt:
                for (int r = 0; r < mt.Rows; r++)
                {
                    CheckInitSlot(c, mt.Columns == 1 ? mt.Element : HlslType.Vector(mt.Element, mt.Columns));
                }

                break;
            default:
                TakeInitScalars(c, 1, s.ScalarElement ?? ScalarType.Float);
                break;
        }
    }

    private void CheckInitSlot(InitCursor c, HlslType s)
    {
        if (c.Exhausted)
        {
            c.Short = true;
            return;
        }

        if (c.Pending == 0)
        {
            var e = c.Elems[c.Pos];
            if (e is InitializerListExpression sub)
            {
                c.Pos++;
                CheckInitializerList(sub, s);
                return;
            }

            var t = e.Type ?? CheckExpr(e);
            if (t.IsError)
            {
                c.Pos++;
                return;
            }

            if (s is not ScalarType && t.Equals(s))
            {
                c.Pos++;
                return;
            }
        }

        CheckAggregate(c, s);
    }

    private void TakeInitScalars(InitCursor c, int n, ScalarType target)
    {
        for (int k = 0; k < n; k++)
        {
            if (c.Pending > 0)
            {
                c.Pending--;
                continue;
            }

            if (c.Pos >= c.Elems.Count)
            {
                c.Short = true;
                return;
            }

            var e = c.Elems[c.Pos++];
            if (e is InitializerListExpression sub)
            {
                CheckInitializerList(sub, target);
                continue;
            }

            var t = e.Type ?? CheckExpr(e);
            if (t.IsError)
            {
                continue;
            }

            var se = t.ScalarElement;
            if (se is null || TypeOps.Classify(se, target) == TypeOps.Conv.None)
            {
                Err(SemanticCodes.TypeMismatch, "A value of type '" + t + "' cannot initialize a '" + target + "' here. Convert it, or use braces to start a new element.", e.Span);
                continue;
            }

            c.Pending = TypeOps.Count(t) - 1;
        }
    }
}
