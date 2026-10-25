using System;
using System.Collections.Generic;
using System.Text;
using OneShader.Diagnostics;
using OneShader.IR;
using OneShader.Types;

namespace OneShader.Wgsl;

internal sealed partial class WgslEmitter
{
    // WGSL cannot express the byte layout of an HLSL buffer with its own struct and array rules (a float3 followed
    // by a float, 4-byte array strides in storage, matrices stored by row), so buffers are declared as raw 32-bit
    // words and every access computes its byte offset from the reflected layout. Uniform buffers are
    // array<vec4<u32>, N> (WGSL 13.4.1 gives uniform arrays a 16-byte stride, which is exactly one row per vec4),
    // storage buffers are array<u32> (or array<atomic<u32>> when the shader uses atomics on them).

    private abstract class Mem(HlslType type)
    {
        /// <summary>The type of the value the memory holds, as the IR sees it.</summary>
        public HlslType Type { get; } = type;
    }

    private readonly record struct PlainStep(string? Field, IrValue? Index);

    private sealed class PlainMem(HlslType type, string root, GlobalVariable? atomicRoot, IReadOnlyList<PlainStep>? steps = null) : Mem(type)
    {
        public string Root { get; } = root;

        public GlobalVariable? AtomicRoot { get; } = atomicRoot;

        public IReadOnlyList<PlainStep> Steps { get; } = steps ?? [];
    }

    private sealed class BufInfo(GlobalVariable g, ResourceType rt, string name)
    {
        public GlobalVariable Global { get; } = g;

        public ResourceType Resource { get; } = rt;

        public string Name { get; } = name;

        public bool Uniform { get; init; }

        public bool ByteAddress { get; init; }

        public bool Atomic { get; set; }

        public bool Written { get; set; }

        public TypeLayout? Element { get; init; }

        public int Stride { get; init; }
    }

    /// <summary>A position inside a buffer: the type there, its layout, the constant byte offset and the dynamic index terms.</summary>
    private sealed class Cur(HlslType type, TypeLayout? layout, int offset, IReadOnlyList<(IrValue Index, int Stride)> dyn, int compStride)
    {
        public HlslType Type { get; } = type;

        public TypeLayout? Layout { get; } = layout;

        public int Offset { get; } = offset;

        public IReadOnlyList<(IrValue Index, int Stride)> Dyn { get; } = dyn;

        /// <summary>For vectors: bytes between consecutive components (a matrix row of a column-major matrix is strided).</summary>
        public int CompStride { get; } = compStride;
    }

    private sealed class BufMem(HlslType type, BufInfo info, Cur cursor, bool needsIndex) : Mem(type)
    {
        public BufInfo Info { get; } = info;

        public Cur Cursor { get; } = cursor;

        /// <summary>True until the first index of the chain has chosen the element (structured buffer) or word (byte-address buffer).</summary>
        public bool NeedsIndex { get; } = needsIndex;
    }

    private readonly Dictionary<GlobalVariable, BufInfo> _bufs = [];
    private readonly Dictionary<GlobalVariable, Mem> _globalMems = [];

    private int ElemBytes(ScalarType s)
    {
        int b = s.StorageBytes(_m.LayoutOptions.Enable16BitTypes);
        if (b != 4)
        {
            Unsupported("A " + s + " stored in a buffer is " + Inv(b) + " bytes wide; the WGSL backend reads buffers in 32-bit words.");
            return 4;
        }

        return b;
    }

    private Cur MakeCur(HlslType t, TypeLayout? layout, int offset, IReadOnlyList<(IrValue, int)> dyn)
    {
        int comp = t is VectorType v ? ElemBytes(v.Element) : 0;
        return new Cur(t, layout, offset, dyn, comp);
    }

    private Mem? MemOf(IrValue v)
    {
        switch (v)
        {
            case GlobalVariable g:
                return GlobalMem(g);
            default:
                return _mems.TryGetValue(v, out var m) ? m : null;
        }
    }

