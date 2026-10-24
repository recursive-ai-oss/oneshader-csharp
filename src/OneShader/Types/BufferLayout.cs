using System;
using System.Collections.Generic;

namespace OneShader.Types;

/// <summary>Which buffer packing rules apply.</summary>
public enum BufferLayoutRules
{
    /// <summary>
    /// <c>cbuffer</c> and <c>ConstantBuffer&lt;T&gt;</c>: Vulkan std140 with the "relaxed" vector
    /// rule DXC uses by default. Arrays, matrices and structs start on 16 bytes; array elements and
    /// matrix columns/rows are 16-byte strided.
    /// </summary>
    ConstantBuffer,

    /// <summary>
    /// Structured and byte-address buffers: std430 with the same relaxed vector rule. Arrays are
    /// tightly strided and structs align to their widest member.
    /// </summary>
    StorageBuffer,
}

/// <summary>Settings that change buffer layouts.</summary>
public sealed class LayoutOptions
{
    /// <summary>Shared default: 32-bit <c>half</c>/<c>min16*</c> storage, column-major matrices.</summary>
    public static LayoutOptions Default { get; } = new();

    /// <summary>When true <c>half</c> and the <c>min16*</c> types take 2 bytes (DXC's <c>-enable-16bit-types</c>); otherwise 4.</summary>
    public bool Enable16BitTypes { get; init; }

    /// <summary>Packing for matrices with no <c>row_major</c>/<c>column_major</c> of their own; follows <c>#pragma pack_matrix</c> or the command-line default.</summary>
    public MatrixPacking DefaultMatrixPacking { get; init; } = MatrixPacking.ColumnMajor;
}

/// <summary>The computed byte layout of one type inside a buffer.</summary>
public sealed class TypeLayout
{
    internal TypeLayout(HlslType type, int size, int alignment)
    {
        Type = type;
        Size = size;
        Alignment = alignment;
    }

    /// <summary>The laid-out type.</summary>
    public HlslType Type { get; }

    /// <summary>
    /// Bytes the value occupies, including trailing padding for structs (rounded up to
    /// <see cref="Alignment"/>), arrays (count x stride; 0 when unsized) and matrices (all lines x stride).
    /// </summary>
    public int Size { get; }

    /// <summary>Alignment a member of this type is placed at (vectors may be placed more tightly, see <see cref="BufferLayoutRules"/>).</summary>
    public int Alignment { get; }

    /// <summary>For arrays: bytes between consecutive elements; otherwise 0.</summary>
    public int ArrayStride { get; internal set; }

    /// <summary>For arrays: element count (0 = unsized); otherwise 0.</summary>
    public int ArrayLength { get; internal set; }

    /// <summary>For arrays: the element layout; otherwise null.</summary>
    public TypeLayout? Element { get; internal set; }

    /// <summary>For matrices: bytes between consecutive stored vectors (columns when column-major, rows when row-major); otherwise 0.</summary>
    public int MatrixStride { get; internal set; }

    /// <summary>For matrices: how it is stored.</summary>
    public MatrixPacking MatrixPacking { get; internal set; }

    /// <summary>For structs: member layouts in declaration order; otherwise empty.</summary>
    public IReadOnlyList<MemberLayout> Members { get; internal set; } = [];

    /// <summary>True for an unsized (runtime) array.</summary>
    public bool IsUnsized => ArrayLength == 0 && Type.Kind == TypeKind.Array;
}

/// <summary>Where one struct member sits.</summary>
/// <param name="Field">The member declaration.</param>
/// <param name="Offset">Byte offset from the start of the struct.</param>
/// <param name="Layout">The member's own layout.</param>
public sealed record MemberLayout(StructField Field, int Offset, TypeLayout Layout)
{
    /// <summary>The member name.</summary>
    public string Name => Field.Name;
}

/// <summary>
/// Computes buffer layouts. The rules are the ones DXC's SPIR-V path applies by default, which the
/// repository's tests check against DXC's own output:
/// <list type="bullet">
/// <item>A scalar aligns to its size. A vector aligns to its component size, but moves to the next
/// 16-byte boundary if it would cross one ("relaxed"); vectors larger than 16 bytes (double3, double4)
/// align to 4 x component size.</item>
/// <item>Constant buffers: arrays, matrices and structs align to 16 and are padded to a multiple of
/// 16; array elements and matrix lines are 16-byte strided.</item>
/// <item>Storage buffers: a vector's standard alignment (2 x or 4 x its component size) is what arrays,
/// matrix lines and struct alignment use; array elements stride by size rounded to element alignment; a struct
/// aligns to its widest member and its size is rounded to that.</item>
/// <item>Explicit offsets (<c>packoffset</c>, <c>[[vk::offset]]</c>) are taken as given.</item>
/// </list>
/// </summary>
public static class BufferLayout
{
    /// <summary>Nesting beyond which layout gives up; real structs are far shallower, and bad input must not overflow the stack.</summary>
    private const int MaxDepth = 64;

