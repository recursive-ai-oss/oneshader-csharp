using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using OneShader.IR;
using OneShader.Types;

namespace OneShader.Msl;

internal sealed partial class MslEmitter
{
    private readonly Dictionary<StructType, string> _structNames = [];
    private readonly Dictionary<StructType, string[]> _fieldNames = [];
    private int _typeDepth;

    // ---- register types ---------------------------------------------------------------------------

    private string T(HlslType t)
    {
        if (_typeDepth > MaxDepth)
        {
            Malformed("A type is nested too deeply to be emitted; flatten the declaration.");
            return "int";
        }

        _typeDepth++;
        try
        {
            switch (t)
            {
                case ScalarType s:
                    return ScalarName(s);
                case EnumType e:
                    return ScalarName(e.Underlying);
                case VectorType v:
                    return v.Count == 1 ? ScalarName(v.Element) : ScalarName(v.Element) + Inv(v.Count);
                case MatrixType m:
                    if (!m.Element.IsFloatingPoint)
                    {
                        Unsupported("MSL has matrices of float and half only; the " + m + " matrix cannot be emitted. Use an array of vectors.");
                        return "float4x4";
                    }

                    if (m.Rows == 1 || m.Columns == 1)
                    {
                        int n = m.Rows * m.Columns;
                        return n == 1 ? ScalarName(m.Element) : ScalarName(m.Element) + Inv(n);
                    }

                    // An HLSL matrix of R rows by C columns is an MSL matrix of R columns of C components: m[i] is row i.
                    return ScalarName(m.Element) + Inv(m.Rows) + "x" + Inv(m.Columns);
                case ArrayType a:
                    if (a.Length == 0)
                    {
                        Unsupported("An unsized array can only be the element array of a structured buffer.");
                        return "array<int, 1>";
                    }

                    return "array<" + T(a.Element) + ", " + Inv(a.Length) + ">";
                case StructType st:
                    return StructName(st);
                case PointerType p:
                    return T(p.Pointee);
                default:
                    if (t.IsVoid)
                    {
                        return "void";
                    }

                    Unsupported("The type '" + t + "' cannot be used as a value in MSL.");
                    return "int";
            }
        }
        finally
        {
            _typeDepth--;
        }
    }

    private string ScalarName(ScalarType s)
    {
        switch (s.ScalarKind)
        {
            case ScalarKind.Bool:
                return "bool";
            case ScalarKind.Int or ScalarKind.LiteralInt:
                return "int";
            case ScalarKind.UInt:
                return "uint";
            case ScalarKind.Int16:
                return "short";
            case ScalarKind.UInt16:
                return "ushort";
            case ScalarKind.Int64:
                return "long";
            case ScalarKind.UInt64:
                return "ulong";
            case ScalarKind.Min16Int:
                return _enable16 ? "short" : "int";
            case ScalarKind.Min16UInt:
                return _enable16 ? "ushort" : "uint";
            case ScalarKind.Half or ScalarKind.Min16Float or ScalarKind.Min10Float:
                return _enable16 ? "half" : "float";
            case ScalarKind.Double:
                Unsupported("MSL has no 64-bit floating-point type; replace double with float.");
                return "float";
            default:
                return "float";
        }
    }

    private string StructName(StructType st)
    {
        if (_structNames.TryGetValue(st, out var name))
        {
            return name;
        }

        // Claim the name first so a self-referential declaration cannot recurse; members come out before the struct itself.
        name = _names.Claim(st.Name.Length == 0 ? "Struct" : st.Name, "Struct");
        _structNames[st] = name;
        var fields = new string[st.Fields.Count];
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var body = new StringBuilder();
        for (int i = 0; i < fields.Length; i++)
        {
            fields[i] = UniqueMember(st.Fields[i].Name, "m" + Inv(i), taken);
        }

        _fieldNames[st] = fields;
        for (int i = 0; i < fields.Length; i++)
        {
            body.Append("    ").Append(T(st.Fields[i].Type)).Append(' ').Append(fields[i]).Append(";\n");
        }

        _typeText.Append("struct ").Append(name).Append("\n{\n").Append(body).Append("};\n\n");
        return name;
    }

