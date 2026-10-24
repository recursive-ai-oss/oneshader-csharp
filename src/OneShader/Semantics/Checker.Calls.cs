using System;
using System.Collections.Generic;
using OneShader.Diagnostics;
using OneShader.IR;
using OneShader.Syntax;
using OneShader.Types;

namespace OneShader.Semantics;

internal enum BuiltinKind
{
    /// <summary>A <see cref="Opcode.CallIntrinsic"/> over converted operands.</summary>
    Intrinsic,

    /// <summary>An IR arithmetic opcode (fmod is Rem).</summary>
    Op,

    /// <summary>HLSL <c>mul</c>.</summary>
    Mul,

    /// <summary><c>transpose</c>.</summary>
    Transpose,

    /// <summary><c>mad</c>: fused multiply-add.</summary>
    Mad,

    /// <summary><c>sign</c>, which returns int.</summary>
    Sign,

    /// <summary><c>asfloat</c>, <c>asint</c>, <c>asuint</c>.</summary>
    AsType,

    /// <summary><c>clip</c>.</summary>
    Clip,

    /// <summary>A texture sampling or load method.</summary>
    Texture,

    /// <summary><c>GetDimensions</c>.</summary>
    GetDimensions,

    /// <summary><c>select(c, a, b)</c>.</summary>
    Select,

    /// <summary>Structured buffer <c>Load</c>.</summary>
    BufferLoad,
}

internal sealed class BuiltinCall
{
    public BuiltinKind Kind { get; init; }

    public Intrinsic Ir { get; init; }

    public Opcode Op { get; init; }

    public Opcode TextureOp { get; init; }

    public string Name { get; init; } = "";

    public HlslType[] ParamTypes { get; init; } = [];

    public HlslType Result { get; init; } = HlslType.Error;

    /// <summary>Gather component, or the coordinate dimension for texture methods.</summary>
    public int Extra { get; init; }

    /// <summary>For texture methods: whether an offset argument is present, and the index of the first optional argument.</summary>
    public bool HasOffset { get; init; }
}

internal sealed partial class Checker
{
    private enum Cat
    {
        Float,
        FloatToBool,
        NumericKeep,
    }

    private static readonly Dictionary<string, (Intrinsic Ir, int Arity, Cat Cat)> FloatTable = BuildFloatTable();

    private static Dictionary<string, (Intrinsic, int, Cat)> BuildFloatTable()
    {
        var d = new Dictionary<string, (Intrinsic, int, Cat)>(StringComparer.Ordinal);
        void F1(string n, Intrinsic i) => d[n] = (i, 1, Cat.Float);
        F1("acos", Intrinsic.Acos); F1("asin", Intrinsic.Asin); F1("atan", Intrinsic.Atan); F1("ceil", Intrinsic.Ceil);
        F1("cos", Intrinsic.Cos); F1("cosh", Intrinsic.Cosh); F1("degrees", Intrinsic.Degrees); F1("exp", Intrinsic.Exp);
        F1("exp2", Intrinsic.Exp2); F1("floor", Intrinsic.Floor); F1("frac", Intrinsic.Frac); F1("log", Intrinsic.Log);
        F1("log2", Intrinsic.Log2); F1("radians", Intrinsic.Radians); F1("round", Intrinsic.Round); F1("rsqrt", Intrinsic.Rsqrt);
        F1("saturate", Intrinsic.Saturate); F1("sin", Intrinsic.Sin); F1("sinh", Intrinsic.Sinh); F1("sqrt", Intrinsic.Sqrt);
        F1("tan", Intrinsic.Tan); F1("tanh", Intrinsic.Tanh); F1("trunc", Intrinsic.Trunc); F1("rcp", Intrinsic.Rcp);
        F1("ddx", Intrinsic.Ddx); F1("ddy", Intrinsic.Ddy); F1("ddx_coarse", Intrinsic.DdxCoarse); F1("ddy_coarse", Intrinsic.DdyCoarse);
        F1("ddx_fine", Intrinsic.DdxFine); F1("ddy_fine", Intrinsic.DdyFine); F1("fwidth", Intrinsic.Fwidth);
        d["isnan"] = (Intrinsic.IsNan, 1, Cat.FloatToBool);
        d["isinf"] = (Intrinsic.IsInf, 1, Cat.FloatToBool);
        d["isfinite"] = (Intrinsic.IsFinite, 1, Cat.FloatToBool);
        d["atan2"] = (Intrinsic.Atan2, 2, Cat.Float);
        d["pow"] = (Intrinsic.Pow, 2, Cat.Float);
        d["step"] = (Intrinsic.Step, 2, Cat.Float);
        d["ldexp"] = (Intrinsic.Ldexp, 2, Cat.Float);
        d["lerp"] = (Intrinsic.Lerp, 3, Cat.Float);
        d["smoothstep"] = (Intrinsic.SmoothStep, 3, Cat.Float);
        d["fma"] = (Intrinsic.Fma, 3, Cat.Float);
        d["abs"] = (Intrinsic.Abs, 1, Cat.NumericKeep);
        d["min"] = (Intrinsic.Min, 2, Cat.NumericKeep);
        d["max"] = (Intrinsic.Max, 2, Cat.NumericKeep);
        d["clamp"] = (Intrinsic.Clamp, 3, Cat.NumericKeep);
        return d;
    }

