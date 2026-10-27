using System;

namespace OneShader.Types;

/// <summary>The non-32-bit scalar widths a type is built from.</summary>
[Flags]
internal enum WideTypes
{
    /// <summary>Only 32-bit scalars, bool and resources.</summary>
    None = 0,

    /// <summary>16-bit floating point.</summary>
    Float16 = 1,

    /// <summary>16-bit integers.</summary>
    Int16 = 2,

    /// <summary>64-bit floating point.</summary>
    Float64 = 4,

    /// <summary>64-bit integers.</summary>
    Int64 = 8,

    /// <summary>Either 16-bit kind.</summary>
    Bits16 = Float16 | Int16,
}

/// <summary>Finds which non-32-bit scalar widths a type contains.</summary>
internal static class TypeFeatures
{
    private const int MaxDepth = 64;

    /// <summary>The widths used anywhere inside <paramref name="type"/> (struct members, array elements, vector and matrix components).</summary>
    /// <param name="type">The type to inspect.</param>
    /// <param name="enable16BitTypes">Whether <c>half</c> and <c>min16*</c> are 16-bit in this compilation.</param>
    public static WideTypes Of(HlslType type, bool enable16BitTypes) => Of(type, enable16BitTypes, 0);

    private static WideTypes Of(HlslType type, bool enable16, int depth)
    {
        if (depth > MaxDepth)
        {
            return WideTypes.None;
        }

        switch (type)
        {
            case ScalarType s:
                return OfScalar(s, enable16);
            case VectorType v:
                return OfScalar(v.Element, enable16);
            case MatrixType m:
                return OfScalar(m.Element, enable16);
            case ArrayType a:
                return Of(a.Element, enable16, depth + 1);
            case PointerType p:
                return Of(p.Pointee, enable16, depth + 1);
            case EnumType e:
                return OfScalar(e.Underlying, enable16);
            case StructType st:
            {
                var all = WideTypes.None;
                foreach (var f in st.Fields)
                {
                    all |= Of(f.Type, enable16, depth + 1);
                }

                return all;
            }

            default:
                return WideTypes.None;
        }
    }

    /// <summary>The width class of one scalar.</summary>
    public static WideTypes OfScalar(ScalarType s, bool enable16BitTypes) => s.ScalarKind switch
    {
        ScalarKind.Int16 or ScalarKind.UInt16 => WideTypes.Int16,
        ScalarKind.Int64 or ScalarKind.UInt64 => WideTypes.Int64,
        ScalarKind.Double => WideTypes.Float64,
        ScalarKind.Half or ScalarKind.Min16Float or ScalarKind.Min10Float => enable16BitTypes ? WideTypes.Float16 : WideTypes.None,
        ScalarKind.Min16Int or ScalarKind.Min16UInt => enable16BitTypes ? WideTypes.Int16 : WideTypes.None,
        _ => WideTypes.None,
    };
}