    private static string UniqueMember(string hint, string fallback, HashSet<string> taken)
    {
        string s = MslNames.Sanitize(hint, fallback);
        string candidate = s;
        for (int n = 1; !taken.Add(candidate); n++)
        {
            candidate = s + "_" + n.ToString(CultureInfo.InvariantCulture);
        }

        return candidate;
    }

    private string FieldName(StructType st, int index)
    {
        StructName(st);
        return _fieldNames[st][index];
    }

    /// <summary>Number of scalar components a value has when it is a scalar, a vector or a one-row or one-column matrix.</summary>
    private static int Comp(HlslType t) => t switch
    {
        VectorType v => v.Count,
        MatrixType m when m.Rows == 1 || m.Columns == 1 => m.Rows * m.Columns,
        _ => 1,
    };

    private static bool IsFullMatrix(HlslType t) => t is MatrixType { Rows: > 1, Columns: > 1 };

    private static string Swz(int i) => i switch { 0 => "x", 1 => "y", 2 => "z", _ => "w" };

    // ---- constants -----------------------------------------------------------------------------------

    private string ConstExpr(Constant c, int depth)
    {
        if (depth > MaxDepth)
        {
            Malformed("A constant is nested too deeply to be emitted.");
            return "0";
        }

        switch (c.ConstantKind)
        {
            case ConstantKind.Undef:
            {
                var zero = Constant.Zero(c.Type);
                return zero.ConstantKind == ConstantKind.Undef ? T(c.Type) + "()" : ConstExpr(zero, depth + 1);
            }

            case ConstantKind.Scalar:
            {
                var st = c.Type as ScalarType ?? (c.Type as EnumType)?.Underlying ?? ScalarType.Int;
                return ScalarLiteral(st, c.Bits);
            }

            default:
                return CompositeConst(c, depth);
        }
    }

    private string CompositeConst(Constant c, int depth)
    {
        var type = c.Type;
        string tn = T(type);
        var parts = new List<string>(c.Elements.Count);
        switch (type)
        {
            case VectorType { Count: 1 }:
                return ConstExpr(c.Elements[0], depth + 1);
            case VectorType:
                foreach (var e in c.Elements)
                {
                    parts.Add(ConstExpr(e, depth + 1));
                }

                if (AllSame(parts))
                {
                    return tn + "(" + parts[0] + ")";
                }

                return tn + "(" + string.Join(", ", parts) + ")";
            case MatrixType m when m.Rows == 1 || m.Columns == 1:
                if (m.Rows * m.Columns == 1)
                {
                    return ConstExpr(FirstScalar(c), depth + 1);
                }

                FlattenScalars(c, parts, depth);
                return tn + "(" + string.Join(", ", parts) + ")";
            case MatrixType:
                foreach (var e in c.Elements)
                {
                    parts.Add(ConstExpr(e, depth + 1));
                }

                return tn + "(" + string.Join(", ", parts) + ")";
            case ArrayType:
                foreach (var e in c.Elements)
                {
                    parts.Add(ConstExpr(e, depth + 1));
                }

                return tn + "{" + string.Join(", ", parts) + "}";
            case StructType:
                foreach (var e in c.Elements)
                {
                    parts.Add(ConstExpr(e, depth + 1));
                }

                return tn + "{" + string.Join(", ", parts) + "}";
            default:
                return tn + "()";
        }
    }

    private static bool AllSame(List<string> parts)
    {
        for (int i = 1; i < parts.Count; i++)
        {
            if (parts[i] != parts[0])
            {
                return false;
            }
        }

        return parts.Count > 1;
    }

    private static Constant FirstScalar(Constant c)
    {
        while (c.ConstantKind == ConstantKind.Composite && c.Elements.Count > 0)
        {
            c = c.Elements[0];
        }

        return c;
    }

    private void FlattenScalars(Constant c, List<string> into, int depth)
    {
        if (c.ConstantKind == ConstantKind.Composite)
        {
            foreach (var e in c.Elements)
            {
                FlattenScalars(e, into, depth + 1);
            }
        }
        else
        {
            into.Add(ConstExpr(c, depth + 1));
        }
    }

