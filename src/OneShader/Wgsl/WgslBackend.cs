using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using OneShader.Diagnostics;
using OneShader.IR;
using OneShader.Reflection;
using OneShader.Types;

namespace OneShader.Wgsl;

/// <summary>Settings for <see cref="WgslBackend.Emit"/>.</summary>
public sealed class WgslOptions
{
    /// <summary>Use source names for local values in the output. Default true; false gives <c>v0</c>, <c>v1</c>, ... Module-level names (resources, functions, structs) are always kept.</summary>
    public bool KeepNames { get; init; } = true;
}

/// <summary>Stable diagnostic codes of the WGSL backend (OSH5200-OSH5299). Codes are never reused.</summary>
internal static class WgslCodes
{
    public const string EntryPointNotFound = "OSH5201";
    public const string Unsupported = "OSH5202";
    public const string MissingBinding = "OSH5203";
    public const string MalformedIr = "OSH5204";
    public const string PreciseNotHonoured = "OSH5205";
}

/// <summary>Turns IR into WGSL source for WebGPU.</summary>
public static class WgslBackend
{
    /// <summary>
    /// Emits WGSL for one entry point and everything it reaches. Buffers keep the byte layout the SPIR-V target uses
    /// (relaxed std140 constant buffers, relaxed std430 structured buffers), whatever WGSL's own layout rules say.
    /// The output is deterministic: the same module and options give the same text.
    /// </summary>
    /// <param name="module">The IR module. Functions that take resource pointers must already be inlined.</param>
    /// <param name="entryPointName">The name of the entry point's function.</param>
    /// <param name="options">Settings; defaults when null.</param>
    /// <param name="diagnostics">Receives errors for constructs WGSL cannot express.</param>
    /// <returns>The WGSL text, or null when an error was reported (never throws for unsupported input).</returns>
    public static string? Emit(IrModule module, string entryPointName, WgslOptions? options, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(entryPointName);
        ArgumentNullException.ThrowIfNull(diagnostics);
        return new WgslEmitter(module, options ?? new WgslOptions(), diagnostics).Run(entryPointName);
    }

    /// <summary>
    /// The name of the entry point's function in the WGSL source, which a host passes as the entry point of a pipeline stage.
    /// It is the HLSL name, except that characters outside letters and digits become underscores and names WGSL reserves or predeclares
    /// (<c>loop</c>, <c>fragment</c>, <c>select</c>, ...) get a trailing underscore.
    /// </summary>
    public static string EntryFunctionName(string hlslName)
    {
        ArgumentNullException.ThrowIfNull(hlslName);
        return WgslNames.Sanitize(hlslName, "main");
    }
}

internal sealed partial class WgslEmitter
{
    private const int MaxDepth = 64;

    private readonly IrModule _m;
    private readonly WgslOptions _o;
    private readonly DiagnosticBag _d;
    private EntryPoint _ep = null!;
    private bool _failed;

    private bool _warnedPrecise;
    private readonly HashSet<string> _moduleNames = new(StringComparer.Ordinal);
    private readonly Dictionary<GlobalVariable, string> _globalNames = [];
    private readonly Dictionary<IrFunction, string> _fnNames = [];
    private readonly List<IrFunction> _functions = [];
    private readonly HashSet<GlobalVariable> _reachableGlobals = [];
    private readonly HashSet<GlobalVariable> _atomicWorkgroup = [];
    private bool _usesDerivatives;
    private bool _usesSubgroups;
    private string? _entryReturnExpr;

    public WgslEmitter(IrModule m, WgslOptions o, DiagnosticBag d)
    {
        _m = m;
        _o = o;
        _d = d;
    }

