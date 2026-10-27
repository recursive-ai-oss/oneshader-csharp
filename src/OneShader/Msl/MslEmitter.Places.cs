using System;
using System.Collections.Generic;
using System.Text;
using OneShader.Diagnostics;
using OneShader.IR;
using OneShader.Reflection;
using OneShader.Types;

namespace OneShader.Msl;

internal sealed partial class MslEmitter
{
    private enum ResClass
    {
        Sampler,
        ConstantBuffer,
        StructuredBuffer,
        ByteAddressBuffer,
        Texture,
        TexelBuffer,
    }

    private sealed class Res
    {
        public required GlobalVariable G { get; init; }

        public ResClass Class { get; init; }

        public int Index { get; init; }

        public int Count { get; init; }

        /// <summary>The resource type of one element (the array's element type for a resource array).</summary>
        public HlslType Elem { get; init; } = HlslType.Error;

        public bool Depth { get; set; }

        public bool Read { get; set; }

        public bool Write { get; set; }

        public TypeLayout? Layout { get; set; }

        public string ElementType { get; set; } = "";

        public bool ElementWrapped { get; set; }

        /// <summary>
        /// True for an unbounded array or a descriptor heap. It is one argument buffer (a buffer-index argument) holding one 8-byte
        /// descriptor per element: a texture's or sampler's <c>gpuResourceID</c>, a buffer's <c>gpuAddress</c>.
        /// </summary>
        public bool Bindless { get; init; }

        /// <summary>For a bindless array: the name of the struct that wraps one descriptor, declared on first use.</summary>
        public string EntryType { get; set; } = "";
    }

    private static ResClass? ClassOfElement(HlslType elem) => elem switch
    {
        SamplerType => ResClass.Sampler,
        ResourceType rt when rt.IsTexture => ResClass.Texture,
        ResourceType { ResourceKind: ResourceTypeKind.Buffer or ResourceTypeKind.RWBuffer } => ResClass.TexelBuffer,
        ResourceType { ResourceKind: ResourceTypeKind.ConstantBuffer } => ResClass.ConstantBuffer,
        ResourceType { ResourceKind: ResourceTypeKind.ByteAddressBuffer or ResourceTypeKind.RWByteAddressBuffer } => ResClass.ByteAddressBuffer,
        ResourceType => ResClass.StructuredBuffer,
        _ => null,
    };

    private readonly Dictionary<GlobalVariable, Res> _res = [];

    // Argument limits of a Metal function (all Apple GPU families).
    private const int MaxBuffers = 31;
    private const int MaxTextures = 128;
    private const int MaxSamplers = 16;

