using System.Collections.Generic;
using System.Globalization;
using System.Text;
using OneShader.Diagnostics;
using OneShader.IR;
using OneShader.Semantics;
using OneShader.Reflection;
using OneShader.Types;

namespace OneShader.SpirV;

internal sealed partial class SpirvEmitter
{
    private readonly Dictionary<HlslType, uint> _plain = [];
    private readonly Dictionary<string, uint> _constants = new(System.StringComparer.Ordinal);
    private readonly Dictionary<GlobalVariable, ResourceType> _resourceOf = [];
    private int _depth;

    private static string Inv(uint v) => v.ToString(CultureInfo.InvariantCulture);

    // ---- plain (register) types -------------------------------------------------------------

    private uint TypeId(HlslType t)
    {
        if (_plain.TryGetValue(t, out var cached))
        {
            return cached;
        }

        if (_depth > MaxDepth)
        {
            Fail(SpirvCodes.MalformedIr, "A type is nested too deeply to be emitted; flatten the declaration.");
            return _s.DeclType("void", Op.TypeVoid);
        }

        _depth++;
        uint id = CreateType(t);
        _depth--;
        _plain[t] = id;
        return id;
    }

    private uint CreateType(HlslType t)
    {
        switch (t)
        {
            case ScalarType s:
                return ScalarTypeId(s);
            case VectorType v:
            {
                uint e = TypeId(v.Element);
                if (v.Count == 1)
                {
                    return e;
                }

                return VectorOf(e, v.Count);
            }

            case MatrixType m:
            {
                if (m.Element.ScalarKind == ScalarKind.Bool)
                {
                    Unsupported("Matrices of bool cannot be represented in SPIR-V; use an int or float matrix.");
                }

                uint e = TypeId(m.Element);
                if (m.Rows == 1 || m.Columns == 1)
                {
                    int n = m.Rows * m.Columns;
                    return n == 1 ? e : VectorOf(e, n);
                }

                // HLSL matrices map transposed: one SPIR-V column per HLSL row, so rows extract as columns.
                uint col = VectorOf(e, m.Columns);
                uint id = _s.DeclType("m" + Inv(col) + "x" + Inv((uint)m.Rows), Op.TypeMatrix, col, (uint)m.Rows);
                _s.SetInfo(id, TypeInfoKind.Matrix, col);
                return id;
            }

            case ArrayType a:
            {
                uint e = TypeId(a.Element);
                if (a.Length == 0)
                {
                    uint r = _s.DeclType("ra" + Inv(e), Op.TypeRuntimeArray, e);
                    _s.SetInfo(r, TypeInfoKind.Array, e);
                    return r;
                }

                uint len = ConstId(Constant.UInt((uint)a.Length));
                uint id = _s.DeclType("a" + Inv(e) + "x" + Inv(len), Op.TypeArray, e, len);
                _s.SetInfo(id, TypeInfoKind.Array, e);
                return id;
            }

            case StructType st:
            {
                var kids = new uint[st.Fields.Count];
                for (int i = 0; i < kids.Length; i++)
                {
                    kids[i] = TypeId(st.Fields[i].Type);
                }

                uint id = _s.NewId();
                var ops = new uint[kids.Length + 1];
                ops[0] = id;
                kids.CopyTo(ops, 1);
                SpirvModule.Put(_s.Types, Op.TypeStruct, ops);
                _s.SetInfo(id, TypeInfoKind.Struct, kids);
                if (_o.KeepNames)
                {
                    if (st.Name.Length > 0)
                    {
                        _s.Name(id, st.Name);
                    }

                    for (int i = 0; i < kids.Length; i++)
                    {
                        _s.MemberName(id, (uint)i, st.Fields[i].Name);
                    }
                }

                return id;
            }

            case EnumType e:
                return TypeId(e.Underlying);
            case PointerType p:
                return PointerTo(StorageClassOf(p.Space), TypeId(p.Pointee));
            case ResourceType r:
                return ImageTypeId(r);
            case SamplerType:
                return _s.DeclType("sampler", Op.TypeSampler);
            default:
                if (t.IsVoid)
                {
                    return _s.DeclType("void", Op.TypeVoid);
                }

                Unsupported("The type '" + t + "' cannot be represented in SPIR-V.");
                return _s.DeclType("void", Op.TypeVoid);
        }
    }