    public string? Run(string entryName)
    {
        EntryPoint? ep = null;
        foreach (var e in _m.EntryPoints)
        {
            if (e.Function.Name == entryName)
            {
                ep = e;
                break;
            }
        }

        if (ep is null)
        {
            Fail(WgslCodes.EntryPointNotFound, "The module has no entry point named '" + entryName + "'; check the name passed to the compiler.");
            return null;
        }

        _ep = ep;
        Scan();
        if (_failed)
        {
            return null;
        }

        RegisterNames();
        PrepareBuffers();
        RegisterTypes();
        var functionTexts = new List<string>();
        foreach (var f in _functions)
        {
            if (!ReferenceEquals(f, ep.Function))
            {
                functionTexts.Add(EmitFunctionText(f, false, null));
            }
        }

        functionTexts.Add(EmitFunctionText(ep.Function, true, EntrySetup));
        if (_failed)
        {
            return null;
        }

        return Assemble(functionTexts);
    }

    // ---- reachability and usage ---------------------------------------------------------------------------

    private static IrValue RootOf(IrValue v)
    {
        for (int guard = 0; v is Instruction { Op: Opcode.AccessChain } chain && guard < 1000; guard++)
        {
            v = chain.Operands[0];
        }

        return v;
    }

    private void Scan()
    {
        var found = new HashSet<IrFunction> { _ep.Function };
        var work = new Stack<IrFunction>();
        work.Push(_ep.Function);
        var callees = new Dictionary<IrFunction, List<IrFunction>>();
        while (work.Count > 0)
        {
            var f = work.Pop();
            var calls = new List<IrFunction>();
            callees[f] = calls;
            foreach (var b in f.Blocks)
            {
                foreach (var i in b.Instructions)
                {
                    if (i.Precise && !_warnedPrecise)
                    {
                        // WGSL 15.7.1: an implementation may fuse a multiply and an add, and the language has no way to forbid it.
                        _warnedPrecise = true;
                        _d.Warning(WgslCodes.PreciseNotHonoured, "'precise' arithmetic is compiled, but WGSL cannot forbid a multiply-add being fused or a sum being reordered, so the result may differ in the last bits from the other targets. Use 'fma' where the exact rounding matters, or leave WGSL out of the targets for this shader.", i.Span);
                    }

                    foreach (var op in i.Operands)
                    {
                        if (op is GlobalVariable g)
                        {
                            _reachableGlobals.Add(g);
                        }
                    }

                    if (i.Callee is { } callee)
                    {
                        calls.Add(callee);
                        if (found.Add(callee))
                        {
                            work.Push(callee);
                        }
                    }

                    ScanUse(i);
                }
            }
        }

        foreach (var g in _ep.Inputs)
        {
            _reachableGlobals.Add(g);
        }

        foreach (var g in _ep.Outputs)
        {
            _reachableGlobals.Add(g);
        }

        foreach (var f in _m.Functions)
        {
            if (found.Contains(f))
            {
                _functions.Add(f);
            }
        }

        if (!_functions.Contains(_ep.Function))
        {
            _functions.Add(_ep.Function);
        }

        CheckRecursion(callees);

        // One clear error per array instead of a follow-on error at every use.
        foreach (var g in _reachableGlobals)
        {
            if (g.ResourceCount != 1)
            {
                Unsupported("Resource arrays are not available in WGSL (WebGPU has no binding arrays); declare '" + g.Name + "' as separate resources, or use a texture array (Texture2DArray) when the elements are textures of one size.", g.Span);
            }
        }
    }

    private void CheckRecursion(Dictionary<IrFunction, List<IrFunction>> callees)
    {
        // Iterative depth-first search with colours: 1 = on the stack, 2 = finished.
        var colour = new Dictionary<IrFunction, int>();
        foreach (var root in _functions)
        {
            if (colour.ContainsKey(root))
            {
                continue;
            }

            var stack = new Stack<(IrFunction F, int Next)>();
            stack.Push((root, 0));
            colour[root] = 1;
            while (stack.Count > 0)
            {
                var (f, next) = stack.Pop();
                var list = callees.TryGetValue(f, out var l) ? l : [];
                if (next < list.Count)
                {
                    stack.Push((f, next + 1));
                    var c = list[next];
                    if (colour.TryGetValue(c, out int state))
                    {
                        if (state == 1)
                        {
                            Unsupported("The function '" + c.Name + "' calls itself, directly or through other functions; WGSL does not allow recursion.", c.Span);
                        }
                    }
                    else
                    {
                        colour[c] = 1;
                        stack.Push((c, 0));
                    }
                }
                else
                {
                    colour[f] = 2;
                }
            }
        }
    }