    /// <summary>
    /// Assigns Metal argument indices: buffers (constant, structured and byte-address), textures (including typed buffers) and
    /// samplers each count up from 0 in declaration order over the whole file, and a resource array takes one index per element.
    /// Every entry point of the file sees the same indices, so pipelines that share resources bind them identically.
    /// </summary>
    private void AssignBindings()
    {
        int buffers = 0, textures = 0, samplers = 0;
        var heapIndex = new Dictionary<DescriptorHeaps, int>();
        foreach (var g in _m.Globals)
        {
            if (g.GlobalKind != GlobalKind.Resource)
            {
                continue;
            }

            int count = Math.Max(1, g.ResourceCount);
            var elem = g.ValueType is ArrayType resourceArray ? resourceArray.Element : g.ValueType;
            if (g.ResourceCount == 0)
            {
                // Unbounded arrays and heaps: one argument buffer each, whatever the elements are. The variables read from one heap
                // are views of the same argument buffer and share its index.
                if (ClassOfElement(elem) is not { } bindlessClass)
                {
                    Malformed("The resource '" + g.Name + "' is not a texture, sampler or buffer.", g.Span);
                    continue;
                }

                if (g.DescriptorHeap == DescriptorHeaps.None || !heapIndex.TryGetValue(g.DescriptorHeap, out int bindlessIndex))
                {
                    bindlessIndex = buffers++;
                    if (g.DescriptorHeap != DescriptorHeaps.None)
                    {
                        heapIndex[g.DescriptorHeap] = bindlessIndex;
                    }
                }

                _res[g] = new Res { G = g, Class = bindlessClass, Index = bindlessIndex, Count = 1, Elem = elem, Bindless = true };
                continue;
            }

            ResClass cls;
            int index;
            switch (elem)
            {
                case SamplerType:
                    cls = ResClass.Sampler;
                    index = samplers;
                    samplers += count;
                    break;
                case ResourceType rt when rt.IsTexture:
                    cls = ResClass.Texture;
                    index = textures;
                    textures += count;
                    break;
                case ResourceType { ResourceKind: ResourceTypeKind.Buffer or ResourceTypeKind.RWBuffer }:
                    cls = ResClass.TexelBuffer;
                    index = textures;
                    textures += count;
                    break;
                case ResourceType { ResourceKind: ResourceTypeKind.ConstantBuffer }:
                    cls = ResClass.ConstantBuffer;
                    index = buffers;
                    buffers += count;
                    break;
                case ResourceType { ResourceKind: ResourceTypeKind.ByteAddressBuffer or ResourceTypeKind.RWByteAddressBuffer }:
                    cls = ResClass.ByteAddressBuffer;
                    index = buffers;
                    buffers += count;
                    break;
                case ResourceType:
                    cls = ResClass.StructuredBuffer;
                    index = buffers;
                    buffers += count;
                    break;
                default:
                    Malformed("The resource '" + g.Name + "' is not a texture, sampler or buffer.", g.Span);
                    continue;
            }

            _res[g] = new Res { G = g, Class = cls, Index = index, Count = count, Elem = elem };
        }

        foreach (var f in _order)
        {
            foreach (var b in f.Blocks)
            {
                foreach (var i in b.Instructions)
                {
                    NoteResourceUse(i);
                }
            }
        }

        foreach (var g in _needed[_ep.Function])
        {
            if (!_res.TryGetValue(g, out var r))
            {
                continue;
            }

            // A vertex function that fetches attributes reads them from MslBackend.VertexBufferIndex, so that slot is not available to resources.
            int bufferLimit = _ep.Stage == ShaderStage.Vertex && HasVertexAttributes() ? MslBackend.VertexBufferIndex : MaxBuffers;
            bool over = r.Bindless ? r.Index + r.Count > bufferLimit : r.Class switch
            {
                ResClass.Sampler => r.Index + r.Count > MaxSamplers,
                ResClass.Texture or ResClass.TexelBuffer => r.Index + r.Count > MaxTextures,
                _ => r.Index + r.Count > bufferLimit,
            };
            if (over)
            {
                bool asBuffer = r.Bindless;
                Fail(MslCodes.TooManyResources, "The resource '" + g.Name + "' would need Metal " + (!asBuffer && r.Class == ResClass.Sampler ? "sampler" : !asBuffer && r.Class is ResClass.Texture or ResClass.TexelBuffer ? "texture" : "buffer") +
                    " index " + Inv(r.Index + r.Count - 1) + ", beyond the limit of " + (!asBuffer && r.Class == ResClass.Sampler ? Inv(MaxSamplers) : !asBuffer && r.Class is ResClass.Texture or ResClass.TexelBuffer ? Inv(MaxTextures) : Inv(bufferLimit)) +
                    " per function; merge or remove resources.", g.Span);
            }
        }
    }

    private bool HasVertexAttributes()
    {
        foreach (var g in _ep.Inputs)
        {
            if (g.BuiltIn == BuiltIn.None)
            {
                return true;
            }
        }

        return false;
    }

    private static GlobalVariable? RootGlobal(IrValue v)
    {
        for (int guard = 0; guard < 64; guard++)
        {
            switch (v)
            {
                case GlobalVariable g:
                    return g;
                case Instruction { Op: Opcode.AccessChain } i when i.Operands.Count > 0:
                    v = i.Operands[0];
                    break;
                default:
                    return null;
            }
        }

        return null;
    }

    private void NoteResourceUse(Instruction i)
    {
        switch (i.Op)
        {
            case Opcode.TextureSampleCompare or Opcode.TextureSampleCompareLevelZero or Opcode.TextureGatherCompare:
                if (RootGlobal(i.Operands[0]) is { } dg && _res.TryGetValue(dg, out var dr))
                {
                    dr.Depth = true;
                }

                break;
            case Opcode.TextureStore:
                if (RootGlobal(i.Operands[0]) is { } wg && _res.TryGetValue(wg, out var wr))
                {
                    wr.Write = true;
                }

                break;
            case Opcode.TextureLoad:
                if (RootGlobal(i.Operands[0]) is { } rg && _res.TryGetValue(rg, out var rr))
                {
                    rr.Read = true;
                }

                break;
            case Opcode.AtomicRmw or Opcode.AtomicCompareExchange when i.Operands.Count > 0 && i.Operands[0].Type is PointerType { Pointee: ResourceType }:
                if (RootGlobal(i.Operands[0]) is { } ag && _res.TryGetValue(ag, out var ar))
                {
                    ar.Read = true;
                    ar.Write = true;
                }

                break;
            default:
                break;
        }
    }

    private static ScalarType SampledScalarOf(ResourceType rt)
    {
        var e = rt.Element?.ScalarElement ?? ScalarType.Float;
        return e.IsFloatingPoint || e.ScalarKind == ScalarKind.Bool ? ScalarType.Float : e.IsSignedInteger ? ScalarType.Int : ScalarType.UInt;
    }