    private uint VectorOf(uint element, int count)
    {
        uint id = _s.DeclType("v" + Inv(element) + "x" + Inv((uint)count), Op.TypeVector, element, (uint)count);
        _s.SetInfo(id, TypeInfoKind.Vector, element);
        return id;
    }

    private uint PointerTo(uint storageClass, uint pointee) =>
        _s.DeclType("p" + Inv(storageClass) + ":" + Inv(pointee), Op.TypePointer, storageClass, pointee);

    private static uint StorageClassOf(AddressSpace s) => s switch
    {
        AddressSpace.Function => Sc.Function,
        AddressSpace.Private => Sc.Private,
        AddressSpace.Workgroup => Sc.Workgroup,
        AddressSpace.Uniform => Sc.Uniform,
        AddressSpace.StorageBuffer => Sc.StorageBuffer,
        AddressSpace.Input => Sc.Input,
        AddressSpace.Output => Sc.Output,
        _ => Sc.UniformConstant,
    };

    private uint ScalarTypeId(ScalarType s)
    {
        switch (s.ScalarKind)
        {
            case ScalarKind.Bool:
                return _s.DeclType("bool", Op.TypeBool);
            case ScalarKind.Double:
                _s.Capabilities.Add(Cap.Float64);
                return _s.DeclType("f64", Op.TypeFloat, 64);
            case ScalarKind.Half or ScalarKind.Min16Float or ScalarKind.Min10Float:
                if (_m.LayoutOptions.Enable16BitTypes)
                {
                    Unsupported("16-bit floating point types are not supported by the SPIR-V backend yet; compile without 16-bit types.");
                }

                return _s.DeclType("f32", Op.TypeFloat, 32);
            case ScalarKind.Float or ScalarKind.LiteralFloat:
                return _s.DeclType("f32", Op.TypeFloat, 32);
            case ScalarKind.Int16:
                _s.Capabilities.Add(Cap.Int16);
                return _s.DeclType("i16", Op.TypeInt, 16, 1);
            case ScalarKind.UInt16:
                _s.Capabilities.Add(Cap.Int16);
                return _s.DeclType("u16", Op.TypeInt, 16, 0);
            case ScalarKind.Min16Int when _m.LayoutOptions.Enable16BitTypes:
                _s.Capabilities.Add(Cap.Int16);
                return _s.DeclType("i16", Op.TypeInt, 16, 1);
            case ScalarKind.Min16UInt when _m.LayoutOptions.Enable16BitTypes:
                _s.Capabilities.Add(Cap.Int16);
                return _s.DeclType("u16", Op.TypeInt, 16, 0);
            case ScalarKind.Int64:
                _s.Capabilities.Add(Cap.Int64);
                return _s.DeclType("i64", Op.TypeInt, 64, 1);
            case ScalarKind.UInt64:
                _s.Capabilities.Add(Cap.Int64);
                return _s.DeclType("u64", Op.TypeInt, 64, 0);
            case ScalarKind.UInt or ScalarKind.Min16UInt:
                return _s.DeclType("u32", Op.TypeInt, 32, 0);
            default:
                return _s.DeclType("i32", Op.TypeInt, 32, 1);
        }
    }

    // ---- constants --------------------------------------------------------------------------

    private uint ConstId(Constant c)
    {
        uint type = TypeId(c.Type);
        switch (c.ConstantKind)
        {
            case ConstantKind.Undef:
            {
                string key = "undef:" + Inv(type);
                if (_constants.TryGetValue(key, out var u))
                {
                    return u;
                }

                u = _s.NewId();
                SpirvModule.Put(_s.Types, Op.Undef, type, u);
                _constants[key] = u;
                return u;
            }

            case ConstantKind.Scalar:
                return ScalarConst(c, type);
            default:
                return CompositeConst(c, type);
        }
    }