    private void ScanUse(Instruction i)
    {
        switch (i.Op)
        {
            case Opcode.AtomicRmw or Opcode.AtomicCompareExchange:
            {
                var root = RootOf(i.Operands[0]) as GlobalVariable;
                if (root is null)
                {
                    break;
                }

                if (root.GlobalKind == GlobalKind.Workgroup)
                {
                    _atomicWorkgroup.Add(root);
                }
                else if (root.ValueType is ResourceType { IsStorageBuffer: true })
                {
                    PendingAtomicBuffers.Add(root);
                }
                else if (root.ValueType is ResourceType { IsTexture: true })
                {
                    Unsupported("Atomic operations on textures are not available in WGSL; use a structured buffer.", i.Span);
                }

                break;
            }

            case Opcode.TextureSampleCompare or Opcode.TextureSampleCompareLevelZero or Opcode.TextureGatherCompare:
                if (i.Operands[0] is GlobalVariable depth)
                {
                    _depthTextures.Add(depth);
                }

                _usesDerivatives |= i.Op == Opcode.TextureSampleCompare;
                break;
            case Opcode.TextureSample or Opcode.TextureSampleBias:
                _usesDerivatives = true;
                break;
            case Opcode.TextureLoad when i.Operands[0] is GlobalVariable lg:
            {
                _storageUse.TryGetValue(lg, out var u);
                _storageUse[lg] = (true, u.Store);
                break;
            }

            case Opcode.TextureStore when i.Operands[0] is GlobalVariable sg:
            {
                _storageUse.TryGetValue(sg, out var u);
                _storageUse[sg] = (u.Load, true);
                break;
            }

            case Opcode.CallIntrinsic:
                _usesDerivatives |= i.Intrinsic is Intrinsic.Ddx or Intrinsic.DdxCoarse or Intrinsic.DdxFine or Intrinsic.Ddy
                    or Intrinsic.DdyCoarse or Intrinsic.DdyFine or Intrinsic.Fwidth;
                _usesSubgroups |= SubgroupUse.Of(i.Intrinsic) != SubgroupOperations.None;
                break;
            default:
                break;
        }
    }

    private HashSet<GlobalVariable> PendingAtomicBuffers { get; } = [];

    // ---- names and types -------------------------------------------------------------------------------------

    private void RegisterNames()
    {
        _fnNames[_ep.Function] = WgslNames.Unique(_moduleNames, WgslBackend.EntryFunctionName(_ep.Function.Name));
        foreach (var g in _m.Globals)
        {
            if (_reachableGlobals.Contains(g))
            {
                _globalNames[g] = WgslNames.Unique(_moduleNames, WgslNames.Sanitize(g.Name, "g"));
            }
        }

        foreach (var g in _ep.Inputs)
        {
            if (!_globalNames.ContainsKey(g))
            {
                _globalNames[g] = WgslNames.Unique(_moduleNames, WgslNames.Sanitize(g.Name, "g"));
            }
        }

        foreach (var g in _ep.Outputs)
        {
            if (!_globalNames.ContainsKey(g))
            {
                _globalNames[g] = WgslNames.Unique(_moduleNames, WgslNames.Sanitize(g.Name, "g"));
            }
        }

        foreach (var f in _functions)
        {
            if (!_fnNames.ContainsKey(f))
            {
                _fnNames[f] = WgslNames.Unique(_moduleNames, WgslNames.Sanitize(f.Name, "fn"));
            }
        }
    }