    private static readonly string[] OtherBuiltins =
    [
        "dot", "length", "distance", "normalize", "cross", "reflect", "refract", "faceforward", "all", "any", "determinant",
        "mul", "transpose", "mad", "sign", "fmod", "asfloat", "asint", "asuint", "clip", "select", "sincos", "modf", "frexp",
        "countbits", "reversebits", "firstbithigh", "firstbitlow", "f16tof32", "f32tof16",
    ];

    private static IEnumerable<string> BuiltinNames()
    {
        foreach (var k in FloatTable.Keys)
        {
            yield return k;
        }

        foreach (var k in OtherBuiltins)
        {
            yield return k;
        }
    }

    private HlslType[] CheckArgs(CallExpression c)
    {
        var at = new HlslType[c.Arguments.Count];
        for (int i = 0; i < at.Length; i++)
        {
            var a = c.Arguments[i];
            if (a is InitializerListExpression il)
            {
                Err(SemanticCodes.TypeMismatch, "An initializer list cannot be passed as an argument; build the value with a constructor such as float3(...).", il.Span);
                at[i] = HlslType.Error;
                continue;
            }

            at[i] = CheckExpr(a);
        }

        return at;
    }

    private static bool AnyError(HlslType[] types)
    {
        foreach (var t in types)
        {
            if (t.IsError)
            {
                return true;
            }
        }

        return false;
    }

    private HlslType CheckCall(CallExpression c)
    {
        var info = InfoOf(c);
        if (c.Callee is MemberAccessExpression ma)
        {
            return CheckMethodCall(c, ma, info);
        }

        if (c.Callee is not NameExpression ne)
        {
            CheckExpr(c.Callee);
            CheckArgs(c);
            Err(SemanticCodes.BadOperator, "This expression is not a function and cannot be called.", c.Callee.Span);
            return HlslType.Error;
        }

        if (c.TemplateArguments.Count > 0)
        {
            CheckArgs(c);
            Err(SemanticCodes.Unsupported, "Calling templates with explicit template arguments is not supported yet.", c.Span);
            return HlslType.Error;
        }

        var sym = LookupQualified(ne.Name, out _);
        var at = CheckArgs(c);
        string name = ne.Name.Last.Text;
        switch (sym)
        {
            case FunctionGroup g:
                return CallUser(c, ne, g, at, info);
            case TypeSymbol ts:
                return CheckConstructCore(c.Span, ts.Type is EnumType en ? en.Underlying : ts.Type, c.Arguments, at, info);
            case null when ne.Name.Parts.Count == 1:
                if (TryBuiltin(c, name, at, info, out var result))
                {
                    return result;
                }

                string? hint = Suggest(name, BuiltinNames().Concat2(VisibleValueNames()));
                Err(SemanticCodes.Undeclared, "The function '" + name + "' is not declared." + (hint is null ? " Declare it before this call, or check the spelling." : " Did you mean '" + hint + "'?"), ne.Name.Span);
                return HlslType.Error;
            case null:
                ReportUndeclared(ne.Name);
                return HlslType.Error;
            default:
                Err(SemanticCodes.BadOperator, "'" + ne.Name + "' is a " + Describe(sym) + ", not a function.", ne.Name.Span);
                return HlslType.Error;
        }
    }