    private string TextureTypeText(Res r)
    {
        var rt = (ResourceType)r.Elem;
        string scalar = ScalarName(SampledScalarOf(rt));
        string access;
        if (rt.ResourceKind is ResourceTypeKind.Texture2DMS or ResourceTypeKind.Texture2DMSArray)
        {
            access = "read";
        }
        else if (rt.IsReadWrite)
        {
            access = r.Read && r.Write ? "read_write" : r.Write ? "write" : r.Read ? "read" : "read_write";
        }
        else if (rt.ResourceKind == ResourceTypeKind.Buffer)
        {
            access = "read";
        }
        else
        {
            access = "sample";
        }

        if (r.Depth)
        {
            string depthName = rt.ResourceKind switch
            {
                ResourceTypeKind.Texture2D => "depth2d",
                ResourceTypeKind.Texture2DArray => "depth2d_array",
                ResourceTypeKind.TextureCube => "depthcube",
                ResourceTypeKind.TextureCubeArray => "depthcube_array",
                ResourceTypeKind.Texture2DMS => "depth2d_ms",
                ResourceTypeKind.Texture2DMSArray => "depth2d_ms_array",
                _ => "",
            };
            if (depthName.Length == 0)
            {
                Unsupported("Comparison sampling needs a 2D, cube or array texture; '" + r.G.Name + "' is a " + rt.ResourceKind + ".", r.G.Span);
                depthName = "depth2d";
            }

            return depthName + "<float" + (access == "sample" ? "" : ", access::" + access) + ">";
        }

        string name = rt.ResourceKind switch
        {
            ResourceTypeKind.Texture1D or ResourceTypeKind.RWTexture1D => "texture1d",
            ResourceTypeKind.Texture1DArray or ResourceTypeKind.RWTexture1DArray => "texture1d_array",
            ResourceTypeKind.Texture2D or ResourceTypeKind.RWTexture2D => "texture2d",
            ResourceTypeKind.Texture2DArray or ResourceTypeKind.RWTexture2DArray => "texture2d_array",
            ResourceTypeKind.Texture2DMS => "texture2d_ms",
            ResourceTypeKind.Texture2DMSArray => "texture2d_ms_array",
            ResourceTypeKind.Texture3D or ResourceTypeKind.RWTexture3D => "texture3d",
            ResourceTypeKind.TextureCube => "texturecube",
            ResourceTypeKind.TextureCubeArray => "texturecube_array",
            _ => "texture_buffer",
        };
        return name + "<" + scalar + ", access::" + access + ">";
    }

    /// <summary>Fills in the element storage type of a buffer resource the first time it is needed.</summary>
    private void PrepareBuffer(Res r)
    {
        if (r.ElementType.Length > 0)
        {
            return;
        }

        var rt = (ResourceType)r.Elem;
        switch (r.Class)
        {
            case ResClass.ConstantBuffer:
            {
                if (rt.Element is not StructType st)
                {
                    Unsupported("ConstantBuffer<T> needs a struct T; wrap '" + r.G.Name + "' in a struct.", r.G.Span);
                    r.ElementType = "uint";
                    return;
                }

                r.Layout = r.G.Layout ?? LayoutOrFail(st, BufferLayoutRules.ConstantBuffer, r.G);
                r.ElementType = r.Layout is null ? "uint" : MemStruct(st, r.Layout);
                return;
            }

            case ResClass.ByteAddressBuffer:
                r.ElementType = "uint";
                r.Layout = new TypeLayout(ScalarType.UInt, 4, 4);
                return;
            default:
            {
                if (rt.Element is null)
                {
                    Malformed("The structured buffer '" + r.G.Name + "' has no element type.", r.G.Span);
                    r.ElementType = "uint";
                    return;
                }

                var l = r.G.Layout ?? LayoutOrFail(rt.Element, BufferLayoutRules.StorageBuffer, r.G);
                if (l is null)
                {
                    r.ElementType = "uint";
                    return;
                }

                r.Layout = l;
                if (l.Type is StructType st)
                {
                    r.ElementType = MemStruct(st, l);
                    return;
                }

                var d = MemOf(l);
                int align = Math.Max(1, l.Alignment);
                int stride = (l.Size + align - 1) / align * align;
                r.ElementWrapped = true;
                if (d.Dims.Length == 0)
                {
                    // The layout's size is already padded to the stride, but a packed three-component vector is only 12 bytes
                    // wide in MSL: pad it with a cell so consecutive elements keep the SPIR-V stride (16 for float3).
                    int natural = l.Type is VectorType vt ? vt.Element.StorageBytes(_enable16) * vt.Count : l.Size;
                    stride = Math.Max(stride, l.Size);
                    r.ElementType = stride == natural ? d.Type : CellName(d.Type, natural, stride);
                    r.ElementWrapped = stride != natural;
                    return;
                }

                // Matrices and arrays cannot be pointed to directly; wrap them in a one-member struct whose size is the element stride.
                string name = _names.ClaimExact("os_elem" + Inv(_typeText.Length));
                _typeText.Append("struct ").Append(name).Append("\n{\n    ").Append(d.Type).Append(" v").Append(d.Dims).Append(";\n};\n\n");
                if (stride != l.Size)
                {
                    Unsupported("The element of '" + r.G.Name + "' needs a stride of " + Inv(stride) + " bytes for a size of " + Inv(l.Size) + ", which MSL output cannot express.", r.G.Span);
                }

                r.ElementType = name;
                return;
            }
        }
    }

