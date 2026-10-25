using System.Collections.Generic;
using OneShader.IR;
using OneShader.Syntax;
using OneShader.Types;

namespace OneShader.Semantics;

/// <summary>Lowering of initializer lists; mirrors the cursor rules of the checker (flat runs fill aggregates, braces start a new one).</summary>
internal sealed partial class Lowerer
{
    private sealed class InitCursor(IReadOnlyList<Expression> elems)
    {
        public IReadOnlyList<Expression> Elems { get; } = elems;

        public int Pos { get; set; }

        /// <summary>Scalars of an already evaluated vector or matrix expression that no slot has taken yet.</summary>
        public Queue<IrValue> Pending { get; } = new();

        public bool Exhausted => Pos >= Elems.Count && Pending.Count == 0;
    }

    private const int MaxInitializedArray = 4096;

    private IrValue LowerInitList(InitializerListExpression il, HlslType target)
    {
        if (target is ScalarType)
        {
            return il.Elements.Count > 0 ? LowerInitializer(il.Elements[0], target) : Constant.Zero(target);
        }

        return LowerAggregate(new InitCursor(il.Elements), target);
    }

    private IrValue LowerAggregate(InitCursor c, HlslType s)
    {
        switch (s)
        {
            case StructType st:
            {
                var parts = new IrValue[st.Fields.Count];
                for (int i = 0; i < parts.Length; i++)
                {
                    parts[i] = c.Exhausted ? Constant.Zero(st.Fields[i].Type) : LowerInitSlot(c, st.Fields[i].Type);
                }

                return _b.Construct(st, parts);
            }

            case ArrayType at:
            {
                if (at.Length > MaxInitializedArray)
                {
                    // One composite construct holds every element, and a SPIR-V instruction is limited to 65535 words.
                    _diag.Error(SemanticCodes.Limit, "An array initializer cannot cover more than " + MaxInitializedArray + " elements (this array has " + at.Length + "). Declare the array without an initializer and fill it in a loop.", _span);
                    return Constant.Undef(at);
                }

                var parts = new List<IrValue>();
                for (int i = 0; at.Length == 0 ? !c.Exhausted : i < at.Length; i++)
                {
                    parts.Add(c.Exhausted ? Constant.Zero(at.Element) : LowerInitSlot(c, at.Element));
                }

                return _b.Construct(at, [.. parts]);
            }

            case VectorType vt:
                return Rebuild(TakeInitScalars(c, vt.Count, vt.Element), s);
            case MatrixType mt:
            {
                var scalars = new List<IrValue>();
                var rowType = mt.Columns == 1 ? (HlslType)mt.Element : HlslType.Vector(mt.Element, mt.Columns);
                for (int r = 0; r < mt.Rows; r++)
                {
                    FlattenScalars(c.Exhausted ? Constant.Zero(rowType) : LowerInitSlot(c, rowType), scalars);
                }

                return Rebuild(scalars, s);
            }

            default:
                return Rebuild(TakeInitScalars(c, 1, s.ScalarElement ?? ScalarType.Float), s);
        }
    }

    private IrValue LowerInitSlot(InitCursor c, HlslType s)
    {
        if (c.Pending.Count == 0)
        {
            var e = c.Elems[c.Pos];
            if (e is InitializerListExpression sub)
            {
                c.Pos++;
                return LowerInitList(sub, s);
            }

            if (s is not ScalarType && e.Type is { IsError: false } t && t.Equals(s))
            {
                c.Pos++;
                return Rv(e);
            }
        }

        return LowerAggregate(c, s);
    }

    private List<IrValue> TakeInitScalars(InitCursor c, int n, ScalarType target)
    {
        var result = new List<IrValue>(n);
        for (int k = 0; k < n; k++)
        {
            if (c.Pending.Count == 0)
            {
                if (c.Pos >= c.Elems.Count)
                {
                    result.Add(Constant.Zero(target));
                    continue;
                }

                var e = c.Elems[c.Pos++];
                if (e is InitializerListExpression sub)
                {
                    result.Add(Coerce(LowerInitList(sub, target), target));
                    continue;
                }

                var flat = new List<IrValue>();
                FlattenScalars(Rv(e), flat);
                foreach (var f in flat)
                {
                    c.Pending.Enqueue(f);
                }
            }

            result.Add(ConvertScalar(c.Pending.Dequeue(), target));
        }

        return result;
    }
}