    private Mem? GlobalMem(GlobalVariable g)
    {
        if (_globalMems.TryGetValue(g, out var cached))
        {
            return cached;
        }

        Mem? m = null;
        if (_bufs.TryGetValue(g, out var info))
        {
            var rt = info.Resource;
            if (info.ByteAddress)
            {
                m = new BufMem(ScalarType.UInt, info, MakeCur(ScalarType.UInt, null, 0, []), true);
            }
            else if (info.Uniform)
            {
                m = new BufMem(rt.Element!, info, MakeCur(rt.Element!, info.Element, 0, []), false);
            }
            else
            {
                m = new BufMem(rt.Element!, info, MakeCur(rt.Element!, info.Element, 0, []), true);
            }
        }
        else if (_globalNames.TryGetValue(g, out var name) && g.GlobalKind is GlobalKind.Private or GlobalKind.Workgroup or GlobalKind.Input or GlobalKind.Output)
        {
            m = new PlainMem(g.ValueType, name, _atomicWorkgroup.Contains(g) ? g : null);
        }

        if (m is not null)
        {
            _globalMems[g] = m;
        }

        return m;
    }

    /// <summary>
    /// The type reached by indexing <paramref name="cur"/>, and whether the index is kept: single-component vectors and
    /// one-row matrices are plain scalars or vectors here (as in every backend), so their index disappears.
    /// </summary>
    private bool StepType(HlslType cur, IrValue index, out HlslType next, out int field)
    {
        field = -1;
        switch (cur)
        {
            case StructType st:
                if (index is Constant { ConstantKind: ConstantKind.Scalar } c && (int)c.Bits >= 0 && (int)c.Bits < st.Fields.Count)
                {
                    field = (int)c.Bits;
                    next = st.Fields[field].Type;
                    return true;
                }

                Fail(WgslCodes.MalformedIr, "A struct member index must be a constant inside the struct.");
                next = HlslType.Error;
                return false;
            case ArrayType a:
                next = a.Element;
                return true;
            case VectorType v:
                next = v.Element;
                return v.Count != 1;
            case MatrixType m:
                next = HlslType.Vector(m.Element, m.Columns);
                return m.Rows != 1;
            default:
                Fail(WgslCodes.MalformedIr, "An index is applied to a value that cannot be indexed.");
                next = HlslType.Error;
                return false;
        }
    }

    private Mem? ChainMem(Instruction i)
    {
        var baseValue = i.Operands[0];
        var baseMem = MemOf(baseValue);
        if (baseMem is null)
        {
            Fail(WgslCodes.MalformedIr, "An address is computed from something that is not a variable or buffer.", i.Span);
            return null;
        }

        if (baseValue is GlobalVariable { ResourceCount: not 1 })
        {
            Unsupported("Resource arrays are not available in WGSL (WebGPU has no binding arrays); declare separate resources or use a texture array.", i.Span);
            return null;
        }

        Mem current = baseMem;
        for (int k = 1; k < i.Operands.Count; k++)
        {
            var index = i.Operands[k];
            current = current switch
            {
                PlainMem p => StepPlain(p, index),
                BufMem b => StepBuf(b, index),
                _ => current,
            };
        }

        return current;
    }

    private PlainMem StepPlain(PlainMem m, IrValue index)
    {
        bool keep = StepType(m.Type, index, out var next, out int field);
        if (!keep)
        {
            return new PlainMem(next, m.Root, m.AtomicRoot, m.Steps);
        }

        var steps = new List<PlainStep>(m.Steps);
        if (field >= 0)
        {
            steps.Add(new PlainStep(FieldName((StructType)m.Type, field), null));
        }
        else
        {
            steps.Add(new PlainStep(null, ClampIndex(index, m.Type)));
        }

        return new PlainMem(next, m.Root, m.AtomicRoot, steps);
    }

    /// <summary>
    /// WGSL rejects a constant index outside a fixed-size array, vector or matrix when the shader is created, while HLSL leaves it
    /// undefined. Clamp such a constant to the valid range, which is what WGSL itself does for a run-time index.
    /// </summary>
    private static IrValue ClampIndex(IrValue index, HlslType container)
    {
        int length = container switch
        {
            ArrayType { Length: > 0 } a => a.Length,
            VectorType v => v.Count,
            MatrixType m => m.Rows,
            _ => 0,
        };
        if (length == 0 || index is not Constant { ConstantKind: ConstantKind.Scalar } c || c.Type is not ScalarType st)
        {
            return index;
        }

        bool negative = st == ScalarType.Int && (int)c.Bits < 0;
        if (!negative && (uint)c.Bits < (uint)length)
        {
            return index;
        }

        uint clamped = negative ? 0u : (uint)(length - 1);
        return Constant.FromBits(st, clamped);
    }

    private string Render(PlainMem m)
    {
        var sb = new StringBuilder(m.Root);
        foreach (var s in m.Steps)
        {
            if (s.Field is not null)
            {
                sb.Append('.').Append(s.Field);
            }
            else
            {
                sb.Append('[').Append(Val(s.Index!)).Append(']');
            }
        }

        return sb.ToString();
    }