    private TypeLayout? LayoutOrFail(HlslType element, BufferLayoutRules rules, GlobalVariable g)
    {
        if (BufferLayout.TryCompute(element, rules, _m.LayoutOptions, out var layout, out var error))
        {
            return layout;
        }

        Unsupported("The buffer '" + g.Name + "' has no usable layout: " + error, g.Span);
        return null;
    }

    /// <summary>The MSL type of a resource global as a function parameter, without the name or attribute.</summary>
    private string ResourceParamType(Res r)
    {
        if (r.Bindless)
        {
            return "constant " + BindlessEntryType(r) + "*";
        }

        string one = ResourceElementParamType(r);
        return r.G.ResourceCount != 1 && r.Count >= 1 && r.G.ResourceCount > 0 ? "array<" + one + ", " + Inv(r.Count) + ">" : one;
    }

    /// <summary>True for an array of buffers: Metal cannot take those as one argument, so each element is its own argument.</summary>
    private static bool IsBufferArray(Res r) => r.Count > 1 && r.Class is ResClass.ConstantBuffer or ResClass.StructuredBuffer or ResClass.ByteAddressBuffer;

    private string ResourceElementParamType(Res r)
    {
        string one;
        switch (r.Class)
        {
            case ResClass.Sampler:
                one = "sampler";
                break;
            case ResClass.Texture or ResClass.TexelBuffer:
                one = TextureTypeText(r);
                break;
            case ResClass.ConstantBuffer:
                PrepareBuffer(r);
                one = r.Count > 1 ? "constant " + r.ElementType + "*" : "constant " + r.ElementType + "&";
                break;
            default:
            {
                PrepareBuffer(r);
                bool rw = ((ResourceType)r.Elem).IsReadWrite;
                one = (rw ? "device " : "const device ") + r.ElementType + "*";
                break;
            }
        }

        return one;
    }

    /// <summary>
    /// The struct that wraps one descriptor of a bindless array, declared on first use: a texture, sampler or buffer pointer is only
    /// legal in an argument buffer as a struct member, and an argument buffer is only read through a pointer to such a struct.
    /// </summary>
    private string BindlessEntryType(Res r)
    {
        if (r.EntryType.Length > 0)
        {
            return r.EntryType;
        }

        string member;
        switch (r.Class)
        {
            case ResClass.Sampler:
                member = "sampler";
                break;
            case ResClass.Texture or ResClass.TexelBuffer:
                member = TextureTypeText(r);
                break;
            case ResClass.ConstantBuffer:
                PrepareBuffer(r);
                member = "constant " + r.ElementType + "*";
                break;
            default:
                PrepareBuffer(r);
                member = (((ResourceType)r.Elem).IsReadWrite ? "device " : "const device ") + r.ElementType + "*";
                break;
        }

        string name = _names.Claim(NameOf(r.G) + "_entry", "type");
        _typeText.Append("// One 8-byte descriptor of the argument buffer '").Append(NameOf(r.G)).Append("'.\n");
        _typeText.Append("struct ").Append(name).Append("\n{\n    ").Append(member).Append(" v;\n};\n\n");
        r.EntryType = name;
        return name;
    }

    private static string ResourceAttribute(Res r) => r.Bindless ? "[[buffer(" + Inv(r.Index) + ")]]" : r.Class switch
    {
        ResClass.Sampler => "[[sampler(" + Inv(r.Index) + ")]]",
        ResClass.Texture or ResClass.TexelBuffer => "[[texture(" + Inv(r.Index) + ")]]",
        _ => "[[buffer(" + Inv(r.Index) + ")]]",
    };

    // ---- places -------------------------------------------------------------------------------------------

    private enum PlaceKind
    {
        /// <summary>Ordinary MSL storage (a local, a threadgroup variable, a stage variable).</summary>
        Reg,

        /// <summary>A value in buffer memory, laid out as its <see cref="Place.Layout"/> says.</summary>
        Mem,

        /// <summary>A row of a column-major matrix in buffer memory: the row is spread over the stored columns.</summary>
        MatRow,

        /// <summary>A texture or sampler.</summary>
        Handle,

        /// <summary>A structured or byte-address buffer; the next index selects the element.</summary>
        BufferRoot,

        /// <summary>A resource array; the next index selects the resource.</summary>
        Pending,
    }

    private sealed class Place
    {
        public PlaceKind Kind { get; init; }

        /// <summary>The expression naming the storage.</summary>
        public string Expr { get; init; } = "";

        /// <summary>The register type of the value at this place.</summary>
        public HlslType Type { get; init; } = HlslType.Error;

        public TypeLayout? Layout { get; init; }

        /// <summary>The MSL address space: thread, threadgroup, device or constant.</summary>
        public string Space { get; init; } = "thread";

