using System;
using System.Collections.Generic;
using System.Globalization;
using OneShader.Diagnostics;
using OneShader.Syntax;
using OneShader.Types;

namespace OneShader.Semantics;

internal sealed partial class Checker
{
    private static ExprInfo InfoOf(Expression e) => e.Info ??= new ExprInfo();

    public HlslType CheckExpr(Expression e)
    {
        if (e.Type is not null)
        {
            return e.Type;
        }

        if (!Enter(e.Span))
        {
            Leave();
            return e.Type = HlslType.Error;
        }

        try
        {
            var t = CheckExprCore(e);
            e.Type = t;
            return t;
        }
        finally
        {
            Leave();
        }
    }

    private HlslType CheckExprCore(Expression e)
    {
        switch (e)
        {
            case LiteralExpression l:
                return CheckLiteral(l);
            case NameExpression n:
                return CheckName(n);
            case ParenthesizedExpression p:
            {
                var t = CheckExpr(p.Inner);
                var info = InfoOf(p);
                var inner = p.Inner.Info;
                if (inner is not null)
                {
                    info.Const = inner.Const;
                    info.LiteralValue = inner.LiteralValue;
                    info.IsLValue = inner.IsLValue;
                }

                return t;
            }

            case UnaryExpression u:
                return CheckUnary(u);
            case BinaryExpression b:
                return CheckBinary(b);
            case AssignmentExpression a:
                return CheckAssignment(a);
            case ConditionalExpression c:
                return CheckConditional(c);
            case CommaExpression c:
                CheckExpr(c.Left);
                return CheckExpr(c.Right);
            case CallExpression call:
                return CheckCall(call);
            case ConstructExpression ce:
                return CheckConstruct(ce);
            case CastExpression ca:
                return CheckCast(ca);
            case MemberAccessExpression m:
                return CheckMember(m);
            case IndexExpression ix:
                return CheckIndex(ix);
            case InitializerListExpression il:
                Err(SemanticCodes.TypeMismatch, "An initializer list can only initialize a variable: write 'Type name = { ... };' or construct the value with Type(...).", il.Span);
                foreach (var el in il.Elements)
                {
                    CheckExpr(el);
                }

                return HlslType.Error;
            case SizeOfExpression s:
                return CheckSizeOf(s);
            case ThisExpression t:
                return CheckThis(t);
            default:
                return HlslType.Error;
        }
    }

    // ------------------------------------------------------------ literals and names

    private HlslType CheckLiteral(LiteralExpression l)
    {
        var info = InfoOf(l);
        switch (l.Kind)
        {
            case LiteralKind.Integer:
            {
                ScalarType t;
                ulong v = l.IntegerValue;
                if (l.SuffixType is { } sk)
                {
                    t = ScalarType.Get(sk);
                }
                else if (v <= int.MaxValue)
                {
                    t = ScalarType.Int;
                }
                else if (v <= uint.MaxValue)
                {
                    t = ScalarType.UInt;
                }
                else if (v <= long.MaxValue)
                {
                    t = ScalarType.Get(ScalarKind.Int64);
                }
                else
                {
                    t = ScalarType.Get(ScalarKind.UInt64);
                }

                info.Const = ConstValue.FromInt(t, unchecked((long)v));
                if (l.SuffixType is null && v <= long.MaxValue)
                {
                    info.LiteralValue = (long)v;
                }

                return t;
            }

            case LiteralKind.Float:
            {
                var t = l.SuffixType switch
                {
                    ScalarKind.Double => ScalarType.Double,
                    ScalarKind.Half => Canon(ScalarType.Half),
                    _ => ScalarType.Float,
                };
                // Without a suffix the literal keeps its full precision so that it can still become a double exactly.
                info.Const = l.SuffixType is null ? new ConstValue(t, 0, l.FloatValue) : ConstValue.FromFloat(t, l.FloatValue);
                return t;
            }

            case LiteralKind.Bool:
                info.Const = ConstValue.Bool(l.BoolValue);
                return ScalarType.Bool;
            default:
                Err(SemanticCodes.TypeMismatch, "String literals can only be used as attribute arguments.", l.Span);
                return HlslType.Error;
        }
    }

    private HlslType CheckName(NameExpression n)
    {
        var info = InfoOf(n);
        if (TryImplicitMember(n, info, out var memberType))
        {
            return memberType;
        }

        var sym = LookupQualified(n.Name, out _);
        if (sym is null && IsHeapName(n.Name, out var heapType))
        {
            return heapType;
        }

        if (sym is null)
        {
            ReportUndeclared(n.Name);
            return HlslType.Error;
        }

        info.Symbol = sym;
        n.Declaration = null;
        switch (sym)
        {
            case VariableSymbol v:
                info.IsLValue = true;
                info.Const = v.Const;
                if (v.Kind == VarKind.Spec)
                {
                    _specRefs.Add(n.Span);
                }

                return v.Type;
            case EnumeratorSymbol en:
                info.Const = ConstValue.FromInt(en.Type.Underlying, en.Value);
                return en.Type.Underlying;
            case FunctionGroup:
                Err(SemanticCodes.BadOperator, "'" + n.Name + "' is a function; call it with arguments, for example " + n.Name + "(...).", n.Span);
                return HlslType.Error;
            case TypeSymbol:
                Err(SemanticCodes.BadOperator, "'" + n.Name + "' is a type, not a value. To make a value write " + n.Name + "(...).", n.Span);
                return HlslType.Error;
            case CBufferSymbol:
                Err(SemanticCodes.BadOperator, "'" + n.Name + "' is a constant buffer; use its members by name, for example " + (((CBufferSymbol)sym).Members.Count > 0 ? ((CBufferSymbol)sym).Members[0].Name : "member") + ".", n.Span);
                return HlslType.Error;
            default:
                Err(SemanticCodes.BadOperator, "'" + n.Name + "' cannot be used as a value here.", n.Span);
                return HlslType.Error;
        }
    }

