using System.Collections.Generic;
using OneShader.Diagnostics;
using OneShader.Syntax;
using OneShader.Types;

namespace OneShader.Semantics;

/// <summary>The enums the cooperative-matrix members take; they exist without a declaration, as <c>MatrixUse</c> and <c>MatrixLayout</c>.</summary>
internal static class CooperativeNames
{
    public static EnumType Use { get; } = new("MatrixUse", ScalarType.Int,
        [new("A", 0), new("B", 1), new("Accumulator", 2)]);

    public static EnumType Layout { get; } = new("MatrixLayout", ScalarType.Int,
        [new("RowMajor", 0), new("ColumnMajor", 1)]);

    public static EnumType? ByName(string name) => name switch
    {
        "MatrixUse" => Use,
        "MatrixLayout" => Layout,
        _ => null,
    };
}

internal sealed partial class Checker
{
    /// <summary>Largest side a cooperative matrix type may name; real hardware shapes are 8 to 64.</summary>
    private const int MaxCooperativeSide = 256;

    private HlslType ResolveCooperativeMatrix(NamedTypeSyntax n)
    {
        if (n.Arguments.Count != 4)
        {
            Err(SemanticCodes.BadCooperativeMatrix, "CooperativeMatrix needs four template arguments: CooperativeMatrix<element type, rows, columns, MatrixUse::A | B | Accumulator>.", n.Span);
            return HlslType.Error;
        }

        if (n.Arguments[0].Type is not { } elementSyntax)
        {
            Err(SemanticCodes.BadCooperativeMatrix, "The first template argument of CooperativeMatrix must be the element type, for example float16_t.", n.Arguments[0].Span);
            return HlslType.Error;
        }

        string elementName = elementSyntax is NamedTypeSyntax en ? en.Name.Last.Text : "";
        if (elementName is "int8_t" or "uint8_t" or "int8_t4_packed" or "uint8_t4_packed")
        {
            Err(SemanticCodes.BadCooperativeMatrix, "8-bit integer cooperative matrices are not supported yet. Use float16_t or bfloat16_t inputs with a float accumulator.", elementSyntax.Span);
            return HlslType.Error;
        }

        if (elementName is "half" or "min16float" or "min10float" && !_opt.Layout.Enable16BitTypes)
        {
            Err(SemanticCodes.SixteenBitTypesOff, "A CooperativeMatrix of '" + elementName + "' needs 16-bit types enabled. Compile with -enable-16bit-types (MSBuild: OneShaderEnable16BitTypes=true), or use bfloat16_t or float.", elementSyntax.Span);
            return HlslType.Error;
        }

        var element = ResolveType(elementSyntax);
        if (element.IsError)
        {
            return HlslType.Error;
        }

        if (element is not ScalarType { IsFloatingPoint: true } scalar || scalar.IsLiteral || scalar.ScalarKind is ScalarKind.Double)
        {
            Err(SemanticCodes.BadCooperativeMatrix, "The element type of a CooperativeMatrix must be float16_t, bfloat16_t or float, not '" + element + "'. Integer matrices are not supported yet.", elementSyntax.Span);
            return HlslType.Error;
        }

        var rows = TemplateInt(n.Arguments[1], "the row count");
        var columns = TemplateInt(n.Arguments[2], "the column count");
        if (rows is null || columns is null)
        {
            return HlslType.Error;
        }

        if (rows.Value is < 1 or > MaxCooperativeSide || columns.Value is < 1 or > MaxCooperativeSide)
        {
            Err(SemanticCodes.BadCooperativeMatrix, "A CooperativeMatrix has between 1 and " + Num(MaxCooperativeSide) + " rows and columns; this one is " + Num(rows.Value) + " x " + Num(columns.Value) + ".", n.Span);
            return HlslType.Error;
        }

        if (n.Arguments[3].Type is not NamedTypeSyntax { Name.Parts.Count: 2 } useName || useName.Name.Parts[0].Text != "MatrixUse")
        {
            Err(SemanticCodes.BadCooperativeMatrix, "The fourth template argument of CooperativeMatrix must be MatrixUse::A, MatrixUse::B or MatrixUse::Accumulator.", n.Arguments[3].Span);
            return HlslType.Error;
        }

        CooperativeMatrixUse use;
        switch (useName.Name.Parts[1].Text)
        {
            case "A":
                use = CooperativeMatrixUse.A;
                break;
            case "B":
                use = CooperativeMatrixUse.B;
                break;
            case "Accumulator":
                use = CooperativeMatrixUse.Accumulator;
                break;
            default:
                Err(SemanticCodes.BadCooperativeMatrix, "'MatrixUse::" + useName.Name.Parts[1].Text + "' is not a matrix use. Write MatrixUse::A, MatrixUse::B or MatrixUse::Accumulator.", n.Arguments[3].Span);
                return HlslType.Error;
        }

        if (use == CooperativeMatrixUse.Accumulator && scalar.ScalarKind == ScalarKind.BFloat16)
        {
            Err(SemanticCodes.BadCooperativeMatrix, "An accumulator cannot be bfloat16_t; accumulate in float.", elementSyntax.Span);
            return HlslType.Error;
        }

        return new CooperativeMatrixType(scalar, rows.Value, columns.Value, use);
    }