        /// <summary>For <see cref="PlaceKind.MatRow"/>: the row index.</summary>
        public string Index { get; init; } = "";

        public Res? Resource { get; init; }
    }

    private readonly Dictionary<IrValue, Place> _places = [];

    private static string SpaceOf(AddressSpace s) => s == AddressSpace.Workgroup ? "threadgroup" : s is AddressSpace.StorageBuffer ? "device" : s is AddressSpace.Uniform or AddressSpace.PushConstant ? "constant" : "thread";

    private Place PlaceOf(IrValue v)
    {
        if (_places.TryGetValue(v, out var cached))
        {
            return cached;
        }

        Place p;
        switch (v)
        {
            case GlobalVariable g:
                p = GlobalPlace(g);
                break;
            case Argument { Type: PointerType pt } a:
                p = new Place { Kind = PlaceKind.Reg, Expr = NameOf(a), Type = pt.Pointee, Space = SpaceOf(pt.Space) };
                break;
            case Instruction { Op: Opcode.Alloca } al:
                p = new Place { Kind = PlaceKind.Reg, Expr = NameOf(al), Type = ((PointerType)al.Type).Pointee };
                break;
            case Instruction { Op: Opcode.AccessChain } ac:
                p = ChainPlace(ac);
                break;
            default:
                Malformed("A pointer operand is not an address this backend can follow.");
                p = new Place { Kind = PlaceKind.Reg, Expr = "0", Type = HlslType.Error };
                break;
        }

        _places[v] = p;
        return p;
    }

    private Place GlobalPlace(GlobalVariable g)
    {
        if (g.GlobalKind != GlobalKind.Resource)
        {
            return new Place { Kind = PlaceKind.Reg, Expr = NameOf(g), Type = g.ValueType, Space = g.GlobalKind == GlobalKind.Workgroup ? "threadgroup" : "thread" };
        }

        if (!_res.TryGetValue(g, out var r))
        {
            Malformed("The resource '" + g.Name + "' has no binding.", g.Span);
            return new Place { Kind = PlaceKind.Reg, Expr = NameOf(g), Type = HlslType.Error };
        }

        if (g.ResourceCount != 1)
        {
            return new Place { Kind = PlaceKind.Pending, Expr = NameOf(g), Resource = r, Type = g.ValueType };
        }

        return ResolveResource(r, NameOf(g));
    }

    /// <summary>The place of one resource (<paramref name="expr"/> is its parameter, already indexed for arrays).</summary>
    private Place ResolveResource(Res r, string expr)
    {
        switch (r.Class)
        {
            case ResClass.ConstantBuffer:
                PrepareBuffer(r);
                return new Place
                {
                    Kind = PlaceKind.Mem,
                    Expr = r.G.ResourceCount != 1 ? "(*" + expr + ")" : expr,
                    Type = ((ResourceType)r.Elem).Element ?? HlslType.Error,
                    Layout = r.Layout,
                    Space = "constant",
                    Resource = r,
                };
            case ResClass.StructuredBuffer or ResClass.ByteAddressBuffer:
                PrepareBuffer(r);
                return new Place { Kind = PlaceKind.BufferRoot, Expr = expr, Type = r.Elem, Space = "device", Resource = r };
            default:
                return new Place { Kind = PlaceKind.Handle, Expr = expr, Type = r.Elem, Resource = r };
        }
    }

    private static int? ConstIndex(IrValue v) => v is Constant { ConstantKind: ConstantKind.Scalar } c ? (int)(uint)c.Bits : null;

    private string IndexText(IrValue v)
    {
        if (ConstIndex(v) is { } c)
        {
            return Inv(c);
        }

        return V(v);
    }

    private Place ChainPlace(Instruction ac)
    {
        var cur = PlaceOf(ac.Operands[0]);
        for (int k = 1; k < ac.Operands.Count; k++)
        {
            cur = Step(cur, ac.Operands[k], ac.Span);
        }

        return cur;
    }

    private Place Step(Place p, IrValue index, SourceSpan span)
    {
        switch (p.Kind)
        {
            case PlaceKind.Pending:
                return ResolveResource(p.Resource!, p.Expr + "[" + IndexText(index) + "]" + (p.Resource!.Bindless ? ".v" : ""));
            case PlaceKind.BufferRoot:
            {
                var r = p.Resource!;
                string e = p.Expr + "[" + IndexText(index) + "]" + (r.ElementWrapped ? ".v" : "");
                var rt = (ResourceType)r.Elem;
                return new Place { Kind = PlaceKind.Mem, Expr = e, Type = rt.Element ?? ScalarType.UInt, Layout = r.Layout, Space = "device", Resource = r };
            }

            case PlaceKind.Handle:
                Malformed("A texture or sampler is indexed like memory.", span);
                return p;
            case PlaceKind.Reg:
                return StepReg(p, index, span);
            case PlaceKind.MatRow:
            {
                var m = (MatrixType)p.Layout!.Type;
                var elem = new TypeLayout(m.Element, m.Element.StorageBytes(_enable16), m.Element.StorageBytes(_enable16));
                var line = MatrixLine(p.Expr, p.Layout, m, IndexText(index));
                return new Place { Kind = PlaceKind.Mem, Expr = line + "[" + p.Index + "]", Type = m.Element, Layout = elem, Space = p.Space, Resource = p.Resource };
            }

            default:
                return StepMem(p, index, span);
        }
    }