    // ---- buffer cursors --------------------------------------------------------------------------------

    private static int ConstIndex(IrValue v) => v is Constant { ConstantKind: ConstantKind.Scalar } c ? (int)c.Bits : int.MinValue;

    private static Cur AddIndex(Cur c, HlslType type, TypeLayout? layout, IrValue index, int stride, int extraOffset, int compStride)
    {
        int k = ConstIndex(index);
        long folded = (long)c.Offset + extraOffset + (long)k * stride;
        if (k != int.MinValue)
        {
            // A negative or enormous constant index is out of bounds, and the byte offset would overflow. Pin it to a fixed offset far
            // past any buffer so that the access stays out of bounds (WGSL discards or clamps it) and the generated text stays valid.
            return new Cur(type, layout, folded is >= 0 and < (1 << 30) ? (int)folded : (1 << 30) - 16, c.Dyn, compStride);
        }

        var dyn = new List<(IrValue, int)>(c.Dyn) { (index, stride) };
        return new Cur(type, layout, c.Offset + extraOffset, dyn, compStride);
    }

    private BufMem StepBuf(BufMem m, IrValue index)
    {
        var info = m.Info;
        var c = m.Cursor;
        if (m.NeedsIndex)
        {
            // The first index picks the element of a structured buffer, or the 32-bit word of a byte-address buffer.
            int stride = info.ByteAddress ? 4 : info.Stride;
            var next = AddIndex(c, c.Type, c.Layout, index, stride, 0, c.CompStride);
            return new BufMem(next.Type, info, next, false);
        }

        var result = StepCursor(c, index);
        return new BufMem(result.Type, info, result, false);
    }

    private Cur StepCursor(Cur c, IrValue index)
    {
        switch (c.Type)
        {
            case StructType st:
            {
                int k = ConstIndex(index);
                if (k < 0 || k >= st.Fields.Count || c.Layout is null || k >= c.Layout.Members.Count)
                {
                    Fail(WgslCodes.MalformedIr, "A struct member index must be a constant inside the struct.");
                    return c;
                }

                var ml = c.Layout.Members[k];
                return MakeCur(st.Fields[k].Type, ml.Layout, c.Offset + ml.Offset, c.Dyn);
            }

            case ArrayType a:
            {
                if (c.Layout is null || c.Layout.Element is null)
                {
                    Fail(WgslCodes.MalformedIr, "An array in a buffer has no layout.");
                    return c;
                }

                index = ClampIndex(index, a);

                var next = AddIndex(c, a.Element, c.Layout.Element, index, c.Layout.ArrayStride, 0, 0);
                return a.Element is VectorType v ? new Cur(next.Type, next.Layout, next.Offset, next.Dyn, ElemBytes(v.Element)) : next;
            }

            case VectorType v:
            {
                if (v.Count == 1)
                {
                    return new Cur(v.Element, null, c.Offset, c.Dyn, 0);
                }

                return AddIndex(c, v.Element, null, ClampIndex(index, v), c.CompStride, 0, 0);
            }

            case MatrixType m:
            {
                var row = HlslType.Vector(m.Element, m.Columns);
                return MatrixRow(c, m, row, m.Rows == 1 ? null : ClampIndex(index, m));
            }

            default:
                Fail(WgslCodes.MalformedIr, "An index is applied to a value that cannot be indexed.");
                return c;
        }
    }

    /// <summary>
    /// Row <paramref name="index"/> of an HLSL matrix in a buffer (row 0 when null). A column-major matrix stores
    /// columns, so a row's components are one matrix stride apart; a row-major matrix stores rows contiguously.
    /// </summary>
    private Cur MatrixRow(Cur c, MatrixType m, VectorType row, IrValue? index)
    {
        if (c.Layout is null)
        {
            Fail(WgslCodes.MalformedIr, "A matrix in a buffer has no layout.");
            return c;
        }

        int s = ElemBytes(m.Element);
        int stride = c.Layout.MatrixStride;
        bool colMajor = c.Layout.MatrixPacking == MatrixPacking.ColumnMajor;
        int comp = colMajor ? stride : s;
        int rowStep = colMajor ? s : stride;
        if (index is null)
        {
            return new Cur(row, null, c.Offset, c.Dyn, comp);
        }

        return AddIndex(c, row, null, index, rowStep, 0, comp);
    }

    private Cur Child(Cur c, int k) => StepCursor(c, Constant.Int(k));