    /// <summary>A groupshared array variable or a structured buffer: the memory a cooperative matrix loads from and stores to.</summary>
    private bool CheckCooperativeMemory(Expression e, CooperativeMatrixType cm, bool store, string method)
    {
        var t = e.Type ?? HlslType.Error;
        if (t.IsError)
        {
            return false;
        }

        HlslType? element = null;
        bool writable = true;
        if (t is ResourceType { ResourceKind: ResourceTypeKind.StructuredBuffer or ResourceTypeKind.RWStructuredBuffer } rt)
        {
            element = rt.Element;
            writable = rt.IsReadWrite;
        }
        else if (t is ArrayType { Element: ScalarType } at && UnwrapParens(e) is NameExpression { Info.Symbol: VariableSymbol { Kind: VarKind.GroupShared } })
        {
            element = at.Element;
        }

        if (element is null)
        {
            Err(SemanticCodes.BadCooperativeMatrix, "'" + method + "' reads and writes a StructuredBuffer, a RWStructuredBuffer or a groupshared array of " + cm.Element + ", not '" + t + "'.", e.Span);
            return false;
        }

        if (!element.Equals(cm.Element))
        {
            Err(SemanticCodes.BadCooperativeMatrix, "The memory holds '" + element + "' but the matrix element type is '" + cm.Element + "'; the types must be the same.", e.Span);
            return false;
        }

        if (store && !writable)
        {
            Err(SemanticCodes.BadCooperativeMatrix, "Store writes memory, so the buffer must be a RWStructuredBuffer.", e.Span);
            return false;
        }

        return true;
    }

    private static Expression UnwrapParens(Expression e)
    {
        while (e is ParenthesizedExpression p)
        {
            e = p.Inner;
        }

        return e;
    }

    private bool CooperativeLayoutArg(Expression e, out int layout)
    {
        layout = 0;
        if (e.Type is { IsError: true })
        {
            return false;
        }

        if (e.Info?.Const is { IsFloat: false } c && UnwrapParens(e) is NameExpression { Name.Parts.Count: 2 } nm && nm.Name.Parts[0].Text == "MatrixLayout")
        {
            layout = (int)c.Int;
            return true;
        }

        Err(SemanticCodes.BadCooperativeMatrix, "The layout must be written MatrixLayout::RowMajor or MatrixLayout::ColumnMajor.", e.Span);
        return false;
    }

    /// <summary><c>m.Load(...)</c>, <c>m.Store(...)</c> and <c>m.Length()</c>.</summary>
    private HlslType CheckCooperativeMethod(CallExpression c, MemberAccessExpression ma, CooperativeMatrixType cm, HlslType[] at, ExprInfo info)
    {
        string name = ma.Member.Text;
        switch (name)
        {
            case "Load" or "Store":
            {
                bool store = name == "Store";
                if (!ArityOk(c, name, 4))
                {
                    return HlslType.Error;
                }

                if (!store && !(ma.Target.Info?.IsLValue ?? false))
                {
                    Err(SemanticCodes.BadCooperativeMatrix, "Load fills the matrix it is called on, so it needs a variable: write 'matrix.Load(...)' on a declared CooperativeMatrix variable.", ma.Target.Span);
                    return HlslType.Error;
                }

                bool ok = CheckCooperativeMemory(c.Arguments[0], cm, store, name);
                ok &= CheckConvertible(c.Arguments[1], ScalarType.UInt);
                ok &= CheckConvertible(c.Arguments[2], ScalarType.UInt);
                ok &= CooperativeLayoutArg(c.Arguments[3], out int layout);
                if (!ok)
                {
                    return HlslType.Error;
                }

                info.Builtin = new BuiltinCall
                {
                    Kind = BuiltinKind.CooperativeMatrix,
                    Name = name,
                    ParamTypes = [at[0], ScalarType.UInt, ScalarType.UInt, ScalarType.Int],
                    Result = HlslType.Void,
                    Extra = layout,
                    HasOffset = store,
                };
                return HlslType.Void;
            }

            case "Length":
                if (!ArityOk(c, name, 0))
                {
                    return HlslType.Error;
                }

                info.Builtin = new BuiltinCall { Kind = BuiltinKind.CooperativeMatrix, Name = name, ParamTypes = [], Result = ScalarType.UInt };
                return ScalarType.UInt;
            default:
                Err(SemanticCodes.BadMember, "A CooperativeMatrix has the members Load, Store and Length, and the static Zero and MulAdd; it has no '" + name + "'.", ma.Member.Span);
                return HlslType.Error;
        }
    }