    private Place StepReg(Place p, IrValue index, SourceSpan span)
    {
        int? c = ConstIndex(index);
        switch (p.Type)
        {
            case StructType st:
                if (c is not { } fi || fi < 0 || fi >= st.Fields.Count)
                {
                    Malformed("A struct member index must be a constant inside the struct.", span);
                    return p;
                }

                return new Place { Kind = PlaceKind.Reg, Expr = p.Expr + "." + FieldName(st, fi), Type = st.Fields[fi].Type, Space = p.Space };
            case ArrayType a:
                return new Place { Kind = PlaceKind.Reg, Expr = p.Expr + "[" + IndexText(index) + "]", Type = a.Element, Space = p.Space };
            case VectorType v:
                if (v.Count == 1)
                {
                    return new Place { Kind = PlaceKind.Reg, Expr = p.Expr, Type = v.Element, Space = p.Space };
                }

                return new Place { Kind = PlaceKind.Reg, Expr = c is { } ci && ci is >= 0 and < 4 ? p.Expr + "." + Swz(ci) : p.Expr + "[" + IndexText(index) + "]", Type = v.Element, Space = p.Space };
            case MatrixType m:
                if (m.Rows == 1)
                {
                    return new Place { Kind = PlaceKind.Reg, Expr = p.Expr, Type = HlslType.Vector(m.Element, m.Columns), Space = p.Space };
                }

                if (m.Columns == 1)
                {
                    return new Place { Kind = PlaceKind.Reg, Expr = c is { } cj && cj is >= 0 and < 4 ? p.Expr + "." + Swz(cj) : p.Expr + "[" + IndexText(index) + "]", Type = m.Element, Space = p.Space };
                }

                return new Place { Kind = PlaceKind.Reg, Expr = p.Expr + "[" + IndexText(index) + "]", Type = HlslType.Vector(m.Element, m.Columns), Space = p.Space };
            default:
                Malformed("An index is applied to a value that cannot be indexed.", span);
                return p;
        }
    }

    private Place StepMem(Place p, IrValue index, SourceSpan span)
    {
        var l = p.Layout!;
        int? c = ConstIndex(index);
        switch (l.Type)
        {
            case StructType st:
                if (c is not { } fi || fi < 0 || fi >= l.Members.Count)
                {
                    Malformed("A struct member index must be a constant inside the struct.", span);
                    return p;
                }

                return new Place { Kind = PlaceKind.Mem, Expr = p.Expr + "." + FieldName(st, fi), Type = st.Fields[fi].Type, Layout = l.Members[fi].Layout, Space = p.Space, Resource = p.Resource };
            case ArrayType a:
                return new Place
                {
                    Kind = PlaceKind.Mem,
                    Expr = p.Expr + "[" + IndexText(index) + "]" + (ElementIsCell(l) ? ".v" : ""),
                    Type = a.Element,
                    Layout = l.Element,
                    Space = p.Space,
                    Resource = p.Resource,
                };
            case VectorType v:
                if (v.Count == 1)
                {
                    return new Place { Kind = PlaceKind.Mem, Expr = p.Expr, Type = v.Element, Layout = new TypeLayout(v.Element, l.Size, l.Size), Space = p.Space, Resource = p.Resource };
                }

                return new Place { Kind = PlaceKind.Mem, Expr = p.Expr + "[" + IndexText(index) + "]", Type = v.Element, Layout = new TypeLayout(v.Element, l.Size / v.Count, l.Size / v.Count), Space = p.Space, Resource = p.Resource };
            case MatrixType m:
                return StepMemMatrix(p, m, index);
            default:
                Malformed("An index is applied to a value that cannot be indexed.", span);
                return p;
        }
    }