    private uint ScalarConst(Constant c, uint type)
    {
        var st = (ScalarType)c.Type;
        string key = "c:" + Inv(type) + ":" + c.Bits.ToString(CultureInfo.InvariantCulture);
        if (_constants.TryGetValue(key, out var id))
        {
            return id;
        }

        id = _s.NewId();
        if (st.ScalarKind == ScalarKind.Bool)
        {
            SpirvModule.Put(_s.Types, c.Bits != 0 ? Op.ConstantTrue : Op.ConstantFalse, type, id);
        }
        else if (st.StorageBytes(true) == 8 && st.ScalarKind is ScalarKind.Double or ScalarKind.Int64 or ScalarKind.UInt64)
        {
            SpirvModule.Put(_s.Types, Op.Constant, type, id, (uint)c.Bits, (uint)(c.Bits >> 32));
        }
        else if (st.ScalarKind is ScalarKind.Int16)
        {
            SpirvModule.Put(_s.Types, Op.Constant, type, id, (uint)(int)(short)c.Bits);
        }
        else
        {
            SpirvModule.Put(_s.Types, Op.Constant, type, id, (uint)c.Bits);
        }

        _constants[key] = id;
        return id;
    }

    private uint CompositeConst(Constant c, uint type)
    {
        // Single-element vectors and degenerate matrices share their SPIR-V type with a scalar/vector.
        if (c.Type is VectorType { Count: 1 })
        {
            return ConstId(c.Elements[0]);
        }

        if (c.Type is MatrixType m)
        {
            if (m.Rows == 1)
            {
                return ConstId(c.Elements[0]);
            }

            if (m.Columns == 1)
            {
                var parts = new uint[c.Elements.Count];
                for (int i = 0; i < parts.Length; i++)
                {
                    parts[i] = ConstId(c.Elements[i]);
                }

                return CompositeOf(type, parts);
            }
        }

        var ids = new uint[c.Elements.Count];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = ConstId(c.Elements[i]);
        }