    /// <summary>
    /// Computes the layout of <paramref name="type"/> under <paramref name="rules"/>. For a
    /// <c>cbuffer</c> pass the struct of its members; for <c>StructuredBuffer&lt;T&gt;</c> pass T
    /// (the element stride is then <see cref="TypeLayout.Size"/>).
    /// </summary>
    /// <param name="type">The type to lay out.</param>
    /// <param name="rules">The packing rules.</param>
    /// <param name="options">Layout settings; <see cref="LayoutOptions.Default"/> when null.</param>
    /// <param name="layout">The layout on success.</param>
    /// <param name="error">Why the type cannot live in a buffer (resource member, nesting too deep, unsized array not last); null on success.</param>
    /// <returns>True on success. Never throws for bad types.</returns>
    public static bool TryCompute(
        HlslType type, BufferLayoutRules rules, LayoutOptions? options, out TypeLayout? layout, out string? error)
    {
        ArgumentNullException.ThrowIfNull(type);
        var ctx = new Context(rules, options ?? LayoutOptions.Default);
        layout = Compute(ctx, type, (options ?? LayoutOptions.Default).DefaultMatrixPacking, 0);
        error = ctx.Error;
        if (error is not null)
        {
            layout = null;
            return false;
        }

        return true;
    }

    private sealed class Context(BufferLayoutRules rules, LayoutOptions options)
    {
        public BufferLayoutRules Rules { get; } = rules;

        public LayoutOptions Options { get; } = options;

        public string? Error { get; set; }
    }

    private static int AlignUp(int value, int alignment) => alignment <= 1 ? value : (value + alignment - 1) / alignment * alignment;

    /// <summary>Standard alignment of a vector of <paramref name="n"/> components of <paramref name="s"/> bytes.</summary>
    private static int VectorAlignment(int s, int n) => n == 1 ? s : n == 2 ? 2 * s : 4 * s;

    private static TypeLayout Compute(Context ctx, HlslType type, MatrixPacking packing, int depth)
    {
        bool cb = ctx.Rules == BufferLayoutRules.ConstantBuffer;
        if (depth > MaxDepth)
        {
            ctx.Error ??= "the type is nested too deeply to lay out in a buffer";
            return new TypeLayout(type, 0, 1);
        }

        switch (type)
        {
            case ScalarType sc:
            {
                int s = sc.StorageBytes(ctx.Options.Enable16BitTypes);
                return new TypeLayout(type, s, s);
            }

            case EnumType en:
            {
                int s = en.Underlying.StorageBytes(ctx.Options.Enable16BitTypes);
                return new TypeLayout(type, s, s);
            }

            case VectorType v:
            {
                int s = v.Element.StorageBytes(ctx.Options.Enable16BitTypes);
                return new TypeLayout(type, s * v.Count, VectorAlignment(s, v.Count));
            }

            case MatrixType m:
            {
                int s = m.Element.StorageBytes(ctx.Options.Enable16BitTypes);
                bool rowMajor = packing == MatrixPacking.RowMajor;
                int lines = rowMajor ? m.Rows : m.Columns;
                int lineLength = rowMajor ? m.Columns : m.Rows;
                int lineAlign = VectorAlignment(s, lineLength);
                int stride = cb ? AlignUp(lineAlign, 16) : lineAlign;
                return new TypeLayout(type, lines * stride, stride)
                {
                    MatrixStride = stride,
                    MatrixPacking = packing,
                };
            }

            case ArrayType a:
            {
                var element = Compute(ctx, a.Element, packing, depth + 1);
                int align = cb ? Math.Max(16, element.Alignment) : element.Alignment;
                int stride = AlignUp(element.Size, cb ? Math.Max(16, element.Alignment) : element.Alignment);
                return new TypeLayout(type, stride * a.Length, align)
                {
                    ArrayStride = stride,
                    ArrayLength = a.Length,
                    Element = element,
                };
            }

            case StructType st:
                return ComputeStruct(ctx, st, packing, depth);

            case ErrorType:
                return new TypeLayout(type, 0, 1);

            default:
                ctx.Error ??= "'" + type + "' cannot be stored in a buffer; use it as a separate resource instead";
                return new TypeLayout(type, 0, 1);
        }
    }

    private static TypeLayout ComputeStruct(Context ctx, StructType st, MatrixPacking packing, int depth)
    {
        bool cb = ctx.Rules == BufferLayoutRules.ConstantBuffer;
        int alignment = cb ? 16 : 1;
        int cursor = 0;
        int end = 0;
        var members = new List<MemberLayout>(st.Fields.Count);
        for (int i = 0; i < st.Fields.Count; i++)
        {
            var field = st.Fields[i];
            var fl = Compute(ctx, field.Type, field.MatrixPacking ?? packing, depth + 1);
            if (fl.IsUnsized && i != st.Fields.Count - 1)
            {
                ctx.Error ??= "an unsized array must be the last member of '" + st.Name + "'";
            }

            int offset = field.ExplicitOffset ?? Place(cursor, fl);
            members.Add(new MemberLayout(field, offset, fl));
            cursor = offset + fl.Size;
            end = Math.Max(end, cursor);
            alignment = Math.Max(alignment, fl.Alignment);
        }

        return new TypeLayout(st, AlignUp(end, alignment), alignment) { Members = members };
    }

    /// <summary>The offset at which a value of this layout starts when the previous member ended at <paramref name="cursor"/>.</summary>
    private static int Place(int cursor, TypeLayout l)
    {
        if (l.Type is VectorType v)
        {
            // The relaxed rule: a vector needs only its component alignment, as long as it does not
            // cross a 16-byte boundary (this is how HLSL packs "float a; float3 b;" into one register).
            int s = l.Size / v.Count;
            int o = AlignUp(cursor, s);
            if (l.Size <= 16)
            {
                return o % 16 + l.Size > 16 ? AlignUp(o, 16) : o;
            }

            return AlignUp(o, l.Alignment);
        }

        return AlignUp(cursor, l.Alignment);
    }
}