    private Place StepMemMatrix(Place p, MatrixType m, IrValue index)
    {
        var l = p.Layout!;
        int s = m.Element.StorageBytes(_enable16);
        if (m.Rows == 1 || m.Columns == 1)
        {
            // Stored as one contiguous vector.
            int n = m.Rows * m.Columns;
            if (m.Rows == 1)
            {
                return new Place { Kind = PlaceKind.Mem, Expr = p.Expr, Type = HlslType.Vector(m.Element, m.Columns), Layout = new TypeLayout(HlslType.Vector(m.Element, m.Columns), n * s, s), Space = p.Space, Resource = p.Resource };
            }

            return new Place { Kind = PlaceKind.Mem, Expr = n == 1 ? p.Expr : p.Expr + "[" + IndexText(index) + "]", Type = m.Element, Layout = new TypeLayout(m.Element, s, s), Space = p.Space, Resource = p.Resource };
        }

        var rowType = HlslType.Vector(m.Element, m.Columns);
        if (l.MatrixPacking == MatrixPacking.RowMajor)
        {
            // The stored lines are the rows.
            string line = MatrixLine(p.Expr, l, m, IndexText(index));
            return new Place { Kind = PlaceKind.Mem, Expr = line, Type = rowType, Layout = new TypeLayout(rowType, m.Columns * s, s), Space = p.Space, Resource = p.Resource };
        }

        return new Place { Kind = PlaceKind.MatRow, Expr = p.Expr, Type = rowType, Layout = l, Space = p.Space, Index = IndexText(index), Resource = p.Resource };
    }

    /// <summary>The expression for stored line <paramref name="line"/> of a matrix in memory (a column when column-major, a row when row-major).</summary>
    private string MatrixLine(string matrix, TypeLayout l, MatrixType m, string line) =>
        matrix + "[" + line + "]" + (MatrixLinesAreCells(l, m) ? ".v" : "");

    // ---- loading and storing through places -------------------------------------------------------------

    private int _tmp;

    private string Tmp(string stem) => _names.ClaimExact("os_" + stem + Inv(++_tmp));

    /// <summary>Returns an MSL expression for the register value at <paramref name="p"/>; may write temporaries first.</summary>
    private string LoadValue(Place p)
    {
        switch (p.Kind)
        {
            case PlaceKind.Reg:
                return p.Expr;
            case PlaceKind.Mem:
                return MemLoad(p.Expr, p.Layout!, p.Type, 0);
            case PlaceKind.MatRow:
            {
                var m = (MatrixType)p.Layout!.Type;
                var parts = new List<string>();
                for (int j = 0; j < m.Columns; j++)
                {
                    parts.Add(MatrixLine(p.Expr, p.Layout, m, Inv(j)) + "[" + p.Index + "]");
                }

                return T(p.Type) + "(" + string.Join(", ", parts) + ")";
            }

            default:
                Malformed("A load addresses something that is not memory.");
                return "0";
        }
    }

    private string MemLoad(string src, TypeLayout l, HlslType t, int depth)
    {
        if (depth > MaxDepth)
        {
            Malformed("A buffer type is nested too deeply to be loaded.");
            return "0";
        }

        if (l.Type is StructType or ArrayType)
        {
            string tmp = Tmp("t");
            W(T(t) + " " + tmp + ";");
            CopyMemToReg(tmp, src, l, t, depth);
            return tmp;
        }

        return MemLoadLeaf(src, l, t);
    }

    private string MemLoadLeaf(string src, TypeLayout l, HlslType t)
    {
        switch (t)
        {
            case ScalarType s:
                return s.ScalarKind == ScalarKind.Bool ? "bool(" + src + ")" : src;
            case EnumType:
                return src;
            case VectorType v:
                if (v.Count == 1)
                {
                    return v.Element.ScalarKind == ScalarKind.Bool ? "bool(" + src + ")" : src;
                }

                return v.Element.ScalarKind == ScalarKind.Bool ? T(t) + "(uint" + Inv(v.Count) + "(" + src + "))" : T(t) + "(" + src + ")";
            case MatrixType m:
                return MatrixLoad(src, l, m);
            default:
                Malformed("The type '" + t + "' cannot be read from a buffer.");
                return "0";
        }
    }

    private string MatrixLoad(string src, TypeLayout l, MatrixType m)
    {
        if (m.Rows == 1 || m.Columns == 1)
        {
            return m.Rows * m.Columns == 1 ? src : T(m) + "(" + src + ")";
        }

        bool rowMajor = l.MatrixPacking == MatrixPacking.RowMajor;
        int lines = rowMajor ? m.Rows : m.Columns;
        int lineLength = rowMajor ? m.Columns : m.Rows;
        string lineType = ScalarName(m.Element) + Inv(lineLength);
        var parts = new List<string>();
        for (int j = 0; j < lines; j++)
        {
            parts.Add(lineType + "(" + MatrixLine(src, l, m, Inv(j)) + ")");
        }

        if (rowMajor)
        {
            return T(m) + "(" + string.Join(", ", parts) + ")";
        }

        // Column-major storage: the stored lines are columns, so build the C-column matrix and transpose it into R rows.
        return "transpose(" + ScalarName(m.Element) + Inv(m.Columns) + "x" + Inv(m.Rows) + "(" + string.Join(", ", parts) + "))";
    }