    // ---- reading and writing buffer words ---------------------------------------------------------------

    private string U32(IrValue index)
    {
        string e = Val(index);
        var s = index.Type.ScalarElement;
        return s is not null && s.IsSignedInteger ? "u32(" + e + ")" : e;
    }

    private static string Mul(string e, int factor) => factor == 1 ? e : e + " * " + Inv(factor) + "u";

    private string WordIndexExpr(Cur c)
    {
        var parts = new List<string>();
        int constant = c.Offset / 4;
        if (constant != 0 || c.Dyn.Count == 0)
        {
            parts.Add(Inv(constant) + "u");
        }

        foreach (var (idx, stride) in c.Dyn)
        {
            parts.Add(Mul(U32(idx), stride / 4));
        }

        return string.Join(" + ", parts);
    }

    /// <summary>The slot (<c>name[w]</c>, or <c>name[row][lane]</c> for a uniform buffer) holding the word at the cursor.</summary>
    private string WordSlot(BufInfo info, Cur c)
    {
        if (c.Offset % 4 != 0)
        {
            Fail(WgslCodes.MalformedIr, "A buffer member is not 4-byte aligned.");
        }

        if (!info.Uniform)
        {
            return info.Name + "[" + WordIndexExpr(c) + "]";
        }

        bool rowsOnly = true;
        foreach (var (_, stride) in c.Dyn)
        {
            rowsOnly &= stride % 16 == 0;
        }

        if (rowsOnly)
        {
            var parts = new List<string>();
            int row = c.Offset / 16;
            if (row != 0 || c.Dyn.Count == 0)
            {
                parts.Add(Inv(row) + "u");
            }

            foreach (var (idx, stride) in c.Dyn)
            {
                parts.Add(Mul(U32(idx), stride / 16));
            }

            return info.Name + "[" + string.Join(" + ", parts) + "][" + Inv((c.Offset % 16) / 4) + "u]";
        }

        string w = WordIndexExpr(c);
        return info.Name + "[(" + w + ") / 4u][(" + w + ") % 4u]";
    }

    private string ScalarFromWord(ScalarType s, string word)
    {
        string n = ScalarName(s);
        return n switch
        {
            "f32" => "bitcast<f32>(" + word + ")",
            "i32" => "bitcast<i32>(" + word + ")",
            "bool" => "(" + word + " != 0u)",
            _ => word,
        };
    }

    private string ScalarToWord(ScalarType s, string value)
    {
        string n = ScalarName(s);
        return n switch
        {
            "f32" => "bitcast<u32>(" + value + ")",
            "i32" => "bitcast<u32>(" + value + ")",
            "bool" => "select(0u, 1u, " + value + ")",
            _ => value,
        };
    }

    private static string LoadWord(BufInfo info, string slot) => info.Atomic ? "atomicLoad(&" + slot + ")" : slot;

    private const int MaxCopiedElements = 1024;

    private static ScalarType? LeafScalar(HlslType t) => t switch
    {
        ScalarType s => s,
        EnumType e => e.Underlying,
        _ => null,
    };