    /// <summary><c>CooperativeMatrix&lt;...&gt;::Zero()</c> and <c>::MulAdd(a, b, c)</c>.</summary>
    private HlslType CheckCooperativeStatic(ConstructExpression ce, HlslType target, HlslType[] at, ExprInfo info) =>
        CheckCooperativeStatic(ce.Span, ce.StaticMember!, ce.Arguments, target, at, info);

    /// <summary>The same through a typedef: <c>MC::Zero()</c>, a call whose callee is the qualified name.</summary>
    private HlslType CheckCooperativeStaticByName(CallExpression c, NameExpression ne, CooperativeMatrixType target, ExprInfo info)
    {
        var at = CheckArgs(c);
        return CheckCooperativeStatic(c.Span, ne.Name.Parts[1], c.Arguments, target, at, info);
    }

    private HlslType CheckCooperativeStatic(SourceSpan span, Identifier member, IReadOnlyList<Expression> args, HlslType target, HlslType[] at, ExprInfo info)
    {
        info.TargetType = target;
        if (target is not CooperativeMatrixType cm)
        {
            if (!target.IsError)
            {
                Err(SemanticCodes.BadMember, "'" + target + "' has no static member '" + member.Text + "'.", member.Span);
            }

            return HlslType.Error;
        }

        if (AnyError(at))
        {
            return HlslType.Error;
        }

        string name = member.Text;
        int n = args.Count;
        switch (name)
        {
            case "Zero":
                if (n != 0)
                {
                    Err(SemanticCodes.ArgumentCount, "Zero takes no arguments.", span);
                    return HlslType.Error;
                }

                info.Builtin = new BuiltinCall { Kind = BuiltinKind.CooperativeMatrix, Name = name, ParamTypes = [], Result = cm };
                return cm;
            case "MulAdd":
            {
                if (n != 3)
                {
                    Err(SemanticCodes.ArgumentCount, "MulAdd expects three arguments (a, b, c) but the call passes " + Num(n) + ".", span);
                    return HlslType.Error;
                }

                if (cm.Use != CooperativeMatrixUse.Accumulator)
                {
                    Err(SemanticCodes.BadCooperativeMatrix, "MulAdd returns an accumulator: call it on CooperativeMatrix<T, M, N, MatrixUse::Accumulator>.", span);
                    return HlslType.Error;
                }

                if (at[0] is not CooperativeMatrixType { Use: CooperativeMatrixUse.A } a)
                {
                    Err(SemanticCodes.BadCooperativeMatrix, "The first MulAdd argument must be a CooperativeMatrix with MatrixUse::A, not '" + at[0] + "'.", args[0].Span);
                    return HlslType.Error;
                }

                if (at[1] is not CooperativeMatrixType { Use: CooperativeMatrixUse.B } b)
                {
                    Err(SemanticCodes.BadCooperativeMatrix, "The second MulAdd argument must be a CooperativeMatrix with MatrixUse::B, not '" + at[1] + "'.", args[1].Span);
                    return HlslType.Error;
                }

                if (!at[2].Equals(cm))
                {
                    Err(SemanticCodes.BadCooperativeMatrix, "The third MulAdd argument must be the accumulator type '" + cm + "', not '" + at[2] + "'.", args[2].Span);
                    return HlslType.Error;
                }

                if (a.Rows != cm.Rows || b.Columns != cm.Columns || a.Columns != b.Rows)
                {
                    Err(SemanticCodes.BadCooperativeMatrix, "MulAdd shapes do not fit: A is " + Num(a.Rows) + " x " + Num(a.Columns) + ", B is " + Num(b.Rows) + " x " + Num(b.Columns) + ", the accumulator is " + Num(cm.Rows) + " x " + Num(cm.Columns) + ". A must be M x K, B K x N and the accumulator M x N.", span);
                    return HlslType.Error;
                }

                if (!a.Element.Equals(b.Element))
                {
                    Err(SemanticCodes.BadCooperativeMatrix, "A and B must have the same element type, but they are '" + a.Element + "' and '" + b.Element + "'.", span);
                    return HlslType.Error;
                }

                info.Builtin = new BuiltinCall { Kind = BuiltinKind.CooperativeMatrix, Name = name, ParamTypes = [a, b, cm], Result = cm };
                return cm;
            }

            default:
                Err(SemanticCodes.BadMember, "CooperativeMatrix has the static members Zero and MulAdd; it has no '" + name + "'.", member.Span);
                return HlslType.Error;
        }
    }
}