    private void ReportUndeclared(QualifiedName name)
    {
        if (_diag.IsFull)
        {
            return;
        }

        string text = name.ToString();
        if (name.Parts.Count > 1)
        {
            Err(SemanticCodes.Undeclared, "'" + text + "' is not declared. Check the namespace or enum name and the spelling of '" + name.Last.Text + "'.", name.Span);
            return;
        }

        string? hint = Suggest(text, VisibleValueNames());
        Err(SemanticCodes.Undeclared, "'" + text + "' is not declared." + (hint is null ? " Declare it before this use, or check the spelling." : " Did you mean '" + hint + "'?"), name.Span);
    }

    // ------------------------------------------------------------ conversions

    /// <summary>Checks that <paramref name="e"/> can convert implicitly to <paramref name="target"/>, reporting truncation as a warning and impossibility as an error.</summary>
    private bool CheckConvertible(Expression e, HlslType target)
    {
        var from = e.Type ?? CheckExpr(e);
        if (from.IsError || target.IsError)
        {
            return true;
        }

        if (from is DescriptorHeapType heapFrom)
        {
            return ConvertHeapElement(e, heapFrom, target);
        }

        var c = TypeOps.Classify(from, target);
        switch (c)
        {
            case TypeOps.Conv.None:
                string hint = from is VectorType fv && target is VectorType tv && tv.Count > fv.Count
                    ? " To add components build the value, for example " + target + "(value, 0)."
                    : from is StructType || target is StructType ? " Struct types must be identical." : "";
                Err(SemanticCodes.TypeMismatch, "Cannot implicitly convert from '" + from + "' to '" + target + "'." + hint, e.Span);
                return false;
            case TypeOps.Conv.Truncates:
                Warn(SemanticCodes.ImplicitTruncation, "Implicit truncation of '" + from + "' to '" + target + "' drops components. Use a swizzle (for example .xyz) or an explicit cast to silence this warning.", e.Span);
                return true;
            default:
                return true;
        }
    }

    // ------------------------------------------------------------ unary and binary

    private static bool IsConstScalar(Expression e, out ConstValue v)
    {
        if (e.Info?.Const is { } c)
        {
            v = c;
            return true;
        }

        v = default;
        return false;
    }

    private HlslType CheckUnary(UnaryExpression u)
    {
        var ot = CheckExpr(u.Operand);
        var info = InfoOf(u);
        if (ot.IsError)
        {
            return ot;
        }

        var s = ot.ScalarElement;
        switch (u.Operator)
        {
            case UnaryOperator.Plus or UnaryOperator.Minus:
            {
                if (s is null)
                {
                    return BadUnary(u, ot);
                }

                var rt = TypeOps.Reshape(ot, TypeOps.ArithmeticScalar(s));
                info.OperandType = rt;
                if (u.Operand.Info?.LiteralValue is long literal)
                {
                    long value = u.Operator == UnaryOperator.Plus ? literal : unchecked(-literal);
                    return SetLiteral(info, value);
                }

                if (IsConstScalar(u.Operand, out var cv) && rt is ScalarType rs)
                {
                    var c = cv.ConvertTo(rs);
                    info.Const = u.Operator == UnaryOperator.Plus ? c : c.IsFloat ? ConstValue.FromFloat(rs, -c.Float) : ConstValue.FromInt(rs, unchecked(-c.Int));
                }

                return rt;
            }

            case UnaryOperator.LogicalNot:
            {
                if (s is null)
                {
                    return BadUnary(u, ot);
                }

                info.OperandType = TypeOps.Reshape(ot, ScalarType.Bool);
                if (IsConstScalar(u.Operand, out var cv))
                {
                    info.Const = ConstValue.Bool(cv.IsZero);
                }

                return info.OperandType;
            }

            case UnaryOperator.BitwiseNot:
            {
                if (s is null || s.IsFloatingPoint)
                {
                    return BadUnary(u, ot);
                }

                var rt = TypeOps.Reshape(ot, TypeOps.ArithmeticScalar(s));
                info.OperandType = rt;
                if (u.Operand.Info?.LiteralValue is long literal)
                {
                    return SetLiteral(info, ~literal);
                }

                if (IsConstScalar(u.Operand, out var cv) && rt is ScalarType rs)
                {
                    info.Const = ConstValue.FromInt(rs, ~cv.ConvertTo(rs).Int);
                }

                return rt;
            }

            default:
            {
                if (s is null || s.ScalarKind == ScalarKind.Bool)
                {
                    return BadUnary(u, ot);
                }

                string why = WriteBlocker(u.Operand);
                if (why.Length > 0)
                {
                    Err(SemanticCodes.NotAssignable, "Cannot modify the operand of '" + (u.Operator is UnaryOperator.PreIncrement or UnaryOperator.PostIncrement ? "++" : "--") + "': " + why, u.Operand.Span);
                    return HlslType.Error;
                }

                info.OperandType = ot;
                return ot;
            }
        }
    }

    private HlslType BadUnary(UnaryExpression u, HlslType ot)
    {
        Err(SemanticCodes.BadOperator, "The operator does not apply to type '" + ot + "'.", u.Span);
        return HlslType.Error;
    }