    // ------------------------------------------------------------ user functions

    private HlslType CallUser(CallExpression c, NameExpression ne, FunctionGroup g, HlslType[] at, ExprInfo info)
    {
        string name = ne.Name.Last.Text;
        if (AnyError(at))
        {
            return OverloadReturn(g);
        }

        var candidates = new List<FunctionSymbol>();
        foreach (var f in g.Overloads)
        {
            int required = 0;
            foreach (var p in f.Params)
            {
                if (p.Default is null)
                {
                    required++;
                }
            }

            if (at.Length >= required && at.Length <= f.Params.Count)
            {
                candidates.Add(f);
            }
        }

        if (candidates.Count == 0)
        {
            var counts = new List<string>();
            foreach (var f in g.Overloads)
            {
                counts.Add(Num(f.Params.Count));
            }

            string expected = g.Overloads.Count == 1
                ? "expects " + Num(g.Overloads[0].Params.Count) + " argument" + (g.Overloads[0].Params.Count == 1 ? "" : "s")
                : "takes " + string.Join(" or ", counts) + " arguments";
            Err(SemanticCodes.ArgumentCount, "The function '" + name + "' " + expected + " but the call passes " + Num(at.Length) + ".", c.Span);
            return OverloadReturn(g);
        }

        FunctionSymbol? best = null;
        long bestCost = long.MaxValue;
        bool tie = false;
        foreach (var f in candidates)
        {
            long cost = 0;
            for (int i = 0; i < at.Length && cost != long.MaxValue; i++)
            {
                var p = f.Params[i];
                int pc = p.IsOut && !p.IsIn
                    ? TypeOps.Cost(p.Type, at[i])
                    : TypeOps.Cost(at[i], p.Type);
                if (p.IsOut && p.IsIn && TypeOps.Cost(p.Type, at[i]) == int.MaxValue)
                {
                    pc = int.MaxValue;
                }

                if (pc == int.MaxValue)
                {
                    cost = long.MaxValue;
                }
                else
                {
                    cost += pc;
                }
            }

            if (cost == long.MaxValue)
            {
                continue;
            }

            if (cost < bestCost)
            {
                best = f;
                bestCost = cost;
                tie = false;
            }
            else if (cost == bestCost)
            {
                tie = true;
            }
        }

        if (best is null)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("No overload of '").Append(name).Append("' accepts (");
            for (int i = 0; i < at.Length; i++)
            {
                sb.Append(i > 0 ? ", " : "").Append(at[i]);
            }

            sb.Append("). Candidates: ");
            for (int i = 0; i < g.Overloads.Count; i++)
            {
                sb.Append(i > 0 ? "; " : "").Append(Signature(g.Overloads[i]));
            }

            sb.Append('.');
            Err(SemanticCodes.NoOverload, sb.ToString(), c.Span);
            return OverloadReturn(g);
        }

        if (tie)
        {
            Err(SemanticCodes.AmbiguousCall, "The call to '" + name + "' is ambiguous: more than one overload converts the arguments equally well. Cast an argument to the exact parameter type.", c.Span);
        }