        return CompositeOf(type, ids);
    }

    private uint CompositeOf(uint type, uint[] parts)
    {
        var sb = new StringBuilder("cc:").Append(Inv(type));
        foreach (var p in parts)
        {
            sb.Append(':').Append(Inv(p));
        }

        string key = sb.ToString();
        if (_constants.TryGetValue(key, out var id))
        {
            return id;
        }

        id = _s.NewId();
        var ops = new uint[parts.Length + 2];
        ops[0] = type;
        ops[1] = id;
        parts.CopyTo(ops, 2);
        SpirvModule.Put(_s.Types, Op.ConstantComposite, ops);
        _constants[key] = id;
        return id;
    }

    private uint Zero(HlslType t) => ConstId(Constant.Zero(t));

    // ---- images and samplers ------------------------------------------------------------------

    private readonly HashSet<uint> _formattedImages = [];

    private uint ImageTypeId(ResourceType r, int format = 0)
    {
        if (r.ResourceKind is ResourceTypeKind.ConstantBuffer || r.IsStorageBuffer)
        {
            Unsupported("A buffer resource of type '" + r + "' cannot be used as a texture operand.");
            return _s.DeclType("void", Op.TypeVoid);
        }

        var elem = r.Element?.ScalarElement ?? ScalarType.Float;
        var sampledScalar = elem.IsFloatingPoint || elem.ScalarKind == ScalarKind.Bool ? ScalarType.Float : elem.IsSignedInteger ? ScalarType.Int : ScalarType.UInt;
        uint sampled = TypeId(sampledScalar);
        bool rw = r.IsReadWrite;
        uint dim;
        uint arrayed = 0;
        uint ms = 0;
        switch (r.ResourceKind)
        {
            case ResourceTypeKind.Texture1D or ResourceTypeKind.RWTexture1D:
                dim = 0;
                _s.Capabilities.Add(rw ? Cap.Image1D : Cap.Sampled1D);
                break;
            case ResourceTypeKind.Texture1DArray or ResourceTypeKind.RWTexture1DArray:
                dim = 0;
                arrayed = 1;
                _s.Capabilities.Add(rw ? Cap.Image1D : Cap.Sampled1D);
                break;
            case ResourceTypeKind.Texture2D or ResourceTypeKind.RWTexture2D:
                dim = 1;
                break;
            case ResourceTypeKind.Texture2DArray or ResourceTypeKind.RWTexture2DArray:
                dim = 1;
                arrayed = 1;
                break;
            case ResourceTypeKind.Texture2DMS:
                dim = 1;
                ms = 1;
                break;
            case ResourceTypeKind.Texture2DMSArray:
                dim = 1;
                ms = 1;
                arrayed = 1;
                _s.Capabilities.Add(Cap.ImageMSArray);
                break;
            case ResourceTypeKind.Texture3D or ResourceTypeKind.RWTexture3D:
                dim = 2;
                break;
            case ResourceTypeKind.TextureCube:
                dim = 3;
                break;
            case ResourceTypeKind.TextureCubeArray:
                dim = 3;
                arrayed = 1;
                _s.Capabilities.Add(Cap.ImageCubeArray);
                break;
            default: // Buffer, RWBuffer
                dim = 5;
                _s.Capabilities.Add(rw ? Cap.ImageBuffer : Cap.SampledBuffer);
                break;
        }

        if (format != 0)
        {
            // Vulkan: only a handful of formats are usable without the extended-format capability.
            if (!ImageFormats.IsCore(format))
            {
                _s.Capabilities.Add(Cap.StorageImageExtendedFormat);
            }
        }

        string key = "img:" + Inv(sampled) + ":" + Inv(dim) + ":" + Inv(arrayed) + ":" + Inv(ms) + ":" + (rw ? "2" : "1") + (format != 0 ? ":f" + Inv((uint)format) : "");
        uint image = _s.DeclType(key, Op.TypeImage, sampled, dim, 0, arrayed, ms, rw ? 2u : 1u, (uint)format);
        if (format != 0)
        {
            _formattedImages.Add(image);
        }

        return image;
    }

    private uint SampledImageTypeId(uint image) => _s.DeclType("simg:" + Inv(image), Op.TypeSampledImage, image);

    // ---- buffer layout types ------------------------------------------------------------------

    private static bool IsDegenerateMatrix(MatrixType m) => m.Rows == 1 || m.Columns == 1;

    /// <summary>The SPIR-V type of a value as stored in a buffer: structs carry offsets and arrays strides.</summary>
    private uint LayoutType(TypeLayout l, bool block, string? blockName)
    {
        if (_depth > MaxDepth)
        {
            Fail(SpirvCodes.MalformedIr, "A buffer type is nested too deeply to be emitted; flatten the declaration.");
            return TypeId(ScalarType.UInt);
        }

        _depth++;
        try
        {
            switch (l.Type)
            {
                case ArrayType a:
                {
                    uint e = LayoutType(l.Element!, false, null);
                    if (a.Length == 0)
                    {
                        string rk = "lra:" + Inv(e) + ":" + Inv((uint)l.ArrayStride);
                        bool isNew = !_s.TryGetKey(rk, out _);
                        uint r = _s.DeclType(rk, Op.TypeRuntimeArray, e);
                        if (isNew)
                        {
                            _s.SetInfo(r, TypeInfoKind.Array, e);
                            _s.Decorate(r, Dec.ArrayStride, (uint)l.ArrayStride);
                        }

                        return r;
                    }

                    uint len = ConstId(Constant.UInt((uint)a.Length));
                    string key = "la:" + Inv(e) + ":" + Inv(len) + ":" + Inv((uint)l.ArrayStride);
                    bool created = !_s.TryGetKey(key, out _);
                    uint id = _s.DeclType(key, Op.TypeArray, e, len);
                    if (created)
                    {
                        _s.SetInfo(id, TypeInfoKind.Array, e);
                        _s.Decorate(id, Dec.ArrayStride, (uint)l.ArrayStride);
                    }

                    return id;
                }

                case StructType st:
                {
                    var kids = new uint[l.Members.Count];
                    var sb = new StringBuilder(block ? "lb:" : "ls:").Append(blockName ?? st.Name);
                    for (int i = 0; i < kids.Length; i++)
                    {
                        kids[i] = LayoutType(l.Members[i].Layout, false, null);
                        sb.Append('|').Append(Inv(kids[i])).Append('@').Append(Inv((uint)l.Members[i].Offset))
                            .Append(MatrixSignature(l.Members[i].Layout));
                    }

                    string key = sb.ToString();
                    if (_s.TryGetKey(key, out var existing))
                    {
                        return existing;
                    }

                    uint id = _s.NewId();
                    var ops = new uint[kids.Length + 1];
                    ops[0] = id;
                    kids.CopyTo(ops, 1);
                    SpirvModule.Put(_s.Types, Op.TypeStruct, ops);
                    _s.SetKey(key, id);
                    _s.SetInfo(id, TypeInfoKind.Struct, kids);
                    if (block)
                    {
                        _s.Decorate(id, Dec.Block);
                    }

                    for (int i = 0; i < kids.Length; i++)
                    {
                        var ml = l.Members[i];
                        _s.MemberDecorate(id, (uint)i, Dec.Offset, (uint)ml.Offset);
                        DecorateMatrix(id, (uint)i, ml.Layout);
                    }

                    if (_o.KeepNames)
                    {
                        string nm = blockName ?? st.Name;
                        if (nm.Length > 0)
                        {
                            _s.Name(id, nm);
                        }

                        for (int i = 0; i < kids.Length; i++)
                        {
                            _s.MemberName(id, (uint)i, l.Members[i].Name);
                        }
                    }

                    return id;
                }

                default:
                    return StoredTypeId(l.Type);
            }
        }
        finally
        {
            _depth--;
        }
    }

    /// <summary>
    /// The type a scalar or vector has in a buffer. OpTypeBool has no defined size and is not allowed in uniform or
    /// storage memory, so bool is stored as a 32-bit unsigned integer (0 or 1), converted on load and store as DXC does.
    /// </summary>
    private uint StoredTypeId(HlslType t)
    {
        if (t.ScalarElement is { ScalarKind: ScalarKind.Bool } && t is ScalarType or VectorType)
        {
            return TypeId(TypeOps.Reshape(t, ScalarType.UInt));
        }

        return TypeId(t);
    }

    private static string MatrixSignature(TypeLayout l)
    {
        while (l.Type is ArrayType && l.Element is not null)
        {
            l = l.Element;
        }

        return l.Type is MatrixType ? "m" + l.MatrixStride.ToString(CultureInfo.InvariantCulture) + (l.MatrixPacking == MatrixPacking.RowMajor ? "r" : "c") : "";
    }

    /// <summary>
    /// Adds MatrixStride and the majorness decoration to a struct member holding a matrix (or an array of
    /// matrices). HLSL <c>column_major</c> is SPIR-V <c>RowMajor</c> because HLSL matrices map transposed.
    /// </summary>
    private void DecorateMatrix(uint structId, uint member, TypeLayout l)
    {
        while (l.Type is ArrayType && l.Element is not null)
        {
            l = l.Element;
        }

        if (l.Type is not MatrixType m)
        {
            return;
        }

        if (IsDegenerateMatrix(m))
        {
            bool contiguous = m.Rows == 1 ? l.MatrixPacking == MatrixPacking.RowMajor : l.MatrixPacking == MatrixPacking.ColumnMajor;
            if (!contiguous && m.Rows * m.Columns > 1)
            {
                Unsupported("A " + m + " matrix stored " + (l.MatrixPacking == MatrixPacking.RowMajor ? "row_major" : "column_major") +
                    " in a buffer is not contiguous and is not supported; use the other packing or a vector.");
            }

            return;
        }

        _s.MemberDecorate(structId, member, Dec.MatrixStride, (uint)l.MatrixStride);
        _s.MemberDecorate(structId, member, l.MatrixPacking == MatrixPacking.ColumnMajor ? Dec.RowMajor : Dec.ColMajor);
    }

    // ---- global variables ---------------------------------------------------------------------

    private void DeclareGlobal(GlobalVariable g)
    {
        switch (g.GlobalKind)
        {
            case GlobalKind.Input or GlobalKind.Output:
                DeclareInterface(g);
                break;
            case GlobalKind.Private:
            {
                uint t = TypeId(g.ValueType);
                uint init = g.Initializer is null ? Zero(g.ValueType) : ConstId(g.Initializer);
                NewVariable(g, PointerTo(Sc.Private, t), Sc.Private, t, init);
                break;
            }

            case GlobalKind.Workgroup:
            {
                uint t = TypeId(g.ValueType);
                NewVariable(g, PointerTo(Sc.Workgroup, t), Sc.Workgroup, t, 0);
                break;
            }

            default:
                DeclareResource(g);
                break;
        }
    }

    private uint NewVariable(GlobalVariable g, uint pointerType, uint storageClass, uint pointee, uint initializer)
    {
        uint id = _s.NewId();
        if (initializer != 0)
        {
            SpirvModule.Put(_s.Types, Op.Variable, pointerType, id, storageClass, initializer);
        }
        else
        {
            SpirvModule.Put(_s.Types, Op.Variable, pointerType, id, storageClass);
        }

        _globalIds[g] = id;
        _ptrPointee[g] = pointee;
        if (_o.KeepNames && !string.IsNullOrEmpty(g.Name))
        {
            _s.Name(id, g.Name);
        }

        return id;
    }

    private void DeclareInterface(GlobalVariable g)
    {
        bool input = g.GlobalKind == GlobalKind.Input;
        uint sc = input ? Sc.Input : Sc.Output;
        uint t = TypeId(g.ValueType);
        uint builtIn = 0;
        bool isBuiltIn = g.BuiltIn != BuiltIn.None;
        bool sampleMask = false;
        if (isBuiltIn)
        {
            switch (g.BuiltIn)
            {
                case BuiltIn.Position:
                    builtIn = Bi.Position;
                    break;
                case BuiltIn.FragCoord:
                    builtIn = Bi.FragCoord;
                    break;
                case BuiltIn.VertexIndex:
                    builtIn = Bi.VertexIndex;
                    break;
                case BuiltIn.InstanceIndex:
                    builtIn = Bi.InstanceIndex;
                    break;
                case BuiltIn.FrontFacing:
                    builtIn = Bi.FrontFacing;
                    break;
                case BuiltIn.FragDepth:
                    builtIn = Bi.FragDepth;
                    break;
                case BuiltIn.SampleIndex:
                    builtIn = Bi.SampleId;
                    _s.Capabilities.Add(Cap.SampleRateShading);
                    break;
                case BuiltIn.SampleMask:
                    builtIn = Bi.SampleMask;
                    sampleMask = true;
                    break;
                case BuiltIn.PrimitiveIndex:
                    builtIn = Bi.PrimitiveId;
                    _s.Capabilities.Add(Cap.Geometry);
                    break;
                case BuiltIn.Layer:
                    builtIn = Bi.Layer;
                    _s.Capabilities.Add(Cap.ShaderLayer);
                    break;
                case BuiltIn.ViewportIndex:
                    builtIn = Bi.ViewportIndex;
                    _s.Capabilities.Add(Cap.ShaderViewportIndex);
                    break;
                case BuiltIn.ClipDistance:
                    builtIn = Bi.ClipDistance;
                    _s.Capabilities.Add(Cap.ClipDistance);
                    break;
                case BuiltIn.CullDistance:
                    builtIn = Bi.CullDistance;
                    _s.Capabilities.Add(Cap.CullDistance);
                    break;
                case BuiltIn.GlobalInvocationId:
                    builtIn = Bi.GlobalInvocationId;
                    break;
                case BuiltIn.LocalInvocationId:
                    builtIn = Bi.LocalInvocationId;
                    break;
                case BuiltIn.WorkgroupId:
                    builtIn = Bi.WorkgroupId;
                    break;
                case BuiltIn.LocalInvocationIndex:
                    builtIn = Bi.LocalInvocationIndex;
                    break;
                default:
                    Unsupported("The system value " + (g.Semantic ?? g.BuiltIn.ToString()) + " is not supported by the SPIR-V backend yet.", g.Span);
                    return;
            }
        }

        uint varType = t;
        if (sampleMask)
        {
            // The sample mask is an array of one 32-bit integer in SPIR-V.
            uint len = ConstId(Constant.UInt(1));
            varType = _s.DeclType("a" + Inv(t) + "x" + Inv(len), Op.TypeArray, t, len);
            _s.SetInfo(varType, TypeInfoKind.Array, t);
            _sampleMasks.Add(g);
        }

        uint id = NewVariable(g, PointerTo(sc, varType), sc, varType, 0);
        if (isBuiltIn)
        {
            _s.Decorate(id, Dec.BuiltIn, builtIn);
        }
        else if (g.Location is { } loc)
        {
            _s.Decorate(id, Dec.Location, (uint)loc);
        }
        else
        {
            Fail(SpirvCodes.MalformedIr, "The stage variable '" + g.Name + "' has neither a location nor a built-in; give it a semantic.", g.Span);
        }

        var scalar = g.ValueType.ScalarElement;
        bool integerLike = scalar is not null && (scalar.IsInteger || scalar.ScalarKind == ScalarKind.Double);
        bool fragmentInput = input && _ep.Stage == ShaderStage.Pixel;
        bool vertexOutput = !input && _ep.Stage == ShaderStage.Vertex;
        if (fragmentInput || vertexOutput)
        {
            // Vulkan requires integer and double fragment inputs to be flat; the vertex output must agree.
            bool flat = g.Interpolation == Interpolation.Flat || (integerLike && (fragmentInput || !isBuiltIn));
            if (flat)
            {
                _s.Decorate(id, Dec.Flat);
            }
            else if (!isBuiltIn)
            {
                switch (g.Interpolation)
                {
                    case Interpolation.NoPerspective:
                        _s.Decorate(id, Dec.NoPerspective);
                        break;
                    case Interpolation.Centroid:
                        _s.Decorate(id, Dec.Centroid);
                        break;
                    case Interpolation.Sample:
                        _s.Decorate(id, Dec.Sample);
                        _s.Capabilities.Add(Cap.SampleRateShading);
                        break;
                    default:
                        break;
                }
            }
        }
    }

    private readonly HashSet<GlobalVariable> _sampleMasks = [];

    private void DeclareResource(GlobalVariable g)
    {
        // A resource array is a global of ArrayType whose element is the resource; ResourceCount carries the size.
        var valueType = g.ValueType is ArrayType resourceArray ? resourceArray.Element : g.ValueType;
        if (valueType is not ResourceType and not SamplerType)
        {
            Fail(SpirvCodes.MalformedIr, "The resource '" + g.Name + "' is not a texture, sampler or buffer.", g.Span);
            return;
        }

        if (g.Set is not { } set || g.Binding is not { } binding)
        {
            Fail(SpirvCodes.MissingBinding, "The resource '" + g.Name + "' has no descriptor set and binding; add [[vk::binding(b, s)]] or a register.", g.Span);
            return;
        }

        uint sc;
        uint pointee;
        bool readOnlyBuffer = false;
        if (valueType is SamplerType sampler)
        {
            sc = Sc.UniformConstant;
            pointee = TypeId(sampler);
        }
        else
        {
            var rt = (ResourceType)valueType;
            _resourceOf[g] = rt;
            if (rt.ResourceKind == ResourceTypeKind.ConstantBuffer)
            {
                sc = Sc.Uniform;
                pointee = ConstantBlock(g, rt);
            }
            else if (rt.IsStorageBuffer)
            {
                sc = Sc.StorageBuffer;
                readOnlyBuffer = !rt.IsReadWrite;
                pointee = StorageBlock(g, rt);
            }
            else
            {
                sc = Sc.UniformConstant;
                pointee = g.ImageFormat is { } fmtName && rt.IsReadWrite && rt.IsTexture ? ImageTypeId(rt, ImageFormats.Find(fmtName)) : TypeId(rt);
            }
        }

        if (g.ResourceCount != 1)
        {
            if (g.ResourceCount == 0)
            {
                Unsupported("Unbounded resource arrays are not supported yet; give '" + g.Name + "' a size.", g.Span);
                return;
            }

            uint len = ConstId(Constant.UInt((uint)g.ResourceCount));
            uint elementType = pointee;
            pointee = _s.DeclType("a" + Inv(elementType) + "x" + Inv(len), Op.TypeArray, elementType, len);
            _s.SetInfo(pointee, TypeInfoKind.Array, elementType);
        }

        _ = readOnlyBuffer;
        uint id = NewVariable(g, PointerTo(sc, pointee), sc, pointee, 0);
        _s.Decorate(id, Dec.DescriptorSet, (uint)set);
        _s.Decorate(id, Dec.Binding, (uint)binding);
    }

    private TypeLayout? LayoutOfElement(GlobalVariable g, HlslType element, BufferLayoutRules rules)
    {
        if (g.Layout is { } l)
        {
            return l;
        }

        if (BufferLayout.TryCompute(element, rules, _m.LayoutOptions, out var layout, out var error))
        {
            return layout;
        }

        Fail(SpirvCodes.Unsupported, "The buffer '" + g.Name + "' has no usable layout: " + error, g.Span);
        return null;
    }

    private uint ConstantBlock(GlobalVariable g, ResourceType rt)
    {
        if (rt.Element is not StructType st)
        {
            Unsupported("ConstantBuffer<T> needs a struct T; wrap '" + g.Name + "' in a struct.", g.Span);
            return TypeId(ScalarType.UInt);
        }

        var layout = LayoutOfElement(g, st, BufferLayoutRules.ConstantBuffer);
        if (layout is null)
        {
            return TypeId(ScalarType.UInt);
        }

        // DXC names the block type after the cbuffer, or after the struct for ConstantBuffer<T>.
        string name = st.Name.Length == 0 ? "type." + g.Name : "type.ConstantBuffer." + st.Name;
        return LayoutType(layout, true, name);
    }

    private uint StorageBlock(GlobalVariable g, ResourceType rt)
    {
        uint element;
        uint stride;
        string name;
        TypeLayout? elementLayout = null;
        if (rt.ResourceKind is ResourceTypeKind.ByteAddressBuffer or ResourceTypeKind.RWByteAddressBuffer)
        {
            element = TypeId(ScalarType.UInt);
            stride = 4;
            name = rt.IsReadWrite ? "type.RWByteAddressBuffer" : "type.ByteAddressBuffer";
        }
        else
        {
            if (rt.Element is null)
            {
                Fail(SpirvCodes.MalformedIr, "The structured buffer '" + g.Name + "' has no element type.", g.Span);
                return TypeId(ScalarType.UInt);
            }

            elementLayout = LayoutOfElement(g, rt.Element, BufferLayoutRules.StorageBuffer);
            if (elementLayout is null)
            {
                return TypeId(ScalarType.UInt);
            }

            element = LayoutType(elementLayout, false, null);
            // The array stride is the element size rounded up to its alignment (16 for a bare float3).
            uint align = (uint)System.Math.Max(1, elementLayout.Alignment);
            stride = ((uint)elementLayout.Size + align - 1) / align * align;
            name = (rt.IsReadWrite ? "type.RWStructuredBuffer." : "type.StructuredBuffer.") + rt.Element;
        }

        string rk = "lra:" + Inv(element) + ":" + Inv(stride);
        bool isNew = !_s.TryGetKey(rk, out _);
        uint arr = _s.DeclType(rk, Op.TypeRuntimeArray, element);
        if (isNew)
        {
            _s.SetInfo(arr, TypeInfoKind.Array, element);
            _s.Decorate(arr, Dec.ArrayStride, stride);
        }

        string key = "sb:" + Inv(arr) + ":" + (rt.IsReadWrite ? "rw" : "ro") + ":" + name;
        if (_s.TryGetKey(key, out var existing))
        {
            return existing;
        }

        uint id = _s.NewId();
        SpirvModule.Put(_s.Types, Op.TypeStruct, id, arr);
        _s.SetKey(key, id);
        _s.SetInfo(id, TypeInfoKind.Struct, arr);
        _s.Decorate(id, Dec.Block);
        _s.MemberDecorate(id, 0, Dec.Offset, 0);
        if (elementLayout is not null)
        {
            DecorateMatrix(id, 0, elementLayout);
        }

        if (!rt.IsReadWrite)
        {
            _s.MemberDecorate(id, 0, Dec.NonWritable);
        }

        if (_o.KeepNames)
        {
            _s.Name(id, name);
            _s.MemberName(id, 0, "data");
        }

        return id;
    }
}
