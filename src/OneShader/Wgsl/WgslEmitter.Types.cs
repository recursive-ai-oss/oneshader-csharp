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
    private readonly Dictionary<(StructType, bool), string> _structNames = [];
    private readonly Dictionary<StructType, string[]> _fieldNames = [];
    private readonly List<string> _structDecls = [];
    private readonly List<string> _privateConstants = [];
    private readonly Dictionary<uint, string> _bitConstants = [];
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private int _typeDepth;

    private static string Inv(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static string Inv(uint v) => v.ToString(CultureInfo.InvariantCulture);

    private void Fail(string code, string message, SourceSpan span = default)
    {
        _failed = true;
        if (_reported.Add(code + message))
        {
            _d.Error(code, message, span.IsKnown ? span : _span);
        }
    }

    private void Unsupported(string what, SourceSpan span = default) => Fail(WgslCodes.Unsupported, what, span);

    // ---- types ------------------------------------------------------------------------------------

    private string ScalarName(ScalarType s)
    {
        switch (s.ScalarKind)
        {
            case ScalarKind.Bool:
                return "bool";
            case ScalarKind.Int or ScalarKind.LiteralInt:
                return "i32";
            case ScalarKind.UInt:
                return "u32";
            case ScalarKind.Float or ScalarKind.LiteralFloat:
                return "f32";
            case ScalarKind.Half or ScalarKind.Min16Float or ScalarKind.Min10Float:
                if (_m.LayoutOptions.Enable16BitTypes)
                {
                    Unsupported("16-bit floating point types need WebGPU's optional f16 feature, which the WGSL backend does not use; compile without 16-bit types so half is a 32-bit float.");
                }

                return "f32";
            case ScalarKind.Min16Int:
            case ScalarKind.Min16UInt:
                if (_m.LayoutOptions.Enable16BitTypes)
                {
                    Unsupported("16-bit integer types are not available in WGSL; compile without 16-bit types.");
                }

                return s.ScalarKind == ScalarKind.Min16Int ? "i32" : "u32";
            case ScalarKind.Double:
                Unsupported("double is not available in WGSL; use float.");
                return "f32";
            default:
                Unsupported("The type " + s + " is not available in WGSL; use int, uint or float.");
                return "i32";
        }
    }

    private string TypeName(HlslType t, bool atomic = false)
    {
        if (_typeDepth > MaxDepth)
        {
            Fail(WgslCodes.MalformedIr, "A type is nested too deeply to be emitted; flatten the declaration.");
            return "i32";
        }

        _typeDepth++;
        try
        {
            switch (t)
            {
                case ScalarType s:
                {
                    string n = ScalarName(s);
                    return atomic && n is "i32" or "u32" ? "atomic<" + n + ">" : n;
                }

                case EnumType e:
                    return TypeName(e.Underlying, atomic);
                case VectorType v:
                {
                    if (atomic)
                    {
                        Unsupported("A vector cannot be the target of an atomic operation in WGSL; use scalar members.");
                    }

                    string e = ScalarName(v.Element);
                    return v.Count == 1 ? e : "vec" + Inv(v.Count) + "<" + e + ">";
                }

                case MatrixType m:
                {
                    if (m.Element.IsFloatingPoint)
                    {
                        // HLSL rows are WGSL columns (see docs/wgsl-backend.md), so floatRxC is matRxC.
                        if (m.Rows == 1 || m.Columns == 1)
                        {
                            int n = m.Rows * m.Columns;
                            return n == 1 ? ScalarName(m.Element) : "vec" + Inv(n) + "<" + ScalarName(m.Element) + ">";
                        }

                        return "mat" + Inv(m.Rows) + "x" + Inv(m.Columns) + "<" + ScalarName(m.Element) + ">";
                    }

                    Unsupported("WGSL matrices hold floating-point values only; the type " + m + " cannot be expressed. Use arrays of vectors.");
                    return "mat2x2<f32>";
                }

                case ArrayType a:
                {
                    string e = TypeName(a.Element, atomic);
                    return a.Length == 0 ? "array<" + e + ">" : "array<" + e + ", " + Inv(a.Length) + ">";
                }

                case StructType st:
                    return StructName(st, atomic);
                case PointerType p:
                    return "ptr<" + SpaceName(p.Space) + ", " + TypeName(p.Pointee) + ">";
                default:
                    if (t.IsVoid)
                    {
                        return "";
                    }

                    Unsupported("The type '" + t + "' cannot be represented in WGSL.");
                    return "i32";
            }
        }
        finally
        {
            _typeDepth--;
        }
    }

    private string SpaceName(AddressSpace s)
    {
        switch (s)
        {
            case AddressSpace.Function:
                return "function";
            case AddressSpace.Private:
                return "private";
            case AddressSpace.Workgroup:
                return "workgroup";
            default:
                Unsupported("A pointer into " + s + " memory is passed to a function; the function must be inlined first (mark it [forceinline]).");
                return "function";
        }
    }

    private string StructName(StructType st, bool atomic)
    {
        if (_structNames.TryGetValue((st, atomic), out var existing))
        {
            return existing;
        }

        string baseName = WgslNames.Sanitize(st.Name, "Struct") + (atomic ? "_atomic" : "");
        string name = WgslNames.Unique(_moduleNames, baseName);
        _structNames[(st, atomic)] = name;
        if (!_fieldNames.TryGetValue(st, out var fields))
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            fields = new string[st.Fields.Count];
            for (int i = 0; i < fields.Length; i++)
            {
                fields[i] = WgslNames.Unique(used, WgslNames.Sanitize(st.Fields[i].Name, "f" + Inv(i)));
            }

            _fieldNames[st] = fields;
        }

        if (st.Fields.Count == 0)
        {
            Unsupported("The struct '" + st.Name + "' has no members; WGSL structs need at least one.");
            return name;
        }

        var sb = new StringBuilder("struct ").Append(name).Append(" {\n");
        for (int i = 0; i < st.Fields.Count; i++)
        {
            sb.Append("  ").Append(fields[i]).Append(": ").Append(TypeName(st.Fields[i].Type, atomic)).Append(",\n");
        }

        sb.Append("}\n");
        _structDecls.Add(sb.ToString());
        return name;
    }

    private string FieldName(StructType st, int index)
    {
        StructName(st, false);
        return _fieldNames[st][index];
    }

    // ---- constants ---------------------------------------------------------------------------------

    private string ConstExpr(Constant c)
    {
        switch (c.ConstantKind)
        {
            case ConstantKind.Undef:
                return TypeName(c.Type) + "()";
            case ConstantKind.Scalar:
                return ScalarConstant(c);
            default:
                return CompositeConstant(c);
        }
    }

    private string ScalarConstant(Constant c)
    {
        var st = (ScalarType)c.Type;
        switch (st.ScalarKind)
        {
            case ScalarKind.Bool:
                return c.Bits != 0 ? "true" : "false";
            case ScalarKind.Int or ScalarKind.LiteralInt:
            case ScalarKind.Min16Int:
            {
                int v = (int)(uint)c.Bits;
                if (v == int.MinValue)
                {
                    return "bitcast<i32>(0x80000000u)";
                }

                return v < 0 ? "(" + Inv(v) + "i)" : Inv(v) + "i";
            }

            case ScalarKind.UInt or ScalarKind.Min16UInt:
                return Inv((uint)c.Bits) + "u";
            case ScalarKind.Float or ScalarKind.LiteralFloat or ScalarKind.Half or ScalarKind.Min16Float or ScalarKind.Min10Float:
                ScalarName(st);
                return FloatLiteral((uint)c.Bits);
            default:
                ScalarName(st);
                Unsupported("A constant of type " + st + " cannot be written in WGSL.");
                return "0";
        }
    }

    private string FloatLiteral(uint bits)
    {
        float f = BitConverter.UInt32BitsToSingle(bits);
        if (float.IsNaN(f) || float.IsInfinity(f))
        {
            // A WGSL constant expression cannot hold a NaN or infinity, so the bits come from a private variable the compiler cannot fold.
            if (!_bitConstants.TryGetValue(bits, out var name))
            {
                name = WgslNames.Unique(_moduleNames, "const_bits_" + bits.ToString("x8", CultureInfo.InvariantCulture));
                _bitConstants[bits] = name;
                _privateConstants.Add("var<private> " + name + ": u32 = 0x" + bits.ToString("x8", CultureInfo.InvariantCulture) + "u;\n");
            }

            return "bitcast<f32>(" + name + ")";
        }

        string s = f.ToString("R", CultureInfo.InvariantCulture);
        if (s.IndexOf('.', StringComparison.Ordinal) < 0 && s.IndexOf('E', StringComparison.Ordinal) < 0)
        {
            s += ".0";
        }

        s += "f";
        return s[0] == '-' ? "(" + s + ")" : s;
    }

    private string CompositeConstant(Constant c)
    {
        var t = c.Type;
        if (t is VectorType { Count: 1 })
        {
            return ConstExpr(c.Elements[0]);
        }

        if (t is MatrixType mt && mt.Rows == 1)
        {
            return ConstExpr(c.Elements[0]);
        }

        var parts = new string[c.Elements.Count];
        bool same = parts.Length > 1;
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = ConstExpr(c.Elements[i]);
            same &= parts[i] == parts[0];
        }

        if (t is VectorType && same)
        {
            return TypeName(t) + "(" + parts[0] + ")";
        }

        return TypeName(t) + "(" + string.Join(", ", parts) + ")";
    }

    private string Zero(HlslType t) => TypeName(t) + "()";
}