    private string ReadBuf(BufInfo info, Cur c, int depth = 0)
    {
        if (depth > MaxDepth)
        {
            Fail(WgslCodes.MalformedIr, "A buffer type is nested too deeply to be read.");
            return "0";
        }

        if (LeafScalar(c.Type) is { } leaf)
        {
            return ScalarFromWord(leaf, LoadWord(info, WordSlot(info, c)));
        }

        switch (c.Type)
        {
            case VectorType v:
            {
                if (v.Count == 1)
                {
                    return ScalarFromWord(v.Element, LoadWord(info, WordSlot(info, c)));
                }

                if (info.Uniform && !info.Atomic && c.CompStride == 4 && v.Element.ScalarKind != ScalarKind.Bool)
                {
                    bool rowsOnly = true;
                    foreach (var (_, stride) in c.Dyn)
                    {
                        rowsOnly &= stride % 16 == 0;
                    }

                    int lane = (c.Offset % 16) / 4;
                    if (rowsOnly && lane + v.Count <= 4)
                    {
                        // The components share one vec4 row: read them as a swizzle and reinterpret the whole vector.
                        string slot = WordSlot(info, c);
                        string row = slot.Substring(0, slot.LastIndexOf('[', slot.Length - 1));
                        string swz = "xyzw".Substring(lane, v.Count);
                        string n = ScalarName(v.Element);
                        return n == "u32" ? row + "." + swz : "bitcast<vec" + Inv(v.Count) + "<" + n + ">>(" + row + "." + swz + ")";
                    }
                }

                var comps = new string[v.Count];
                for (int k = 0; k < comps.Length; k++)
                {
                    comps[k] = ScalarFromWord(v.Element, LoadWord(info, WordSlot(info, Child(c, k))));
                }

                return TypeName(v) + "(" + string.Join(", ", comps) + ")";
            }

            case MatrixType m:
            {
                var row = HlslType.Vector(m.Element, m.Columns);
                if (m.Rows == 1)
                {
                    return ReadBuf(info, MatrixRow(c, m, row, null), depth + 1);
                }

                var rows = new string[m.Rows];
                for (int r = 0; r < rows.Length; r++)
                {
                    rows[r] = ReadBuf(info, MatrixRow(c, m, row, Constant.Int(r)), depth + 1);
                }

                return TypeName(m) + "(" + string.Join(", ", rows) + ")";
            }

            case StructType st:
            {
                var members = new string[st.Fields.Count];
                for (int k = 0; k < members.Length; k++)
                {
                    members[k] = ReadBuf(info, Child(c, k), depth + 1);
                }

                return TypeName(st) + "(" + string.Join(", ", members) + ")";
            }

            case ArrayType a when a.Length > 0 && a.Length <= MaxCopiedElements:
            {
                var elements = new string[a.Length];
                for (int k = 0; k < elements.Length; k++)
                {
                    elements[k] = ReadBuf(info, Child(c, k), depth + 1);
                }

                return TypeName(a) + "(" + string.Join(", ", elements) + ")";
            }

            default:
                Unsupported("A value of type '" + c.Type + "' cannot be read from a buffer as a whole; read its members.");
                return "0";
        }
    }

    private string Simple(string expr) => IsIdentifier(expr) ? expr : Temp(expr);

    private void WriteBuf(BufInfo info, Cur c, string value, int depth = 0)
    {
        if (depth > MaxDepth)
        {
            Fail(WgslCodes.MalformedIr, "A buffer type is nested too deeply to be written.");
            return;
        }

        if (info.Uniform)
        {
            Unsupported("A constant buffer is read-only; write to a RWStructuredBuffer instead.");
            return;
        }

        if (LeafScalar(c.Type) is { } leaf)
        {
            StoreWord(info, c, ScalarToWord(leaf, value));
            return;
        }

        switch (c.Type)
        {
            case VectorType v:
            {
                if (v.Count == 1)
                {
                    StoreWord(info, c, ScalarToWord(v.Element, value));
                    return;
                }

                value = Simple(value);
                for (int k = 0; k < v.Count; k++)
                {
                    StoreWord(info, Child(c, k), ScalarToWord(v.Element, value + "." + "xyzw"[k]));
                }

                return;
            }

            case MatrixType m:
            {
                value = Simple(value);
                var row = HlslType.Vector(m.Element, m.Columns);
                if (m.Rows == 1)
                {
                    WriteBuf(info, MatrixRow(c, m, row, null), value, depth + 1);
                    return;
                }

                for (int r = 0; r < m.Rows; r++)
                {
                    WriteBuf(info, MatrixRow(c, m, row, Constant.Int(r)), value + "[" + Inv(r) + "]", depth + 1);
                }

                return;
            }

            case StructType st:
            {
                value = Simple(value);
                for (int k = 0; k < st.Fields.Count; k++)
                {
                    WriteBuf(info, Child(c, k), value + "." + FieldName(st, k), depth + 1);
                }

                return;
            }

            case ArrayType a when a.Length > 0 && a.Length <= MaxCopiedElements:
            {
                value = Simple(value);
                for (int k = 0; k < a.Length; k++)
                {
                    WriteBuf(info, Child(c, k), value + "[" + Inv(k) + "]", depth + 1);
                }

                return;
            }

            default:
                Unsupported("A value of type '" + c.Type + "' cannot be written to a buffer as a whole; write its members.");
                return;
        }
    }

    private void StoreWord(BufInfo info, Cur c, string wordValue)
    {
        string slot = WordSlot(info, c);
        info.Written = true;
        Put(info.Atomic ? "atomicStore(&" + slot + ", " + wordValue + ");" : slot + " = " + wordValue + ";");
    }

    // ---- loads and stores through pointers ----------------------------------------------------------------