    /// <summary>The shape two numeric operands combine to, truncating (with a warning) the larger of two different vectors.</summary>
    private HlslType? CombineShape(HlslType a, HlslType b, SourceSpan where, bool warn)
    {
        switch (a, b)
        {
            case (ScalarType, ScalarType):
                return a;
            case (ScalarType, _):
                return b;
            case (_, ScalarType):
                return a;
            case (VectorType va, VectorType vb):
                if (va.Count == vb.Count)
                {
                    return a;
                }

                if (warn)
                {
                    Warn(SemanticCodes.ImplicitTruncation, "Implicit truncation of vector type: '" + a + "' and '" + b + "' combine to " + (va.Count < vb.Count ? a : b) + ", dropping components. Use a swizzle to make the sizes match.", where);
                }

                return va.Count < vb.Count ? a : b;
            case (MatrixType ma, MatrixType mb):
                if (ma.Rows == mb.Rows && ma.Columns == mb.Columns)
                {
                    return a;
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// The type arithmetic between two operands is done in. A min-precision operand mixed with a plain one gives the plain type, except
    /// that a literal adopts the other side's type (DXC: <c>min16int * 2</c> stays min16int).
    /// </summary>
    private static ScalarType CommonWithLiterals(ScalarType ls, ScalarType rs, Expression left, Expression right)
    {
        var common = TypeOps.Common(TypeOps.ArithmeticScalar(ls), TypeOps.ArithmeticScalar(rs));
        if (!common.IsRelaxed && (ls.IsRelaxed || rs.IsRelaxed))
        {
            var other = ls.IsRelaxed ? right : left;
            if (Unparen(other) is LiteralExpression { SuffixType: null })
            {
                return ScalarType.Relaxed(common);
            }
        }

        return common;
    }

    private static Expression Unparen(Expression e)
    {
        while (e is ParenthesizedExpression p)
        {
            e = p.Inner;
        }

        return e;
    }

    private HlslType CheckBinary(BinaryExpression b)
    {
        var lt = CheckExpr(b.Left);
        var rt = CheckExpr(b.Right);
        var info = InfoOf(b);
        if (lt.IsError || rt.IsError)
        {
            return HlslType.Error;
        }

        if (lt is StructType || rt is StructType)
        {
            return CheckOperatorCall(b, lt, rt, info);
        }

        var ls = lt.ScalarElement;
        var rs = rt.ScalarElement;
        if (ls is null || rs is null)
        {
            Err(SemanticCodes.BadOperator, "The operator '" + OperatorText(b.Operator) + "' does not apply to '" + lt + "' and '" + rt + "'; both sides must be numbers, vectors or matrices.", b.Span);
            return HlslType.Error;
        }

        var shape = CombineShape(lt, rt, b.Span, true);
        if (shape is null)
        {
            Err(SemanticCodes.BadOperator, "The operator '" + OperatorText(b.Operator) + "' cannot combine '" + lt + "' and '" + rt + "': the sizes do not match. For matrix products use mul(a, b).", b.Span);
            return HlslType.Error;
        }

        HlslType operand;
        HlslType result;
        switch (b.Operator)
        {
            case BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.Multiply or BinaryOperator.Divide or BinaryOperator.Modulo:
                operand = TypeOps.Reshape(shape, CommonWithLiterals(ls, rs, b.Left, b.Right));
                result = operand;
                break;
            case BinaryOperator.ShiftLeft or BinaryOperator.ShiftRight:
            {
                if (ls.IsFloatingPoint || rs.IsFloatingPoint)
                {
                    Err(SemanticCodes.BadOperator, "Shifts need integer operands but the operands are '" + lt + "' and '" + rt + "'; cast to int or uint first.", b.Span);
                    return HlslType.Error;
                }

                var lhsType = TypeOps.Reshape(shape, TypeOps.ArithmeticScalar(ls));
                operand = lhsType;
                result = lhsType;
                break;
            }

            case BinaryOperator.BitwiseAnd or BinaryOperator.BitwiseOr or BinaryOperator.BitwiseXor:
            {
                if (ls.IsFloatingPoint || rs.IsFloatingPoint)
                {
                    Err(SemanticCodes.BadOperator, "Bitwise operators need integer operands but the operands are '" + lt + "' and '" + rt + "'; cast to int or uint first.", b.Span);
                    return HlslType.Error;
                }

                var sc = ls.ScalarKind == ScalarKind.Bool && rs.ScalarKind == ScalarKind.Bool ? ScalarType.Bool : CommonWithLiterals(ls, rs, b.Left, b.Right);
                operand = TypeOps.Reshape(shape, sc);
                result = operand;
                break;
            }

            case BinaryOperator.LogicalAnd or BinaryOperator.LogicalOr:
                operand = TypeOps.Reshape(shape, ScalarType.Bool);
                result = operand;
                break;
            default:
            {
                var sc = ls.ScalarKind == ScalarKind.Bool && rs.ScalarKind == ScalarKind.Bool && b.Operator is BinaryOperator.Equal or BinaryOperator.NotEqual
                    ? ScalarType.Bool
                    : TypeOps.Common(TypeOps.ArithmeticScalar(ls), TypeOps.ArithmeticScalar(rs));
                operand = TypeOps.Reshape(shape, sc);
                result = TypeOps.Reshape(shape, ScalarType.Bool);
                break;
            }
        }

        if (b.Left.Info?.LiteralValue is long lv && b.Right.Info?.LiteralValue is long rv && FoldLiteral(b.Operator, lv, rv) is long folded)
        {
            return SetLiteral(info, folded);
        }

        info.OperandType = operand;
        if (operand is ScalarType os && IsConstScalar(b.Left, out var cl) && IsConstScalar(b.Right, out var cr) && result is ScalarType rts)
        {
            info.Const = FoldBinary(b.Operator, cl, cr, os, rts);
        }

        return result;
    }

    /// <summary>Records a literal-only integer value; its type is the smallest of int, uint and int64 that holds it, as for a literal written out.</summary>
    private static HlslType SetLiteral(ExprInfo info, long value)
    {
        var t = value is >= int.MinValue and <= int.MaxValue ? ScalarType.Int
            : value is > int.MaxValue and <= uint.MaxValue ? ScalarType.UInt
            : ScalarType.Get(ScalarKind.Int64);
        info.LiteralValue = value;
        info.OperandType = t;
        info.Const = ConstValue.FromInt(t, value);
        return t;
    }

    private static long? FoldLiteral(BinaryOperator op, long a, long b) => op switch
    {
        BinaryOperator.Add => unchecked(a + b),
        BinaryOperator.Subtract => unchecked(a - b),
        BinaryOperator.Multiply => unchecked(a * b),
        BinaryOperator.Divide when b != 0 && !(a == long.MinValue && b == -1) => a / b,
        BinaryOperator.Modulo when b != 0 && !(a == long.MinValue && b == -1) => a % b,
        BinaryOperator.ShiftLeft => unchecked(a << (int)(b & 63)),
        BinaryOperator.ShiftRight => a >> (int)(b & 63),
        BinaryOperator.BitwiseAnd => a & b,
        BinaryOperator.BitwiseOr => a | b,
        BinaryOperator.BitwiseXor => a ^ b,
        _ => null,
    };

    internal static string OperatorText(BinaryOperator op) => op switch
    {
        BinaryOperator.Add => "+",
        BinaryOperator.Subtract => "-",
        BinaryOperator.Multiply => "*",
        BinaryOperator.Divide => "/",
        BinaryOperator.Modulo => "%",
        BinaryOperator.ShiftLeft => "<<",
        BinaryOperator.ShiftRight => ">>",
        BinaryOperator.BitwiseAnd => "&",
        BinaryOperator.BitwiseOr => "|",
        BinaryOperator.BitwiseXor => "^",
        BinaryOperator.LogicalAnd => "&&",
        BinaryOperator.LogicalOr => "||",
        BinaryOperator.Equal => "==",
        BinaryOperator.NotEqual => "!=",
        BinaryOperator.Less => "<",
        BinaryOperator.LessEqual => "<=",
        BinaryOperator.Greater => ">",
        _ => ">=",
    };

    /// <summary>Evaluates a scalar binary operation exactly as the GPU would, or null when it cannot be folded (division by zero).</summary>
    internal static ConstValue? FoldBinary(BinaryOperator op, ConstValue a, ConstValue b, ScalarType operand, ScalarType result)
    {
        if (op is BinaryOperator.LogicalAnd or BinaryOperator.LogicalOr)
        {
            bool x = !a.IsZero;
            bool y = !b.IsZero;
            return ConstValue.Bool(op == BinaryOperator.LogicalAnd ? x && y : x || y);
        }

        var ca = a.ConvertTo(operand);
        var cb = b.ConvertTo(operand);
        if (operand.IsFloatingPoint)
        {
            return FoldFloat(op, ca.Float, cb.Float, operand, result);
        }

        long x1 = ca.Int;
        long y1 = cb.Int;
        bool unsigned = !operand.IsSignedInteger && operand.ScalarKind != ScalarKind.Bool;
        bool wide = operand.ScalarKind is ScalarKind.UInt64;
        switch (op)
        {
            case BinaryOperator.Add: return ConstValue.FromInt(result, unchecked(x1 + y1));
            case BinaryOperator.Subtract: return ConstValue.FromInt(result, unchecked(x1 - y1));
            case BinaryOperator.Multiply: return ConstValue.FromInt(result, unchecked(x1 * y1));
            case BinaryOperator.Divide:
                if (y1 == 0 || (!unsigned && x1 == long.MinValue && y1 == -1))
                {
                    return null;
                }

                return ConstValue.FromInt(result, wide ? unchecked((long)((ulong)x1 / (ulong)y1)) : x1 / y1);
            case BinaryOperator.Modulo:
                if (y1 == 0 || (!unsigned && x1 == long.MinValue && y1 == -1))
                {
                    return null;
                }

                return ConstValue.FromInt(result, wide ? unchecked((long)((ulong)x1 % (ulong)y1)) : x1 % y1);
            case BinaryOperator.ShiftLeft:
                return ConstValue.FromInt(result, unchecked(x1 << (int)(y1 & (TypeOps.BitWidth(operand) - 1))));
            case BinaryOperator.ShiftRight:
            {
                int sh = (int)(y1 & (TypeOps.BitWidth(operand) - 1));
                return ConstValue.FromInt(result, unsigned && wide ? unchecked((long)((ulong)x1 >> sh)) : x1 >> sh);
            }

            case BinaryOperator.BitwiseAnd: return ConstValue.FromInt(result, x1 & y1);
            case BinaryOperator.BitwiseOr: return ConstValue.FromInt(result, x1 | y1);
            case BinaryOperator.BitwiseXor: return ConstValue.FromInt(result, x1 ^ y1);
            case BinaryOperator.Equal: return ConstValue.Bool(x1 == y1);
            case BinaryOperator.NotEqual: return ConstValue.Bool(x1 != y1);
            case BinaryOperator.Less: return ConstValue.Bool(wide ? (ulong)x1 < (ulong)y1 : x1 < y1);
            case BinaryOperator.LessEqual: return ConstValue.Bool(wide ? (ulong)x1 <= (ulong)y1 : x1 <= y1);
            case BinaryOperator.Greater: return ConstValue.Bool(wide ? (ulong)x1 > (ulong)y1 : x1 > y1);
            case BinaryOperator.GreaterEqual: return ConstValue.Bool(wide ? (ulong)x1 >= (ulong)y1 : x1 >= y1);
        }

        return null;
    }

    private static ConstValue? FoldFloat(BinaryOperator op, double a, double b, ScalarType operand, ScalarType result)
    {
        bool single = operand.ScalarKind != ScalarKind.Double;
        double Op(Func<float, float, float> f32, Func<double, double, double> f64) =>
            single ? f32((float)a, (float)b) : f64(a, b);
        switch (op)
        {
            case BinaryOperator.Add: return ConstValue.FromFloat(result, Op((x, y) => x + y, (x, y) => x + y));
            case BinaryOperator.Subtract: return ConstValue.FromFloat(result, Op((x, y) => x - y, (x, y) => x - y));
            case BinaryOperator.Multiply: return ConstValue.FromFloat(result, Op((x, y) => x * y, (x, y) => x * y));
            case BinaryOperator.Divide: return ConstValue.FromFloat(result, Op((x, y) => x / y, (x, y) => x / y));
            case BinaryOperator.Modulo: return ConstValue.FromFloat(result, Op((x, y) => MathF.IEEERemainder(0, 1) * 0 + x % y, (x, y) => x % y));
            case BinaryOperator.Equal: return ConstValue.Bool(a == b);
            case BinaryOperator.NotEqual: return ConstValue.Bool(a != b);
            case BinaryOperator.Less: return ConstValue.Bool(a < b);
            case BinaryOperator.LessEqual: return ConstValue.Bool(a <= b);
            case BinaryOperator.Greater: return ConstValue.Bool(a > b);
            case BinaryOperator.GreaterEqual: return ConstValue.Bool(a >= b);
        }

        return null;
    }

    // ------------------------------------------------------------ assignment

    /// <summary>Why <paramref name="e"/> cannot be assigned to, or an empty string when it can.</summary>
    private static string WriteBlocker(Expression e)
    {
        if (e.Info?.Rewritten is { } rewritten)
        {
            return WriteBlocker(rewritten);
        }

        switch (e)
        {
            case ThisExpression th:
                return th.Info?.Symbol is VariableSymbol { IsConst: true }
                    ? "'this' is read-only inside a const member function. Remove 'const' from the function, or do not change the object here."
                    : "";
            case ParenthesizedExpression p:
                return WriteBlocker(p.Inner);
            case NameExpression n:
                switch (n.Info?.Symbol)
                {
                    case VariableSymbol { Kind: VarKind.CBufferMember } v:
                        return "'" + v.Name + "' is a member of the constant buffer '" + (v.Buffer?.Name ?? "") + "', which shaders cannot write. Copy it into a local variable first.";
                    case VariableSymbol { Kind: VarKind.Resource } v:
                        return "'" + v.Name + "' is a resource, not a variable.";
                    case VariableSymbol v when v.IsConst:
                        return "'" + v.Name + "' is declared const.";
                    case VariableSymbol:
                        return "";
                    case EnumeratorSymbol en:
                        return "'" + en.Name + "' is an enumerator constant.";
                    default:
                        return e.Type is { IsError: true } ? "" : "the left side is not a variable.";
                }

            case MemberAccessExpression m:
            {
                if (m.SwizzleComponents is { } sw)
                {
                    var seen = new HashSet<int>();
                    foreach (int c in sw)
                    {
                        if (!seen.Add(c))
                        {
                            return "a swizzle that repeats a component ('" + m.Member.Text + "') cannot be assigned.";
                        }
                    }
                }

                if (m.Target.Type is ResourceType { ResourceKind: ResourceTypeKind.ConstantBuffer })
                {
                    return "'" + m.Member.Text + "' is a member of a constant buffer, which shaders cannot write. Copy it into a local variable first.";
                }

                return WriteBlocker(m.Target);
            }

            case IndexExpression ix:
            {
                var bt = ix.Target.Type;
                if (bt is ResourceType rt)
                {
                    return rt.ResourceKind is ResourceTypeKind.RWStructuredBuffer or ResourceTypeKind.RWTexture1D or ResourceTypeKind.RWTexture1DArray
                        or ResourceTypeKind.RWTexture2D or ResourceTypeKind.RWTexture2DArray or ResourceTypeKind.RWTexture3D or ResourceTypeKind.RWBuffer
                        ? ""
                        : "'" + rt.ResourceKind + "' is read-only; declare it as RW" + rt.ResourceKind + " to write to it.";
                }

                if (bt is ArrayType { Element: ResourceType })
                {
                    return "resource arrays cannot be assigned.";
                }

                var indexed = ix.Target;
                while (indexed is ParenthesizedExpression paren)
                {
                    indexed = paren.Inner;
                }

                if (indexed is MemberAccessExpression { SwizzleComponents.Count: > 1 })
                {
                    return "an element of a multi-component swizzle cannot be assigned. Index the vector itself, for example v[0] = ..., or assign the swizzle whole.";
                }

                return WriteBlocker(ix.Target);
            }

            default:
                return e.Type is { IsError: true } ? "" : "the left side of an assignment must be a variable, member or element.";
        }
    }

    private HlslType CheckAssignment(AssignmentExpression a)
    {
        var tt = CheckExpr(a.Target);
        var vt = a.Value is InitializerListExpression ? null : CheckExpr(a.Value);
        var info = InfoOf(a);
        if (vt is null)
        {
            Err(SemanticCodes.TypeMismatch, "An initializer list cannot be assigned; build the value with a constructor such as float3(...).", a.Value.Span);
            return tt;
        }

        if (tt.IsError || vt.IsError)
        {
            return tt;
        }

        string why = WriteBlocker(a.Target);
        if (why.Length > 0)
        {
            Err(SemanticCodes.NotAssignable, "Cannot assign here: " + why, a.Target.Span);
            return tt;
        }

        if (a.Operator is { } op)
        {
            var ls = tt.ScalarElement;
            var rs = vt.ScalarElement;
            if (ls is null || rs is null)
            {
                Err(SemanticCodes.BadOperator, "The operator '" + OperatorText(op) + "=' does not apply to '" + tt + "' and '" + vt + "'.", a.Span);
                return tt;
            }

            var shape = CombineShape(tt, vt, a.Span, true);
            if (shape is null)
            {
                Err(SemanticCodes.BadOperator, "The operator '" + OperatorText(op) + "=' cannot combine '" + tt + "' and '" + vt + "': the sizes do not match.", a.Span);
                return tt;
            }

            bool shift = op is BinaryOperator.ShiftLeft or BinaryOperator.ShiftRight;
            bool bitwise = op is BinaryOperator.BitwiseAnd or BinaryOperator.BitwiseOr or BinaryOperator.BitwiseXor or BinaryOperator.ShiftLeft or BinaryOperator.ShiftRight or BinaryOperator.Modulo && !ls.IsFloatingPoint;
            if ((shift || op is BinaryOperator.BitwiseAnd or BinaryOperator.BitwiseOr or BinaryOperator.BitwiseXor) && (ls.IsFloatingPoint || rs.IsFloatingPoint))
            {
                Err(SemanticCodes.BadOperator, "The operator '" + OperatorText(op) + "=' needs integer operands, not '" + tt + "' and '" + vt + "'.", a.Span);
                return tt;
            }

            _ = bitwise;
            HlslType operand;
            if (shift)
            {
                operand = TypeOps.Reshape(shape, TypeOps.ArithmeticScalar(ls));
            }
            else
            {
                operand = TypeOps.Reshape(shape, TypeOps.Common(TypeOps.ArithmeticScalar(ls), TypeOps.ArithmeticScalar(rs)));
            }

            info.OperandType = operand;
            if (TypeOps.Classify(operand, tt) == TypeOps.Conv.None)
            {
                Err(SemanticCodes.TypeMismatch, "Cannot store the result of type '" + operand + "' in '" + tt + "'.", a.Span);
            }

            return tt;
        }

        CheckConvertible(a.Value, tt);
        return tt;
    }

    private HlslType CheckConditional(ConditionalExpression c)
    {
        var ct = CheckExpr(c.Condition);
        var at = CheckExpr(c.WhenTrue);
        var bt = CheckExpr(c.WhenFalse);
        var info = InfoOf(c);
        if (ct.IsError || at.IsError || bt.IsError)
        {
            return HlslType.Error;
        }

        if (ct.ScalarElement is null || ct is MatrixType)
        {
            Err(SemanticCodes.TypeMismatch, "The condition of '?:' must be a scalar or a vector, not '" + ct + "'.", c.Condition.Span);
            return HlslType.Error;
        }

        HlslType result;
        if (at.Equals(bt))
        {
            result = at;
        }
        else if (at.ScalarElement is { } sa && bt.ScalarElement is { } sb)
        {
            var shape = CombineShape(at, bt, c.Span, true);
            if (shape is null)
            {
                Err(SemanticCodes.TypeMismatch, "The two results of '?:' have incompatible types '" + at + "' and '" + bt + "'.", c.Span);
                return HlslType.Error;
            }

            result = TypeOps.Reshape(shape, TypeOps.Common(TypeOps.ArithmeticScalar(sa), TypeOps.ArithmeticScalar(sb)));
            if (sa.ScalarKind == ScalarKind.Bool && sb.ScalarKind == ScalarKind.Bool)
            {
                result = TypeOps.Reshape(shape, ScalarType.Bool);
            }
        }
        else
        {
            Err(SemanticCodes.TypeMismatch, "The two results of '?:' have incompatible types '" + at + "' and '" + bt + "'.", c.Span);
            return HlslType.Error;
        }

        if (ct is VectorType cv && !(result is VectorType rv && rv.Count == cv.Count))
        {
            Err(SemanticCodes.TypeMismatch, "A vector condition needs vector results of the same size, but the results are '" + result + "'.", c.Span);
            return HlslType.Error;
        }

        info.OperandType = result;
        if (IsConstScalar(c.Condition, out var cc) && IsConstScalar(c.WhenTrue, out var ca) && IsConstScalar(c.WhenFalse, out var cb) && result is ScalarType rs)
        {
            info.Const = (cc.IsZero ? cb : ca).ConvertTo(rs);
        }

        return result;
    }

    // ------------------------------------------------------------ casts

    private HlslType CheckCast(CastExpression c)
    {
        var target = ResolveType(c.TargetType);
        var ot = CheckExpr(c.Operand);
        var info = InfoOf(c);
        info.TargetType = target;
        if (target.IsError || ot.IsError)
        {
            return target;
        }

        if (ot is DescriptorHeapType heapOperand)
        {
            return ConvertHeapElement(c.Operand, heapOperand, target) ? target : HlslType.Error;
        }

        if (!CanCast(ot, target))
        {
            Err(SemanticCodes.TypeMismatch, "Cannot convert '" + ot + "' to '" + target + "' with a cast.", c.Span);
            return HlslType.Error;
        }

        if (target is ScalarType ts && IsConstScalar(c.Operand, out var cv))
        {
            info.Const = cv.ConvertTo(ts);
        }

        return target;
    }

    private static bool CanCast(HlslType from, HlslType to)
    {
        if (from.Equals(to))
        {
            return true;
        }

        var fs = from.ScalarElement;
        var ts = to.ScalarElement;
        if (fs is null || ts is null)
        {
            return from is ResourceType { ResourceKind: ResourceTypeKind.ConstantBuffer, Element: { } el } && el.Equals(to);
        }

        return (from, to) switch
        {
            (ScalarType, _) => true,
            (VectorType fv, ScalarType) => fv.Count >= 1,
            (VectorType fv, VectorType tv) => tv.Count <= fv.Count,
            (MatrixType, ScalarType) => true,
            (MatrixType fm, MatrixType tm) => tm.Rows <= fm.Rows && tm.Columns <= fm.Columns,
            (MatrixType fm, VectorType tv) => tv.Count <= fm.Rows * fm.Columns,
            (VectorType fv, MatrixType tm) => fv.Count == tm.Rows * tm.Columns,
            _ => false,
        };
    }

    // ------------------------------------------------------------ members

    private static int ComponentIndex(char c, out int set)
    {
        set = 0;
        switch (c)
        {
            case 'x': return 0;
            case 'y': return 1;
            case 'z': return 2;
            case 'w': return 3;
            case 'r': set = 1; return 0;
            case 'g': set = 1; return 1;
            case 'b': set = 1; return 2;
            case 'a': set = 1; return 3;
            default: return -1;
        }
    }

    private HlslType CheckMember(MemberAccessExpression m)
    {
        var tt = CheckExpr(m.Target);
        var info = InfoOf(m);
        string name = m.Member.Text;
        if (tt.IsError)
        {
            return tt;
        }

        if (tt is DescriptorHeapType heapTarget)
        {
            Err(SemanticCodes.BadDescriptorHeap, "'" + name + "' cannot be used on " + heapTarget + " directly: it has no type until it is assigned. " + (heapTarget.IsElement ? HeapUsageHint(heapTarget) : "Index the heap first, for example ResourceDescriptorHeap[i]."), m.Span);
            return HlslType.Error;
        }

        switch (tt)
        {
            case StructType st:
            {
                // The index map can name a field the struct does not hold yet while a template instance or an erroneous struct is still being completed.
                if (_structInfos.TryGetValue(st, out var si) && si.FieldIndex.TryGetValue(name, out int idx) && idx < st.Fields.Count)
                {
                    info.FieldIndex = idx;
                    info.IsLValue = m.Target.Info?.IsLValue ?? false;
                    return st.Fields[idx].Type;
                }

                if (FindField(st, name, out var holder, out _) && !ReferenceEquals(holder, st))
                {
                    // An inherited member: reach it through the base sub-object.
                    var via = BuildBaseAccess(m.Target, st, holder, m.Span);
                    var re = new MemberAccessExpression(m.Span, via, m.Member);
                    var reType = CheckExpr(re);
                    info.Rewritten = re;
                    info.IsLValue = re.Info?.IsLValue ?? false;
                    return reType;
                }

                if (FindMethodGroup(st, name, out _) is not null)
                {
                    Err(SemanticCodes.BadMember, "'" + name + "' is a member function of '" + st.Name + "'; call it with parentheses: " + name + "(...).", m.Member.Span);
                    return HlslType.Error;
                }

                string? hint = Suggest(name, FieldNames(st));
                Err(SemanticCodes.BadMember, "The struct '" + st.Name + "' has no member '" + name + "'." + (hint is null ? "" : " Did you mean '" + hint + "'?"), m.Member.Span);
                return HlslType.Error;
            }

            case ResourceType { ResourceKind: ResourceTypeKind.ConstantBuffer, Element: StructType cst }:
            {
                if (_structInfos.TryGetValue(cst, out var si) && si.FieldIndex.TryGetValue(name, out int idx) && idx < cst.Fields.Count)
                {
                    info.FieldIndex = idx;
                    info.IsLValue = true;
                    return cst.Fields[idx].Type;
                }

                Err(SemanticCodes.BadMember, "The constant buffer type '" + cst.Name + "' has no member '" + name + "'.", m.Member.Span);
                return HlslType.Error;
            }

            case ScalarType or VectorType:
                return CheckSwizzle(m, tt, info);
            case MatrixType mt:
                return CheckMatrixSwizzle(m, mt, info);
            case ResourceType or SamplerType:
                Err(SemanticCodes.BadMember, "'" + name + "' is a method of '" + tt + "' and must be called: write " + name + "(...).", m.Member.Span);
                return HlslType.Error;
            default:
                Err(SemanticCodes.BadMember, "The type '" + tt + "' has no member '" + name + "'.", m.Member.Span);
                return HlslType.Error;
        }
    }

    private static IEnumerable<string> FieldNames(StructType st)
    {
        foreach (var f in st.Fields)
        {
            yield return f.Name;
        }
    }

    private HlslType CheckSwizzle(MemberAccessExpression m, HlslType tt, ExprInfo info)
    {
        string name = m.Member.Text;
        int count = TypeOps.Count(tt);
        if (name.Length is < 1 or > 4)
        {
            Err(SemanticCodes.BadMember, "'" + name + "' is not a valid swizzle of '" + tt + "': use 1 to 4 of x, y, z, w (or r, g, b, a).", m.Member.Span);
            return HlslType.Error;
        }

        var comps = new List<int>(4);
        int firstSet = -1;
        foreach (char ch in name)
        {
            int idx = ComponentIndex(ch, out int set);
            if (idx < 0 || (firstSet >= 0 && set != firstSet))
            {
                Err(SemanticCodes.BadMember, "'" + name + "' is not a valid swizzle of '" + tt + "': use x, y, z, w or r, g, b, a, and do not mix the two sets.", m.Member.Span);
                return HlslType.Error;
            }

            firstSet = set;
            if (idx >= count)
            {
                Err(SemanticCodes.BadMember, "'" + ch + "' is not a component of '" + tt + "', which has " + Num(count) + " component" + (count == 1 ? "" : "s") + ".", m.Member.Span);
                return HlslType.Error;
            }

            comps.Add(idx);
        }

        m.SwizzleComponents = comps;
        info.IsLValue = m.Target.Info?.IsLValue ?? false;
        var elem = tt.ScalarElement!;
        return comps.Count == 1 ? elem : HlslType.Vector(elem, comps.Count);
    }

    private HlslType CheckMatrixSwizzle(MemberAccessExpression m, MatrixType mt, ExprInfo info)
    {
        string name = m.Member.Text;
        var comps = new List<int>(4);
        int i = 0;
        bool ok = name.Length >= 3;
        while (ok && i < name.Length)
        {
            if (name[i] != '_')
            {
                ok = false;
                break;
            }

            i++;
            int baseOne = 0;
            if (i < name.Length && name[i] == 'm')
            {
                i++;
            }
            else
            {
                baseOne = 1;
            }

            if (i + 1 >= name.Length + 0 && i + 2 > name.Length)
            {
                ok = false;
                break;
            }

            int r = name[i] - '0' - baseOne;
            int c = name[i + 1] - '0' - baseOne;
            i += 2;
            if (r < 0 || c < 0 || r >= mt.Rows || c >= mt.Columns)
            {
                Err(SemanticCodes.BadMember, "'" + name + "' selects an element outside the " + mt + " matrix.", m.Member.Span);
                return HlslType.Error;
            }

            comps.Add(r * 4 + c);
            if (comps.Count > 4)
            {
                ok = false;
            }
        }

        if (!ok || comps.Count == 0)
        {
            Err(SemanticCodes.BadMember, "'" + name + "' is not a member of '" + mt + "'. Select elements with _m00 or _11 style names.", m.Member.Span);
            return HlslType.Error;
        }

        m.SwizzleComponents = comps;
        info.IsLValue = m.Target.Info?.IsLValue ?? false;
        return comps.Count == 1 ? mt.Element : HlslType.Vector(mt.Element, comps.Count);
    }

    // ------------------------------------------------------------ indexing

    private HlslType CheckIndex(IndexExpression ix)
    {
        var tt = CheckExpr(ix.Target);
        var it = CheckExpr(ix.Index);
        var info = InfoOf(ix);
        if (tt.IsError || it.IsError)
        {
            return HlslType.Error;
        }

        if (tt is DescriptorHeapType heap)
        {
            return CheckHeapIndex(ix, heap, it);
        }

        if (tt is ResourceType rt)
        {
            return CheckResourceIndex(ix, rt, it, info);
        }

        if (tt is StructType indexedStruct)
        {
            return CheckStructIndex(ix, indexedStruct, it, info);
        }

        var elem = TypeOps.IndexedElement(tt);
        if (elem is null)
        {
            Err(SemanticCodes.BadOperator, "The type '" + tt + "' cannot be indexed with [].", ix.Span);
            return HlslType.Error;
        }

        if (it is not ScalarType { IsFloatingPoint: false } && !(it is ScalarType))
        {
            Err(SemanticCodes.TypeMismatch, "An index must be an integer, not '" + it + "'.", ix.Index.Span);
            return HlslType.Error;
        }

        info.IsLValue = ix.Target.Info?.IsLValue ?? false;
        return elem;
    }

    private HlslType CheckResourceIndex(IndexExpression ix, ResourceType rt, HlslType it, ExprInfo info)
    {
        int dims;
        switch (rt.ResourceKind)
        {
            case ResourceTypeKind.StructuredBuffer or ResourceTypeKind.RWStructuredBuffer or ResourceTypeKind.Buffer or ResourceTypeKind.RWBuffer:
                dims = 0;
                break;
            case ResourceTypeKind.Texture1D or ResourceTypeKind.RWTexture1D:
                dims = 1;
                break;
            case ResourceTypeKind.Texture2D or ResourceTypeKind.RWTexture2D or ResourceTypeKind.Texture1DArray or ResourceTypeKind.RWTexture1DArray:
                dims = 2;
                break;
            case ResourceTypeKind.Texture3D or ResourceTypeKind.RWTexture3D or ResourceTypeKind.Texture2DArray or ResourceTypeKind.RWTexture2DArray:
                dims = 3;
                break;
            default:
                Err(SemanticCodes.BadOperator, "'" + rt + "' cannot be indexed with []; use its Load method.", ix.Span);
                return HlslType.Error;
        }

        if (rt.Element is null)
        {
            return HlslType.Error;
        }

        int have = it is VectorType v ? v.Count : it is ScalarType ? 1 : 0;
        bool intLike = it.ScalarElement is { IsInteger: true };
        if (dims == 0 ? !(it is ScalarType && intLike) : !(have == dims && intLike))
        {
            string expected = dims <= 1 ? "an integer" : "an integer vector such as uint" + Num(dims);
            Err(SemanticCodes.TypeMismatch, "Indexing '" + rt + "' needs " + expected + " but the index is '" + it + "'.", ix.Index.Span);
            return HlslType.Error;
        }

        info.IsLValue = rt.IsReadWrite;
        return rt.Element;
    }

    // ------------------------------------------------------------ initializers

    /// <summary>Checks the initializer of a variable of <paramref name="target"/> type.</summary>
    public void CheckInitializer(Expression init, HlslType target, bool requireConstant = false)
    {
        _ = requireConstant;
        if (init is InitializerListExpression il)
        {
            CheckInitializerList(il, target);
            return;
        }

        var t = CheckExpr(init);
        if (!t.IsError && !target.IsError)
        {
            CheckConvertible(init, target);
        }
    }
}