    private string ScalarLiteral(ScalarType st, ulong bits)
    {
        switch (st.ScalarKind)
        {
            case ScalarKind.Bool:
                return bits != 0 ? "true" : "false";
            case ScalarKind.Int or ScalarKind.LiteralInt:
            {
                int v = (int)(uint)bits;
                return v == int.MinValue ? "(-2147483647 - 1)" : v < 0 ? "(" + Inv(v) + ")" : Inv(v);
            }

            case ScalarKind.UInt:
                return Inv((uint)bits) + "u";
            case ScalarKind.Int16:
                return "short(" + Inv((short)(ushort)bits) + ")";
            case ScalarKind.UInt16:
                return "ushort(" + Inv((ushort)bits) + ")";
            case ScalarKind.Min16Int:
                return _enable16 ? "short(" + Inv((short)(ushort)bits) + ")" : ScalarLiteral(ScalarType.Int, (ulong)(long)(int)(uint)bits);
            case ScalarKind.Min16UInt:
                return _enable16 ? "ushort(" + Inv((ushort)bits) + ")" : Inv((uint)bits) + "u";
            case ScalarKind.Int64:
            {
                long v = (long)bits;
                return v == long.MinValue ? "(-9223372036854775807l - 1)" : "long(" + Inv(v) + ")";
            }

            case ScalarKind.UInt64:
                return "ulong(" + Inv(bits) + "ul)";
            case ScalarKind.Half or ScalarKind.Min16Float or ScalarKind.Min10Float:
            {
                float f = (float)BitConverter.UInt16BitsToHalf((ushort)bits);
                return _enable16 ? "half(" + FloatLiteral(f, false) + ")" : FloatLiteral(f, true);
            }

            case ScalarKind.Double:
                Unsupported("MSL has no 64-bit floating-point type; replace double with float.");
                return "0.0f";
            default:
                return FloatLiteral(BitConverter.UInt32BitsToSingle((uint)bits), true);
        }
    }

    private static string FloatLiteral(float f, bool suffix)
    {
        if (float.IsNaN(f))
        {
            return "as_type<float>(0x" + BitConverter.SingleToUInt32Bits(f).ToString("x8", CultureInfo.InvariantCulture) + "u)";
        }

        if (float.IsPositiveInfinity(f))
        {
            return "INFINITY";
        }

        if (float.IsNegativeInfinity(f))
        {
            return "(-INFINITY)";
        }

        string s = f.ToString("R", CultureInfo.InvariantCulture);
        if (s.IndexOfAny(['.', 'E', 'e']) < 0)
        {
            s += ".0";
        }

        if (suffix)
        {
            s += "f";
        }

        return s[0] == '-' ? "(" + s + ")" : s;
    }

    // ---- buffer (memory) layout types ---------------------------------------------------------------------

    /// <summary>A declaration of a value as stored in a buffer: <c>Type name Dims;</c>.</summary>
    private readonly record struct MemDecl(string Type, string Dims);

    private readonly Dictionary<string, string> _memStructs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _cells = new(StringComparer.Ordinal);
    private int _memDepth;

    /// <summary>The packed scalar or vector type that has exactly the size of the layout's value and the alignment of its component.</summary>
    private string PackedName(ScalarType s, int count)
    {
        string baseName = ScalarName(s);
        if (baseName == "bool")
        {
            baseName = "uint"; // bool has no defined size in buffers: stored as 0 or 1 in a 32-bit word
        }

        if (count == 1)
        {
            return baseName;
        }

        if (baseName is "long" or "ulong")
        {
            Unsupported("64-bit integer vectors in buffers are not supported in MSL output; use scalars.");
            return baseName + Inv(count);
        }

        return "packed_" + baseName + Inv(count);
    }

    private string CellName(string inner, int size, int stride)
    {
        string key = inner + "|" + Inv(stride);
        if (_cells.TryGetValue(key, out var name))
        {
            return name;
        }

        name = _names.ClaimExact("os_cell" + Inv(stride) + "_" + inner);
        _cells[key] = name;
        var sb = new StringBuilder();
        sb.Append("struct ").Append(name).Append("\n{\n    ").Append(inner).Append(" v;\n");
        if (stride > size)
        {
            sb.Append("    uchar pad[").Append(Inv(stride - size)).Append("];\n");
        }

        sb.Append("};\n");
        sb.Append("#ifdef ONESHADER_CHECK_LAYOUT\nstatic_assert(sizeof(").Append(name).Append(") == ").Append(Inv(stride)).Append(", \"cell size\");\n#endif\n\n");
        _typeText.Append(sb);
        return name;
    }