    /// <summary>Registers every struct the entry point reaches, so struct and field names are settled before any body is written.</summary>
    private void RegisterTypes()
    {
        foreach (var g in _m.Globals)
        {
            if (_reachableGlobals.Contains(g) && g.GlobalKind is GlobalKind.Private or GlobalKind.Workgroup or GlobalKind.Input or GlobalKind.Output)
            {
                TypeName(g.ValueType, _atomicWorkgroup.Contains(g));
            }
        }

        foreach (var f in _functions)
        {
            if (!f.ReturnType.IsVoid)
            {
                TypeName(f.ReturnType);
            }

            foreach (var p in f.Parameters)
            {
                TypeName(p.Type);
            }

            foreach (var b in f.Blocks)
            {
                foreach (var i in b.Instructions)
                {
                    if (i.Type is PointerType { Pointee: ResourceType or SamplerType })
                    {
                        continue;
                    }

                    if (!i.IsVoid && i.Type is not PointerType)
                    {
                        TypeName(i.Type);
                    }
                }
            }
        }
    }

    // ---- buffers ---------------------------------------------------------------------------------------------

    private static int AlignUp(int v, int a) => a <= 1 ? v : (v + a - 1) / a * a;

    private void PrepareBuffers()
    {
        foreach (var g in _m.Globals)
        {
            if (!_reachableGlobals.Contains(g) || g.ValueType is not ResourceType rt || !_globalNames.TryGetValue(g, out var name))
            {
                continue;
            }

            if (rt.ResourceKind == ResourceTypeKind.ConstantBuffer)
            {
                if (rt.Element is not StructType)
                {
                    Unsupported("ConstantBuffer<T> needs a struct T; wrap '" + g.Name + "' in a struct.", g.Span);
                    continue;
                }

                var layout = LayoutOf(g, rt.Element, BufferLayoutRules.ConstantBuffer);
                if (layout is not null)
                {
                    _bufs[g] = new BufInfo(g, rt, name) { Uniform = true, Element = layout };
                }
            }
            else if (rt.IsStorageBuffer)
            {
                if (rt.ResourceKind is ResourceTypeKind.AppendStructuredBuffer or ResourceTypeKind.ConsumeStructuredBuffer)
                {
                    Unsupported("Append and consume buffers are not available in WGSL; keep a counter in a separate RWStructuredBuffer and use InterlockedAdd.", g.Span);
                    continue;
                }

                BufInfo info;
                if (rt.ResourceKind is ResourceTypeKind.ByteAddressBuffer or ResourceTypeKind.RWByteAddressBuffer)
                {
                    info = new BufInfo(g, rt, name) { ByteAddress = true, Stride = 4 };
                }
                else if (rt.Element is null)
                {
                    Fail(WgslCodes.MalformedIr, "The structured buffer '" + g.Name + "' has no element type.", g.Span);
                    continue;
                }
                else
                {
                    var layout = LayoutOf(g, rt.Element, BufferLayoutRules.StorageBuffer);
                    if (layout is null)
                    {
                        continue;
                    }

                    // The array stride is the element size rounded up to its alignment (16 for a bare float3).
                    info = new BufInfo(g, rt, name) { Element = layout, Stride = AlignUp(layout.Size, Math.Max(1, layout.Alignment)) };
                }

                info.Atomic = PendingAtomicBuffers.Contains(g);
                _bufs[g] = info;
            }
        }
    }

    private TypeLayout? LayoutOf(GlobalVariable g, HlslType element, BufferLayoutRules rules)
    {
        if (g.Layout is { } l)
        {
            return l;
        }

        if (BufferLayout.TryCompute(element, rules, _m.LayoutOptions, out var layout, out var error))
        {
            return layout;
        }

        Unsupported("The buffer '" + g.Name + "' has no usable layout: " + error, g.Span);
        return null;
    }

    // ---- module assembly -------------------------------------------------------------------------------------