        var target = best.Definition ?? best;
        var ptypes = new HlslType[at.Length];
        for (int i = 0; i < at.Length; i++)
        {
            var p = best.Params[i];
            ptypes[i] = p.Type;
            if (p.IsOut)
            {
                string why = WriteBlocker(c.Arguments[i]);
                if (why.Length > 0)
                {
                    Err(SemanticCodes.NotAssignable, "Argument " + Num(i + 1) + " of '" + name + "' is an " + ((p.Mods & Modifiers.In) != 0 ? "inout" : "out") + " parameter and must be a variable: " + why, c.Arguments[i].Span);
                }
            }

            if (p.IsIn && !(p.IsOut && !p.IsIn))
            {
                CheckConvertible(c.Arguments[i], p.Type);
            }
        }

        if (target.InProgress || ReferenceEquals(target, _fn))
        {
            Err(SemanticCodes.BadFunction, "The function '" + name + "' calls itself; HLSL does not allow recursion. Rewrite it as a loop.", c.Span);
        }

        info.Function = best;
        info.ParamTypes = ptypes;
        best.Called = true;
        _fn?.Callees.Add(best);
        return best.ReturnType;
    }

    private static HlslType OverloadReturn(FunctionGroup g) => HlslType.Error;

    private static string Signature(FunctionSymbol f)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(f.ReturnType).Append(' ').Append(f.Name).Append('(');
        for (int i = 0; i < f.Params.Count; i++)
        {
            sb.Append(i > 0 ? ", " : "").Append(f.Params[i].Type);
        }

        return sb.Append(')').ToString();
    }

    // ------------------------------------------------------------ constructors

    private HlslType CheckConstruct(ConstructExpression ce)
    {
        var target = ResolveType(ce.ConstructedType);
        var at = new HlslType[ce.Arguments.Count];
        for (int i = 0; i < at.Length; i++)
        {
            at[i] = ce.Arguments[i] is InitializerListExpression ? HlslType.Error : CheckExpr(ce.Arguments[i]);
        }

        return CheckConstructCore(ce.Span, target, ce.Arguments, at, InfoOf(ce));
    }

    private HlslType CheckConstructCore(SourceSpan span, HlslType target, IReadOnlyList<Expression> args, HlslType[] at, ExprInfo info)
    {
        info.TargetType = target;
        if (target.IsError || AnyError(at))
        {
            return target;
        }

        if (target is StructType st)
        {
            if (args.Count != st.Fields.Count)
            {
                Err(SemanticCodes.ArgumentCount, "The struct '" + st.Name + "' has " + Num(st.Fields.Count) + " members but " + Num(args.Count) + " values were given.", span);
                return HlslType.Error;
            }

            for (int i = 0; i < args.Count; i++)
            {
                CheckConvertible(args[i], st.Fields[i].Type);
            }

            return target;
        }

        if (target.ScalarElement is null)
        {
            Err(SemanticCodes.BadType, "Values of type '" + target + "' cannot be constructed with " + target + "(...).", span);
            return HlslType.Error;
        }

        int need = TypeOps.Count(target);
        int have = 0;
        for (int i = 0; i < at.Length; i++)
        {
            if (at[i].ScalarElement is null || at[i] is StructType)
            {
                Err(SemanticCodes.TypeMismatch, "Cannot use a value of type '" + at[i] + "' to construct '" + target + "'.", args[i].Span);
                return HlslType.Error;
            }

            have += TypeOps.Count(at[i]);
        }

        if (at.Length == 0)
        {
            Err(SemanticCodes.ArgumentCount, "'" + target + "' needs " + Num(need) + " value" + (need == 1 ? "" : "s") + " but none were given.", span);
            return HlslType.Error;
        }

        bool splat = at.Length == 1 && have == 1;
        bool conversion = at.Length == 1 && have >= need;
        if (!splat && !conversion && have != need)
        {
            Err(SemanticCodes.ArgumentCount, "'" + target + "' needs " + Num(need) + " value" + (need == 1 ? "" : "s") + " but the arguments supply " + Num(have) + ".", span);
            return HlslType.Error;
        }

        if (target is ScalarType ts && args.Count == 1 && IsConstScalar(args[0], out var cv))
        {
            info.Const = cv.ConvertTo(ts);
        }

        return target;
    }

    // ------------------------------------------------------------ built-in functions

    /// <summary>Combines argument types component-wise: the common shape and scalar type, or null after reporting.</summary>
    private HlslType? Unify(CallExpression c, string name, HlslType[] at, bool floatOnly, int[]? indexes = null)
    {
        HlslType? shape = null;
        ScalarType? scalar = null;
        int n = indexes?.Length ?? at.Length;
        for (int k = 0; k < n; k++)
        {
            int i = indexes?[k] ?? k;
            var s = at[i].ScalarElement;
            if (s is null)
            {
                Err(SemanticCodes.TypeMismatch, "Argument " + Num(i + 1) + " of '" + name + "' must be a number, vector or matrix, not '" + at[i] + "'.", c.Arguments[i].Span);
                return null;
            }

            s = TypeOps.ArithmeticScalar(s);
            shape = shape is null ? at[i] : CombineShape(shape, at[i], c.Arguments[i].Span, true);
            if (shape is null)
            {
                Err(SemanticCodes.TypeMismatch, "The arguments of '" + name + "' have sizes that do not match.", c.Span);
                return null;
            }

            scalar = scalar is null ? s : TypeOps.Common(scalar, s);
        }

        if (floatOnly && !scalar!.IsFloatingPoint)
        {
            scalar = ScalarType.Float;
        }

        return TypeOps.Reshape(shape!, scalar!);
    }

    private static HlslType[] Fill(HlslType t, int n)
    {
        var a = new HlslType[n];
        Array.Fill(a, t);
        return a;
    }

    private bool ArityOk(CallExpression c, string name, int expected)
    {
        if (c.Arguments.Count == expected)
        {
            return true;
        }

        Err(SemanticCodes.ArgumentCount, "The function '" + name + "' expects " + Num(expected) + " argument" + (expected == 1 ? "" : "s") + " but the call passes " + Num(c.Arguments.Count) + ".", c.Span);
        return false;
    }

    private bool TryBuiltin(CallExpression c, string name, HlslType[] at, ExprInfo info, out HlslType result)
    {
        result = HlslType.Error;
        if (FloatTable.TryGetValue(name, out var entry))
        {
            if (!ArityOk(c, name, entry.Arity) || AnyError(at))
            {
                return true;
            }

            var u = Unify(c, name, at, entry.Cat != Cat.NumericKeep);
            if (u is null)
            {
                return true;
            }

            info.Builtin = new BuiltinCall { Kind = BuiltinKind.Intrinsic, Ir = entry.Ir, Name = name, ParamTypes = Fill(u, entry.Arity), Result = entry.Cat == Cat.FloatToBool ? TypeOps.Reshape(u, ScalarType.Bool) : u };
            result = info.Builtin.Result;
            return true;
        }

        switch (name)
        {
            case "fmod":
            {
                if (!ArityOk(c, name, 2) || AnyError(at))
                {
                    return true;
                }

                var u = Unify(c, name, at, true);
                if (u is null)
                {
                    return true;
                }

                info.Builtin = new BuiltinCall { Kind = BuiltinKind.Op, Op = Opcode.Rem, Name = name, ParamTypes = Fill(u, 2), Result = u };
                result = u;
                return true;
            }

            case "mad":
            {
                if (!ArityOk(c, name, 3) || AnyError(at))
                {
                    return true;
                }

                var u = Unify(c, name, at, false);
                if (u is null)
                {
                    return true;
                }

                info.Builtin = new BuiltinCall { Kind = BuiltinKind.Mad, Name = name, ParamTypes = Fill(u, 3), Result = u };
                result = u;
                return true;
            }

            case "sign":
            {
                if (!ArityOk(c, name, 1) || AnyError(at))
                {
                    return true;
                }

                var u = Unify(c, name, at, false);
                if (u is null)
                {
                    return true;
                }

                info.Builtin = new BuiltinCall { Kind = BuiltinKind.Sign, Ir = Intrinsic.Sign, Name = name, ParamTypes = [u], Result = TypeOps.Reshape(u, ScalarType.Int) };
                result = info.Builtin.Result;
                return true;
            }

            case "dot" or "distance":
            {
                if (!ArityOk(c, name, 2) || AnyError(at))
                {
                    return true;
                }

                var u = Unify(c, name, at, name == "distance");
                if (u is null)
                {
                    return true;
                }

                if (u is MatrixType)
                {
                    Err(SemanticCodes.TypeMismatch, "'" + name + "' needs vectors or scalars, not matrices.", c.Span);
                    return true;
                }

                info.Builtin = new BuiltinCall { Kind = BuiltinKind.Intrinsic, Ir = name == "dot" ? Intrinsic.Dot : Intrinsic.Distance, Name = name, ParamTypes = Fill(u, 2), Result = u.ScalarElement! };
                result = info.Builtin.Result;
                return true;
            }

            case "length" or "normalize":
            {
                if (!ArityOk(c, name, 1) || AnyError(at))
                {
                    return true;
                }

                var u = Unify(c, name, at, true);
                if (u is null)
                {
                    return true;
                }

                if (u is MatrixType)
                {
                    Err(SemanticCodes.TypeMismatch, "'" + name + "' needs a vector or scalar, not a matrix.", c.Span);
                    return true;
                }

                info.Builtin = new BuiltinCall { Kind = BuiltinKind.Intrinsic, Ir = name == "length" ? Intrinsic.Length : Intrinsic.Normalize, Name = name, ParamTypes = [u], Result = name == "length" ? u.ScalarElement! : u };
                result = info.Builtin.Result;
                return true;
            }

            case "cross":
            {
                if (!ArityOk(c, name, 2) || AnyError(at))
                {
                    return true;
                }

                var u = Unify(c, name, at, true);
                if (u is not VectorType { Count: 3 })
                {
                    if (u is not null)
                    {
                        Err(SemanticCodes.TypeMismatch, "'cross' needs two float3 arguments.", c.Span);
                    }

                    return true;
                }

                info.Builtin = new BuiltinCall { Kind = BuiltinKind.Intrinsic, Ir = Intrinsic.Cross, Name = name, ParamTypes = Fill(u, 2), Result = u };
                result = u;
                return true;
            }

            case "reflect":
            {
                if (!ArityOk(c, name, 2) || AnyError(at))
                {
                    return true;
                }

                var u = Unify(c, name, at, true);
                if (u is null)
                {
                    return true;
                }

                info.Builtin = new BuiltinCall { Kind = BuiltinKind.Intrinsic, Ir = Intrinsic.Reflect, Name = name, ParamTypes = Fill(u, 2), Result = u };
                result = u;
                return true;
            }

            case "refract":
            {
                if (!ArityOk(c, name, 3) || AnyError(at))
                {
                    return true;
                }

                var u = Unify(c, name, at, true, [0, 1]);
                if (u is null)
                {
                    return true;
                }

                info.Builtin = new BuiltinCall { Kind = BuiltinKind.Intrinsic, Ir = Intrinsic.Refract, Name = name, ParamTypes = [u, u, u.ScalarElement!], Result = u };
                result = u;
                return true;
            }

            case "all" or "any":
            {
                if (!ArityOk(c, name, 1) || AnyError(at))
                {
                    return true;
                }

                if (at[0].ScalarElement is null)
                {
                    Err(SemanticCodes.TypeMismatch, "Argument 1 of '" + name + "' must be a number or vector, not '" + at[0] + "'.", c.Arguments[0].Span);
                    return true;
                }

                var pt = TypeOps.Reshape(at[0], ScalarType.Bool);
                info.Builtin = new BuiltinCall { Kind = BuiltinKind.Intrinsic, Ir = name == "all" ? Intrinsic.All : Intrinsic.Any, Name = name, ParamTypes = [pt], Result = ScalarType.Bool };
                result = ScalarType.Bool;
                return true;
            }

            case "determinant":
            {
                if (!ArityOk(c, name, 1) || AnyError(at))
                {
                    return true;
                }

                if (at[0] is not MatrixType { Rows: var r, Columns: var cc } m || r != cc)
                {
                    Err(SemanticCodes.TypeMismatch, "'determinant' needs a square matrix.", c.Span);
                    return true;
                }

                info.Builtin = new BuiltinCall { Kind = BuiltinKind.Intrinsic, Ir = Intrinsic.Determinant, Name = name, ParamTypes = [m], Result = m.Element };
                result = m.Element;
                return true;
            }

            case "transpose":
            {
                if (!ArityOk(c, name, 1) || AnyError(at))
                {
                    return true;
                }

                if (at[0] is not MatrixType m)
                {
                    Err(SemanticCodes.TypeMismatch, "'transpose' needs a matrix.", c.Span);
                    return true;
                }

                result = HlslType.Matrix(m.Element, m.Columns, m.Rows);
                info.Builtin = new BuiltinCall { Kind = BuiltinKind.Transpose, Name = name, ParamTypes = [m], Result = result };
                return true;
            }

            case "mul":
                if (ArityOk(c, name, 2) && !AnyError(at))
                {
                    result = CheckMul(c, at, info);
                }

                return true;
            case "asfloat" or "asint" or "asuint":
            {
                if (!ArityOk(c, name, 1) || AnyError(at))
                {
                    return true;
                }

                var s = at[0].ScalarElement;
                if (s is null || TypeOps.BitWidth(s) != 32 || s.ScalarKind == ScalarKind.Bool)
                {
                    Err(SemanticCodes.TypeMismatch, "'" + name + "' reinterprets 32-bit numbers, not '" + at[0] + "'.", c.Arguments[0].Span);
                    return true;
                }

                var to = name == "asfloat" ? ScalarType.Float : name == "asint" ? ScalarType.Int : ScalarType.UInt;
                result = TypeOps.Reshape(at[0], to);
                info.Builtin = new BuiltinCall { Kind = BuiltinKind.AsType, Name = name, ParamTypes = [at[0]], Result = result };
                return true;
            }

            case "clip":
            {
                if (!ArityOk(c, name, 1) || AnyError(at))
                {
                    return true;
                }

                if (at[0].ScalarElement is null)
                {
                    Err(SemanticCodes.TypeMismatch, "'clip' needs a number or vector.", c.Arguments[0].Span);
                    return true;
                }

                var pt = TypeOps.Reshape(at[0], ScalarType.Float);
                info.Builtin = new BuiltinCall { Kind = BuiltinKind.Clip, Name = name, ParamTypes = [pt], Result = HlslType.Void };
                result = HlslType.Void;
                if (_fn is not null)
                {
                    _fn.Discards = true;
                }

                return true;
            }

            case "select":
            {
                if (!ArityOk(c, name, 3) || AnyError(at))
                {
                    return true;
                }

                var u = Unify(c, name, at, false, [1, 2]);
                if (u is null)
                {
                    return true;
                }

                if (at[0].ScalarElement is null || !(at[0] is ScalarType || TypeOps.SameShape(at[0], u)))
                {
                    Err(SemanticCodes.TypeMismatch, "The condition of 'select' must be a scalar or have the same size as the values.", c.Arguments[0].Span);
                    return true;
                }

                info.Builtin = new BuiltinCall { Kind = BuiltinKind.Select, Name = name, ParamTypes = [TypeOps.Reshape(at[0], ScalarType.Bool), u, u], Result = u };
                result = u;
                return true;
            }

            case "countbits" or "reversebits" or "firstbithigh" or "firstbitlow" or "sincos" or "modf" or "frexp" or "f16tof32" or "f32tof16"
                or "noise" or "lit" or "dst" or "msad4" or "D3DCOLORtoUBYTE4" or "abort" or "errorf" or "printf":
                Err(SemanticCodes.Unsupported, "The intrinsic '" + name + "' is not supported yet.", c.Span);
                return true;
            default:
                if (name.StartsWith("Wave", StringComparison.Ordinal) || name.StartsWith("Quad", StringComparison.Ordinal)
                    || name.StartsWith("Interlocked", StringComparison.Ordinal) || name.Contains("MemoryBarrier", StringComparison.Ordinal))
                {
                    Err(SemanticCodes.Unsupported, "The intrinsic '" + name + "' is not supported yet.", c.Span);
                    return true;
                }

                return false;
        }
    }

    private HlslType CheckMul(CallExpression c, HlslType[] at, ExprInfo info)
    {
        var a = at[0];
        var b = at[1];
        if (a.ScalarElement is null || b.ScalarElement is null)
        {
            Err(SemanticCodes.TypeMismatch, "'mul' needs numbers, vectors or matrices, not '" + a + "' and '" + b + "'.", c.Span);
            return HlslType.Error;
        }

        var sc = TypeOps.Common(TypeOps.ArithmeticScalar(a.ScalarElement), TypeOps.ArithmeticScalar(b.ScalarElement));
        if (!sc.IsFloatingPoint)
        {
            // Integer products are allowed for scalar and component-wise cases only; keep the exact types.
            if (a is MatrixType || b is MatrixType)
            {
                sc = ScalarType.Float;
            }
        }

        HlslType result;
        HlslType pa = TypeOps.Reshape(a, sc);
        HlslType pb = TypeOps.Reshape(b, sc);
        switch (a, b)
        {
            case (ScalarType, _):
                result = pb;
                break;
            case (_, ScalarType):
                result = pa;
                break;
            case (VectorType va, VectorType vb):
                if (va.Count != vb.Count)
                {
                    Err(SemanticCodes.TypeMismatch, "'mul' of two vectors needs equal sizes, not '" + a + "' and '" + b + "'.", c.Span);
                    return HlslType.Error;
                }

                result = sc;
                break;
            case (MatrixType ma, VectorType vb):
                if (ma.Columns != vb.Count)
                {
                    Err(SemanticCodes.TypeMismatch, "mul(" + a + ", " + b + ") does not multiply: the matrix has " + Num(ma.Columns) + " columns but the vector has " + Num(vb.Count) + " components.", c.Span);
                    return HlslType.Error;
                }

                result = HlslType.Vector(sc, ma.Rows);
                break;
            case (VectorType va, MatrixType mb):
                if (mb.Rows != va.Count)
                {
                    Err(SemanticCodes.TypeMismatch, "mul(" + a + ", " + b + ") does not multiply: the vector has " + Num(va.Count) + " components but the matrix has " + Num(mb.Rows) + " rows.", c.Span);
                    return HlslType.Error;
                }

                result = HlslType.Vector(sc, mb.Columns);
                break;
            case (MatrixType ma, MatrixType mb):
                if (ma.Columns != mb.Rows)
                {
                    Err(SemanticCodes.TypeMismatch, "mul(" + a + ", " + b + ") does not multiply: the first matrix has " + Num(ma.Columns) + " columns but the second has " + Num(mb.Rows) + " rows.", c.Span);
                    return HlslType.Error;
                }

                result = HlslType.Matrix(sc, ma.Rows, mb.Columns);
                break;
            default:
                return HlslType.Error;
        }

        info.Builtin = new BuiltinCall { Kind = BuiltinKind.Mul, Name = "mul", ParamTypes = [pa, pb], Result = result };
        return result;
    }
}

internal static class EnumerableExtensions
{
    public static IEnumerable<string> Concat2(this IEnumerable<string> a, IEnumerable<string> b)
    {
        foreach (var x in a)
        {
            yield return x;
        }

        foreach (var x in b)
        {
            yield return x;
        }
    }
}