    /// <summary>
    /// The storage declaration for a value with this layout: scalars and vectors use packed types so their alignment is
    /// that of a component, arrays and matrix lines whose stride exceeds their size are wrapped in padded cells, and
    /// structs get explicit padding so every member sits at its reflected offset.
    /// </summary>
    private MemDecl MemOf(TypeLayout l)
    {
        if (_memDepth > MaxDepth)
        {
            Malformed("A buffer type is nested too deeply to be emitted.");
            return new MemDecl("uint", "");
        }

        _memDepth++;
        try
        {
            switch (l.Type)
            {
                case ScalarType s:
                    return new MemDecl(PackedName(s, 1), "");
                case EnumType e:
                    return new MemDecl(PackedName(e.Underlying, 1), "");
                case VectorType v:
                    return new MemDecl(PackedName(v.Element, v.Count), "");
                case MatrixType m:
                    return MatrixMem(l, m);
                case ArrayType a:
                {
                    if (l.Element is null)
                    {
                        Malformed("A buffer array has no element layout.");
                        return new MemDecl("uint", "");
                    }

                    var elem = ElementMem(l.Element, l.ArrayStride);
                    return new MemDecl(elem.Type, "[" + Inv(a.Length) + "]" + elem.Dims);
                }

                case StructType st:
                    return new MemDecl(MemStruct(st, l), "");
                default:
                    Unsupported("The type '" + l.Type + "' cannot be stored in a buffer in MSL output.");
                    return new MemDecl("uint", "");
            }
        }
        finally
        {
            _memDepth--;
        }
    }

    /// <summary>The declaration of one array element, wrapped in a padded cell when the stride is larger than the element.</summary>
    private MemDecl ElementMem(TypeLayout element, int stride)
    {
        var d = MemOf(element);
        if (stride == element.Size)
        {
            return d;
        }

        if (d.Dims.Length > 0 || stride < element.Size || element.Type is StructType)
        {
            Unsupported("An array in a buffer has a stride of " + Inv(stride) + " bytes for elements of " + Inv(element.Size) + " bytes, which MSL output cannot express for this element type.");
            return d;
        }

        return new MemDecl(CellName(d.Type, element.Size, stride), "");
    }

    /// <summary>True when the element of an array in memory was wrapped in a padded cell (so its value is the cell's <c>v</c> member).</summary>
    private static bool ElementIsCell(TypeLayout array) =>
        array.Element is { } e && array.ArrayStride != e.Size && e.Type is ScalarType or VectorType or EnumType;

    private MemDecl MatrixMem(TypeLayout l, MatrixType m)
    {
        if (!m.Element.IsFloatingPoint)
        {
            Unsupported("MSL has matrices of float and half only; the " + m + " matrix in a buffer is not supported.");
            return new MemDecl("uint", "");
        }

        bool rowMajor = l.MatrixPacking == MatrixPacking.RowMajor;
        int lines = rowMajor ? m.Rows : m.Columns;
        int lineLength = rowMajor ? m.Columns : m.Rows;
        int s = m.Element.StorageBytes(_enable16);
        var line = PackedName(m.Element, lineLength);
        if (m.Rows == 1 || m.Columns == 1)
        {
            bool contiguous = m.Rows == 1 ? rowMajor : !rowMajor;
            if (!contiguous && m.Rows * m.Columns > 1)
            {
                Unsupported("A " + m + " matrix stored " + (rowMajor ? "row_major" : "column_major") + " in a buffer is not contiguous and is not supported; use the other packing or a vector.");
            }

            return new MemDecl(PackedName(m.Element, m.Rows * m.Columns), "");
        }

        if (l.MatrixStride == lineLength * s)
        {
            return new MemDecl(line, "[" + Inv(lines) + "]");
        }

        if (l.MatrixStride < lineLength * s)
        {
            Malformed("A matrix stride is smaller than its columns.");
        }

        return new MemDecl(CellName(line, lineLength * s, l.MatrixStride), "[" + Inv(lines) + "]");
    }