    private string Assemble(List<string> functionTexts)
    {
        var sb = new StringBuilder();
        bool tier1 = false;
        var resources = new StringBuilder();
        foreach (var g in _m.Globals)
        {
            if (_reachableGlobals.Contains(g) && g.GlobalKind == GlobalKind.Resource)
            {
                resources.Append(ResourceDeclaration(g, ref tier1));
            }
        }

        if (_failed)
        {
            return string.Empty;
        }

        foreach (var input in _ep.Inputs)
        {
            _usesSubgroups |= input.BuiltIn is BuiltIn.SubgroupLocalInvocationId or BuiltIn.SubgroupSize;
        }

        if (_usesSubgroups)
        {
            // WebGPU's optional subgroups feature; the host must request it (reflection says when).
            sb.Append("enable subgroups;\n");
            // HLSL does not require wave operations to run in uniform control flow, so the checks that WGSL adds are off.
            sb.Append("diagnostic(off, subgroup_uniformity);\n");
        }

        if (_usesDerivatives && _ep.Stage == ShaderStage.Pixel)
        {
            // HLSL lets a shader sample and take derivatives under non-uniform control flow; the values are then
            // undefined, as they are in WGSL once the check is off.
            sb.Append("diagnostic(off, derivative_uniformity);\n");
        }

        if (tier1)
        {
            sb.Append("requires texture_formats_tier1;\n");
        }

        if (sb.Length > 0)
        {
            sb.Append('\n');
        }

        foreach (string s in _structDecls)
        {
            sb.Append(s).Append('\n');
        }

        foreach (string s in _privateConstants)
        {
            sb.Append(s);
        }

        if (_privateConstants.Count > 0)
        {
            sb.Append('\n');
        }

        if (resources.Length > 0)
        {
            sb.Append(resources).Append('\n');
        }

        bool anyVar = false;
        foreach (var g in _m.Globals)
        {
            if (!_reachableGlobals.Contains(g) || g.GlobalKind == GlobalKind.Resource)
            {
                continue;
            }

            sb.Append(VariableDeclaration(g));
            anyVar = true;
        }

        foreach (var g in _ep.Inputs)
        {
            if (!_m.Globals.Contains(g))
            {
                sb.Append(VariableDeclaration(g));
                anyVar = true;
            }
        }

        foreach (var g in _ep.Outputs)
        {
            if (!_m.Globals.Contains(g))
            {
                sb.Append(VariableDeclaration(g));
                anyVar = true;
            }
        }

        if (anyVar)
        {
            sb.Append('\n');
        }

        var helperNames = new List<string>(_helperTexts.Keys);
        helperNames.Sort(StringComparer.Ordinal);
        foreach (string h in helperNames)
        {
            sb.Append(_helperTexts[h]).Append('\n');
        }

        for (int k = 0; k < functionTexts.Count; k++)
        {
            sb.Append(functionTexts[k]);
            if (k + 1 < functionTexts.Count)
            {
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    private string VariableDeclaration(GlobalVariable g)
    {
        string name = _globalNames[g];
        bool atomic = _atomicWorkgroup.Contains(g);
        string type = TypeName(g.ValueType, atomic);
        switch (g.GlobalKind)
        {
            case GlobalKind.Workgroup:
                return "var<workgroup> " + name + ": " + type + ";\n";
            case GlobalKind.Private when g.Initializer is { } init:
                return "var<private> " + name + ": " + type + " = " + ConstExpr(init) + ";\n";
            default:
                return "var<private> " + name + ": " + type + ";\n";
        }
    }

    private string ResourceDeclaration(GlobalVariable g, ref bool tier1)
    {
        string name = _globalNames[g];
        if (g.ValueType is not (ResourceType or SamplerType))
        {
            Fail(WgslCodes.MalformedIr, "The resource '" + g.Name + "' is not a texture, sampler or buffer.", g.Span);
            return "";
        }

        if (g.Set is not { } set || g.Binding is not { } binding)
        {
            Fail(WgslCodes.MissingBinding, "The resource '" + g.Name + "' has no bind group and binding; add [[vk::binding(b, s)]] or a register.", g.Span);
            return "";
        }

        if (g.ResourceCount != 1)
        {
            Unsupported("Resource arrays are not available in WGSL (WebGPU has no binding arrays); declare '" + g.Name + "' as separate resources or use a texture array.", g.Span);
            return "";
        }

        string attrs = "@group(" + Inv(set) + ") @binding(" + Inv(binding) + ") ";
        if (g.ValueType is SamplerType sampler)
        {
            return attrs + "var " + name + ": " + (sampler.IsComparison ? "sampler_comparison" : "sampler") + ";\n";
        }

        var rt = (ResourceType)g.ValueType;
        if (_bufs.TryGetValue(g, out var info))
        {
            if (info.Uniform)
            {
                int rows = Math.Max(1, (info.Element!.Size + 15) / 16);
                return attrs + "var<uniform> " + name + ": array<vec4<u32>, " + Inv(rows) + ">;\n";
            }

            bool write = info.Written || info.Atomic;
            string access = write ? "read_write" : "read";
            string element = info.Atomic ? "atomic<u32>" : "u32";
            return attrs + "var<storage, " + access + "> " + name + ": array<" + element + ">;\n";
        }

        if (rt.ResourceKind == ResourceTypeKind.ConstantBuffer || rt.IsStorageBuffer)
        {
            return "";
        }

        return attrs + "var " + name + ": " + TextureType(g, rt, ref tier1) + ";\n";
    }

    private static readonly Dictionary<string, string> StorageFormats = new(StringComparer.Ordinal)
    {
        ["rgba8"] = "rgba8unorm",
        ["rgba8snorm"] = "rgba8snorm",
        ["rgba8ui"] = "rgba8uint",
        ["rgba8i"] = "rgba8sint",
        ["rgba16ui"] = "rgba16uint",
        ["rgba16i"] = "rgba16sint",
        ["rgba16f"] = "rgba16float",
        ["r32ui"] = "r32uint",
        ["r32i"] = "r32sint",
        ["r32f"] = "r32float",
        ["rg32ui"] = "rg32uint",
        ["rg32i"] = "rg32sint",
        ["rg32f"] = "rg32float",
        ["rgba32ui"] = "rgba32uint",
        ["rgba32i"] = "rgba32sint",
        ["rgba32f"] = "rgba32float",
    };

    // Formats that need `requires texture_formats_tier1;` (WGSL 6.6.1).
    private static readonly Dictionary<string, string> Tier1Formats = new(StringComparer.Ordinal)
    {
        ["r8"] = "r8unorm",
        ["r8snorm"] = "r8snorm",
        ["r8ui"] = "r8uint",
        ["r8i"] = "r8sint",
        ["r16"] = "r16unorm",
        ["r16snorm"] = "r16snorm",
        ["r16ui"] = "r16uint",
        ["r16i"] = "r16sint",
        ["r16f"] = "r16float",
        ["rgba16"] = "rgba16unorm",
        ["rgba16snorm"] = "rgba16snorm",
        ["rg8"] = "rg8unorm",
        ["rg8snorm"] = "rg8snorm",
        ["rg8ui"] = "rg8uint",
        ["rg8i"] = "rg8sint",
        ["rg16"] = "rg16unorm",
        ["rg16snorm"] = "rg16snorm",
        ["rg16ui"] = "rg16uint",
        ["rg16i"] = "rg16sint",
        ["rg16f"] = "rg16float",
        ["rgb10a2"] = "rgb10a2unorm",
        ["rgb10a2ui"] = "rgb10a2uint",
        ["r11g11b10f"] = "rg11b10ufloat",
    };

    private string TextureType(GlobalVariable g, ResourceType rt, ref bool tier1)
    {
        string sampled = ScalarName(SampledScalar(rt));
        bool depth = _depthTextures.Contains(g);
        if (rt.IsReadWrite && rt.IsTexture)
        {
            string dim = rt.ResourceKind switch
            {
                ResourceTypeKind.RWTexture1D => "texture_storage_1d",
                ResourceTypeKind.RWTexture2D => "texture_storage_2d",
                ResourceTypeKind.RWTexture2DArray => "texture_storage_2d_array",
                ResourceTypeKind.RWTexture3D => "texture_storage_3d",
                _ => "",
            };
            if (dim.Length == 0)
            {
                Unsupported("A " + rt.ResourceKind + " is not available in WGSL; use a 2D array texture.", g.Span);
                return "texture_storage_2d<rgba8unorm, write>";
            }

            if (g.ImageFormat is not { } fmt)
            {
                Unsupported("The storage texture '" + g.Name + "' has no texel format; WGSL needs one. Add [[vk::image_format(\"rgba8\")]] (or another format) to its declaration.", g.Span);
                return dim + "<rgba8unorm, write>";
            }

            string? wgslFormat = null;
            if (StorageFormats.TryGetValue(fmt, out var core))
            {
                wgslFormat = core;
            }
            else if (Tier1Formats.TryGetValue(fmt, out var t1))
            {
                wgslFormat = t1;
                tier1 = true;
            }

            if (wgslFormat is null)
            {
                Unsupported("The storage format '" + fmt + "' of '" + g.Name + "' is not a WGSL storage texel format.", g.Span);
                return dim + "<rgba8unorm, write>";
            }

            _storageUse.TryGetValue(g, out var use);
            string access = use.Load && use.Store ? "read_write" : use.Load ? "read" : "write";
            if (access == "read_write" && wgslFormat is not ("r32float" or "r32uint" or "r32sint"))
            {
                Unsupported("'" + g.Name + "' is both read and written, which WebGPU allows only for r32float, r32uint and r32sint storage textures; split it into a read-only and a write-only texture or change the format.", g.Span);
            }

            return dim + "<" + wgslFormat + ", " + access + ">";
        }

        switch (rt.ResourceKind)
        {
            case ResourceTypeKind.Texture1D:
                return depth ? Depth1D(g) : "texture_1d<" + sampled + ">";
            case ResourceTypeKind.Texture2D:
                return depth ? "texture_depth_2d" : "texture_2d<" + sampled + ">";
            case ResourceTypeKind.Texture2DArray:
                return depth ? "texture_depth_2d_array" : "texture_2d_array<" + sampled + ">";
            case ResourceTypeKind.Texture2DMS:
                return depth ? "texture_depth_multisampled_2d" : "texture_multisampled_2d<" + sampled + ">";
            case ResourceTypeKind.Texture3D:
                return depth ? Depth1D(g) : "texture_3d<" + sampled + ">";
            case ResourceTypeKind.TextureCube:
                return depth ? "texture_depth_cube" : "texture_cube<" + sampled + ">";
            case ResourceTypeKind.TextureCubeArray:
                return depth ? "texture_depth_cube_array" : "texture_cube_array<" + sampled + ">";
            case ResourceTypeKind.Buffer or ResourceTypeKind.RWBuffer:
                Unsupported("Typed buffers (" + rt.ResourceKind + ") are not available in WGSL; use a StructuredBuffer.", g.Span);
                return "texture_2d<f32>";
            default:
                Unsupported("A " + rt.ResourceKind + " is not available in WGSL (WebGPU has no one-dimensional or multisampled array textures); use a 2D array texture.", g.Span);
                return "texture_2d<f32>";
        }
    }

    private string Depth1D(GlobalVariable g)
    {
        Unsupported("'" + g.Name + "' is compared against a reference value, which WGSL only allows for 2D, 2D array and cube depth textures.", g.Span);
        return "texture_depth_2d";
    }
}