    private bool HasAtomicLeaf(HlslType t) => t switch
    {
        ScalarType s => s.ScalarKind is ScalarKind.Int or ScalarKind.UInt or ScalarKind.LiteralInt or ScalarKind.Min16Int or ScalarKind.Min16UInt,
        VectorType v => v.Count > 1 && false,
        ArrayType a => HasAtomicLeaf(a.Element),
        StructType st => AnyField(st),
        _ => false,
    };

    private bool AnyField(StructType st)
    {
        foreach (var f in st.Fields)
        {
            if (HasAtomicLeaf(f.Type))
            {
                return true;
            }
        }

        return false;
    }

    private string ReadMem(Mem m)
    {
        switch (m)
        {
            case PlainMem p:
            {
                string path = Render(p);
                return p.AtomicRoot is not null && HasAtomicLeaf(p.Type) ? ExpandAtomicRead(path, p.Type, 0) : path;
            }

            case BufMem b:
                if (b.NeedsIndex)
                {
                    Fail(WgslCodes.MalformedIr, "A buffer is read without choosing an element.");
                    return "0";
                }

                return ReadBuf(b.Info, b.Cursor);
            default:
                return "0";
        }
    }

    private string ExpandAtomicRead(string path, HlslType t, int depth)
    {
        if (depth > MaxDepth)
        {
            return path;
        }

        switch (t)
        {
            case ScalarType s when HasAtomicLeaf(s):
                return "atomicLoad(&" + path + ")";
            case ArrayType a when a.Length > 0 && a.Length <= MaxCopiedElements && HasAtomicLeaf(a):
            {
                var parts = new string[a.Length];
                for (int k = 0; k < parts.Length; k++)
                {
                    parts[k] = ExpandAtomicRead(path + "[" + Inv(k) + "]", a.Element, depth + 1);
                }

                return TypeName(a) + "(" + string.Join(", ", parts) + ")";
            }

            case StructType st when HasAtomicLeaf(st):
            {
                var parts = new string[st.Fields.Count];
                for (int k = 0; k < parts.Length; k++)
                {
                    parts[k] = ExpandAtomicRead(path + "." + FieldName(st, k), st.Fields[k].Type, depth + 1);
                }

                return TypeName(st) + "(" + string.Join(", ", parts) + ")";
            }

            default:
                return path;
        }
    }

    private void WriteMem(Mem m, string value)
    {
        switch (m)
        {
            case PlainMem p:
            {
                string path = Render(p);
                if (p.AtomicRoot is not null && HasAtomicLeaf(p.Type))
                {
                    ExpandAtomicWrite(path, Simple(value), p.Type, 0);
                }
                else
                {
                    Put(path + " = " + value + ";");
                }

                break;
            }

            case BufMem b:
                if (b.NeedsIndex)
                {
                    Fail(WgslCodes.MalformedIr, "A buffer is written without choosing an element.");
                    return;
                }

                WriteBuf(b.Info, b.Cursor, value);
                break;
            default:
                break;
        }
    }

    private void ExpandAtomicWrite(string path, string value, HlslType t, int depth)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        switch (t)
        {
            case ScalarType s when HasAtomicLeaf(s):
                Put("atomicStore(&" + path + ", " + value + ");");
                break;
            case ArrayType a when a.Length > 0 && a.Length <= MaxCopiedElements:
                for (int k = 0; k < a.Length; k++)
                {
                    ExpandAtomicWrite(path + "[" + Inv(k) + "]", value + "[" + Inv(k) + "]", a.Element, depth + 1);
                }

                break;
            case StructType st:
                for (int k = 0; k < st.Fields.Count; k++)
                {
                    ExpandAtomicWrite(path + "." + FieldName(st, k), value + "." + FieldName(st, k), st.Fields[k].Type, depth + 1);
                }

                break;
            default:
                Put(path + " = " + value + ";");
                break;
        }
    }

    /// <summary>The address of the 32-bit target of an atomic operation (<c>&amp;slot</c>), or null after reporting why not.</summary>
    private string? AtomicAddress(IrValue pointer)
    {
        switch (MemOf(pointer))
        {
            case BufMem { NeedsIndex: false } b when LeafScalar(b.Cursor.Type) is not null || b.Cursor.Type is VectorType { Count: 1 }:
                b.Info.Written = true;
                return "&" + WordSlot(b.Info, b.Cursor);
            case PlainMem { AtomicRoot: not null } p when HasAtomicLeaf(p.Type):
                return "&" + Render(p);
            default:
                Unsupported("This atomic operation does not address a 32-bit integer in a buffer or groupshared variable.");
                return null;
        }
    }
}