    /// <summary>True when the lines of a stored matrix are wrapped in padded cells.</summary>
    private bool MatrixLinesAreCells(TypeLayout l, MatrixType m)
    {
        bool rowMajor = l.MatrixPacking == MatrixPacking.RowMajor;
        int lineLength = rowMajor ? m.Columns : m.Rows;
        return l.MatrixStride != lineLength * m.Element.StorageBytes(_enable16);
    }

    private string MemStruct(StructType st, TypeLayout l)
    {
        var sig = new StringBuilder();
        sig.Append(StructName(st)).Append('|').Append(Inv(l.Size));
        foreach (var ml in l.Members)
        {
            sig.Append('|').Append(Inv(ml.Offset)).Append(':').Append(LayoutSignature(ml.Layout, 0));
        }

        string key = sig.ToString();
        if (_memStructs.TryGetValue(key, out var existing))
        {
            return existing;
        }

        string name = _names.Claim(StructName(st) + "_mem", "Mem");
        _memStructs[key] = name;
        var order = new List<int>();
        for (int i = 0; i < l.Members.Count; i++)
        {
            order.Add(i);
        }

        order.Sort((a, b) =>
        {
            int c = l.Members[a].Offset.CompareTo(l.Members[b].Offset);
            return c != 0 ? c : a.CompareTo(b);
        });

        var decls = new MemDecl[l.Members.Count];
        for (int i = 0; i < decls.Length; i++)
        {
            decls[i] = MemOf(l.Members[i].Layout);
        }

        var body = new StringBuilder();
        var checks = new StringBuilder();
        int cursor = 0;
        int padCount = 0;
        foreach (int i in order)
        {
            var ml = l.Members[i];
            if (ml.Offset < cursor)
            {
                Unsupported("Members of '" + st.Name + "' overlap in the buffer layout (member '" + ml.Name + "' at offset " + Inv(ml.Offset) + "); MSL output cannot express overlapping members.");
                return name;
            }

            if (ml.Offset > cursor)
            {
                body.Append("    uchar pad").Append(Inv(padCount++)).Append('[').Append(Inv(ml.Offset - cursor)).Append("];\n");
            }

            string member = FieldName(st, i);
            body.Append("    ").Append(decls[i].Type).Append(' ').Append(member).Append(decls[i].Dims).Append(";\n");
            checks.Append("static_assert(__builtin_offsetof(").Append(name).Append(", ").Append(member).Append(") == ").Append(Inv(ml.Offset)).Append(", \"offset of ").Append(member).Append("\");\n");
            cursor = ml.Offset + ml.Layout.Size;
        }

        if (l.Size > cursor)
        {
            body.Append("    uchar pad").Append(Inv(padCount)).Append('[').Append(Inv(l.Size - cursor)).Append("];\n");
        }

        _typeText.Append("struct ").Append(name).Append("\n{\n").Append(body).Append("};\n\n");
        _layoutChecks.Append("static_assert(sizeof(").Append(name).Append(") == ").Append(Inv(l.Size)).Append(", \"size of ").Append(name).Append("\");\n").Append(checks);
        return name;
    }

    private static string LayoutSignature(TypeLayout l, int depth)
    {
        if (depth > MaxDepth)
        {
            return "?";
        }

        switch (l.Type)
        {
            case StructType st:
            {
                var sb = new StringBuilder("S").Append(st.Name).Append('{');
                foreach (var ml in l.Members)
                {
                    sb.Append(Inv(ml.Offset)).Append(':').Append(LayoutSignature(ml.Layout, depth + 1)).Append(',');
                }

                return sb.Append('}').ToString();
            }

            case ArrayType a:
                return "A" + Inv(a.Length) + "/" + Inv(l.ArrayStride) + "(" + (l.Element is null ? "" : LayoutSignature(l.Element, depth + 1)) + ")";
            case MatrixType m:
                return "M" + m + "/" + Inv(l.MatrixStride) + (l.MatrixPacking == MatrixPacking.RowMajor ? "r" : "c");
            default:
                return l.Type.ToString() + "/" + Inv(l.Size);
        }
    }
}