    private void CopyMemToReg(string dst, string src, TypeLayout l, HlslType t, int depth)
    {
        if (depth > MaxDepth)
        {
            Malformed("A buffer type is nested too deeply to be loaded.");
            return;
        }

        switch (l.Type)
        {
            case StructType st when t is StructType rst:
                for (int i = 0; i < l.Members.Count; i++)
                {
                    string f = FieldName(rst, i);
                    CopyMemToReg(dst + "." + f, src + "." + FieldName(st, i), l.Members[i].Layout, rst.Fields[i].Type, depth + 1);
                }

                break;
            case ArrayType when t is ArrayType rat && l.Element is { } el:
            {
                string k = Tmp("i");
                W("for (int " + k + " = 0; " + k + " < " + Inv(rat.Length) + "; ++" + k + ")");
                W("{");
                _indent++;
                CopyMemToReg(dst + "[" + k + "]", src + "[" + k + "]" + (ElementIsCell(l) ? ".v" : ""), el, rat.Element, depth + 1);
                _indent--;
                W("}");
                break;
            }

            default:
                W(dst + " = " + MemLoadLeaf(src, l, t) + ";");
                break;
        }
    }

    private void StoreValue(Place p, string value)
    {
        switch (p.Kind)
        {
            case PlaceKind.Reg:
                W(p.Expr + " = " + value + ";");
                break;
            case PlaceKind.Mem:
                CopyRegToMem(p.Expr, value, p.Layout!, p.Type, 0);
                break;
            case PlaceKind.MatRow:
            {
                var m = (MatrixType)p.Layout!.Type;
                string src = value;
                if (!IsPlainName(value))
                {
                    src = Tmp("r");
                    W(T(p.Type) + " " + src + " = " + value + ";");
                }

                for (int j = 0; j < m.Columns; j++)
                {
                    W(MatrixLine(p.Expr, p.Layout, m, Inv(j)) + "[" + p.Index + "] = " + src + "." + Swz(j) + ";");
                }

                break;
            }

            default:
                Malformed("A store addresses something that is not memory.");
                break;
        }
    }

    private static bool IsPlainName(string s)
    {
        foreach (char c in s)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return s.Length > 0;
    }

    private void CopyRegToMem(string dst, string src, TypeLayout l, HlslType t, int depth)
    {
        if (depth > MaxDepth)
        {
            Malformed("A buffer type is nested too deeply to be stored.");
            return;
        }

        switch (l.Type)
        {
            case StructType st when t is StructType rst:
                if (!IsPlainName(src) && !src.Contains('.', StringComparison.Ordinal))
                {
                    string tmp = Tmp("s");
                    W(T(t) + " " + tmp + " = " + src + ";");
                    src = tmp;
                }

                for (int i = 0; i < l.Members.Count; i++)
                {
                    CopyRegToMem(dst + "." + FieldName(st, i), src + "." + FieldName(rst, i), l.Members[i].Layout, rst.Fields[i].Type, depth + 1);
                }

                break;
            case ArrayType when t is ArrayType rat && l.Element is { } el:
            {
                if (!IsPlainName(src) && !src.Contains('.', StringComparison.Ordinal))
                {
                    string tmp = Tmp("a");
                    W(T(t) + " " + tmp + " = " + src + ";");
                    src = tmp;
                }

                string k = Tmp("i");
                W("for (int " + k + " = 0; " + k + " < " + Inv(rat.Length) + "; ++" + k + ")");
                W("{");
                _indent++;
                CopyRegToMem(dst + "[" + k + "]" + (ElementIsCell(l) ? ".v" : ""), src + "[" + k + "]", el, rat.Element, depth + 1);
                _indent--;
                W("}");
                break;
            }

            default:
                StoreLeaf(dst, src, l, t);
                break;
        }
    }

    private void StoreLeaf(string dst, string src, TypeLayout l, HlslType t)
    {
        switch (t)
        {
            case ScalarType { ScalarKind: ScalarKind.Bool }:
                W(dst + " = uint(" + src + ");");
                break;
            case VectorType { Count: 1 } v1 when v1.Element.ScalarKind == ScalarKind.Bool:
                W(dst + " = uint(" + src + ");");
                break;
            case VectorType v when v.Count > 1 && v.Element.ScalarKind == ScalarKind.Bool:
                W(dst + " = uint" + Inv(v.Count) + "(" + src + ");");
                break;
            case MatrixType m when m.Rows > 1 && m.Columns > 1:
            {
                string value = src;
                if (!IsPlainName(src))
                {
                    value = Tmp("m");
                    W(T(m) + " " + value + " = " + src + ";");
                }

                bool rowMajor = l.MatrixPacking == MatrixPacking.RowMajor;
                int lines = rowMajor ? m.Rows : m.Columns;
                string source = value;
                if (!rowMajor)
                {
                    source = Tmp("mt");
                    W(ScalarName(m.Element) + Inv(m.Columns) + "x" + Inv(m.Rows) + " " + source + " = transpose(" + value + ");");
                }

                for (int j = 0; j < lines; j++)
                {
                    W(MatrixLine(dst, l, m, Inv(j)) + " = " + source + "[" + Inv(j) + "];");
                }

                break;
            }

            default:
                W(dst + " = " + src + ";");
                break;
        }
    }
}
