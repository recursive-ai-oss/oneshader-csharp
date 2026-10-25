using System;
using System.Collections.Generic;
using OneShader.Diagnostics;
using OneShader.IR;
using OneShader.Reflection;
using OneShader.Syntax;
using OneShader.Types;

namespace OneShader.Semantics;

/// <summary>Turns a checked syntax tree into an <see cref="IrModule"/>.</summary>
internal sealed partial class Lowerer
{
    private readonly Checker _ck;
    private readonly DiagnosticBag _diag;
    private readonly IrModule _mod = new();
    private readonly Dictionary<FunctionSymbol, IrFunction> _fns = [];
    private readonly HashSet<string> _fnNames = new(StringComparer.Ordinal);
    private IrFunction? _initFn;
    private IrFunction _f = null!;
    private IrBuilder _b = null!;
    private bool _terminated;
    private HashSet<BasicBlock> _reached = [];
    private readonly List<(BasicBlock Break, BasicBlock? Continue)> _jumps = [];
    private int _depth;
    private SourceSpan _span;

    public Lowerer(Checker checker, DiagnosticBag diag, string moduleName)
    {
        _ck = checker;
        _diag = diag;
        _mod.Name = moduleName;
        _mod.LayoutOptions = checker.Layout;
    }

    public IrModule Lower()
    {
        DeclareGlobals();
        // A function that takes resources has no run-time form: each call site with different resources gets
        // its own copy of the function with those resources bound (see Specialize).
        foreach (var f in _ck.Functions)
        {
            if (!f.HasResourceParams)
            {
                Declare(f);
            }
        }

        foreach (var f in _ck.Functions)
        {
            if (!f.HasResourceParams)
            {
                LowerFunction(f, _fns[f], null);
            }
        }

        for (int i = 0; i < _specQueue.Count; i++)
        {
            var (sym, fn, bound) = _specQueue[i];
            LowerFunction(sym, fn, bound);
        }

        foreach (var e in _ck.Entries)
        {
            BuildEntryWrapper(e);
        }

        return _mod;
    }

    private void Fail(string message, SourceSpan span) => _diag.Error(SemanticCodes.Internal, message, span);

    // ------------------------------------------------------------ globals

    private void DeclareGlobals()
    {
        foreach (var r in _ck.Resources)
        {
            AddressSpace space;
            HlslType valueType = r.Type;
            var element = r.Type is ArrayType at ? at.Element : r.Type;
            if (element is ResourceType rt)
            {
                space = rt.ResourceKind == ResourceTypeKind.ConstantBuffer ? AddressSpace.Uniform
                    : rt.IsStorageBuffer ? AddressSpace.StorageBuffer : AddressSpace.UniformConstant;
            }
            else
            {
                space = AddressSpace.UniformConstant;
            }

            var g = new GlobalVariable(r.Name, GlobalKind.Resource, space, valueType)
            {
                Set = r.Set,
                Binding = r.Binding,
                ResourceCount = r.Count,
                Layout = r.Layout,
                ImageFormat = r.ImageFormat,
                Span = r.Span,
            };
            r.Global = g;
            _mod.Globals.Add(g);
        }

        foreach (var v in _ck.GroupSharedGlobals)
        {
            var g = new GlobalVariable(v.Name, GlobalKind.Workgroup, AddressSpace.Workgroup, v.Type) { Span = v.Span };
            v.Ir = g;
            _mod.Globals.Add(g);
        }

        foreach (var v in _ck.PrivateGlobals)
        {
            var g = new GlobalVariable(v.IrName ?? v.Name, GlobalKind.Private, AddressSpace.Private, v.Type) { Span = v.Span };
            v.Ir = g;
            _mod.Globals.Add(g);
            if (v.Initializer is null)
            {
                // HLSL static variables start at zero.
                g.Initializer = Constant.Zero(v.Type);
                continue;
            }

            if (v.Const is { } cv && v.Type is ScalarType)
            {
                g.Initializer = Constant.FromBits((ScalarType)v.Type, cv.Bits());
            }
            else
            {
                _pendingInit.Add(v);
            }
        }
    }

    private readonly List<VariableSymbol> _pendingInit = [];
    private bool _inStaticInit;

    /// <summary>The function that runs non-constant static initializers; null when there are none.</summary>
    private IrFunction? EnsureInit()
    {
        if (_pendingInit.Count == 0)
        {
            return null;
        }

        if (_initFn is not null)
        {
            return _initFn;
        }

        var f = new IrFunction(UniqueFunctionName("init_globals"), HlslType.Void);
        _mod.Functions.Add(f);
        _initFn = f;
        BeginFunction(f);
        _inStaticInit = true;
        foreach (var v in _pendingInit)
        {
            _span = v.Span;
            var val = LowerInitializer(v.Initializer!, v.Type);
            _b.Store((IrValue)v.Ir!, val);
        }

        _inStaticInit = false;

        _b.Return();
        return f;
    }

    private string UniqueFunctionName(string name)
    {
        string n = name;
        int i = 2;
        while (!_fnNames.Add(n))
        {
            n = name + "_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            i++;
        }

        return n;
    }

    // ------------------------------------------------------------ functions

    private static AddressSpace SpaceOfResource(HlslType t)
    {
        var e = t is ArrayType a ? a.Element : t;
        if (e is ResourceType rt)
        {
            return rt.ResourceKind == ResourceTypeKind.ConstantBuffer ? AddressSpace.Uniform : rt.IsStorageBuffer ? AddressSpace.StorageBuffer : AddressSpace.UniformConstant;
        }

        return AddressSpace.UniformConstant;
    }

    private void Declare(FunctionSymbol sym) => Declare(sym, null);

    private IrFunction Declare(FunctionSymbol sym, IrValue?[]? bound)
    {
        string name = sym.Name;
        var f = new IrFunction(UniqueFunctionName(sym.IrName ?? name), sym.ReturnType) { Span = sym.Span };
        for (int i = 0; i < sym.Params.Count; i++)
        {
            var p = sym.Params[i];
            HlslType t;
            if (bound is not null && bound[i] is not null)
            {
                continue;
            }

            if (p.Type is ResourceType or SamplerType or ArrayType { Element: ResourceType or SamplerType })
            {
                t = new PointerType(SpaceOfResource(p.Type), p.Type);
            }
            else if (p.IsOut)
            {
                t = new PointerType(AddressSpace.Function, p.Type);
            }
            else
            {
                t = p.Type;
            }

            f.AddParameter(t, p.Name);
        }

        if (bound is null)
        {
            _fns[sym] = f;
        }

        _mod.Functions.Add(f);
        return f;
    }

    // ------------------------------------------------------------ specialization on resource arguments

    private readonly Dictionary<(FunctionSymbol, string), IrFunction> _specs = [];
    private readonly List<(FunctionSymbol Sym, IrFunction Fn, IrValue?[] Bound)> _specQueue = [];

    /// <summary>A resource pointer in a form that survives into another function: the global itself or a chain of constant indices into it.</summary>
    private static bool Describe(IrValue ptr, out string key)
    {
        key = "";
        switch (ptr)
        {
            case GlobalVariable g:
                key = g.Name + "#" + g.Binding + "/" + g.Set;
                return true;
            case Instruction { Op: Opcode.AccessChain } chain when chain.Operands.Count >= 2 && chain.Operands[0] is GlobalVariable root:
            {
                var sb = new System.Text.StringBuilder();
                Describe(root, out string rootKey);
                sb.Append(rootKey);
                for (int i = 1; i < chain.Operands.Count; i++)
                {
                    if (chain.Operands[i] is not Constant { ConstantKind: ConstantKind.Scalar } c)
                    {
                        return false;
                    }

                    sb.Append('[').Append(c.Bits.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(']');
                }

                key = sb.ToString();
                return true;
            }

            default:
                return false;
        }
    }

    private IrValue RebuildResource(IrValue ptr)
    {
        if (ptr is Instruction { Op: Opcode.AccessChain } chain)
        {
            var idx = new IrValue[chain.Operands.Count - 1];
            for (int i = 0; i < idx.Length; i++)
            {
                idx[i] = chain.Operands[i + 1];
            }

            return _b.AccessChain(chain.Operands[0], ((PointerType)chain.Type).Pointee, idx);
        }

        return ptr;
    }

    /// <summary>The copy of a function that takes resources, for these particular resource arguments; null (after reporting) when an argument is not a resource known at compile time.</summary>
    private IrFunction? Specialize(FunctionSymbol target, List<Expression> all, SourceSpan span)
    {
        var bound = new IrValue?[target.Params.Count];
        var key = new System.Text.StringBuilder();
        for (int i = 0; i < target.Params.Count; i++)
        {
            var p = target.Params[i];
            if (p.Type is not (ResourceType or SamplerType or ArrayType { Element: ResourceType or SamplerType }))
            {
                continue;
            }

            var ptr = i < all.Count ? ResourcePointer(all[i]) : null;
            if (ptr is null || !Describe(ptr, out string k))
            {
                _diag.Error(SemanticCodes.BadResource, "Argument " + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + " of '" + target.Name + "' must be a resource that is known at compile time: a global resource, a resource parameter or a resource variable assigned from one. A resource chosen by a run-time index or condition cannot be passed.", i < all.Count ? all[i].Span : span);
                return null;
            }

            bound[i] = ptr;
            key.Append(k).Append(';');
        }

        if (_specs.TryGetValue((target, key.ToString()), out var existing))
        {
            return existing;
        }

        var fn = Declare(target, bound);
        _specs[(target, key.ToString())] = fn;
        _specQueue.Add((target, fn, bound));
        return fn;
    }

    private IrFunction FunctionFor(FunctionSymbol sym) => _fns[sym.Definition ?? sym];

    private void BeginFunction(IrFunction f)
    {
        _f = f;
        _b = new IrBuilder(f);
        var entry = f.AddBlock("entry");
        _b.Block = entry;
        _terminated = false;
        _reached = [entry];
        _jumps.Clear();
        _resLocals.Clear();
        _resSpan.Clear();
    }

    private void LowerFunction(FunctionSymbol sym, IrFunction f, IrValue?[]? bound)
    {
        EnsureInit();
        BeginFunction(f);
        _span = sym.Span;
        _b.Span = _span;
        int next = 0;
        for (int i = 0; i < sym.Params.Count; i++)
        {
            var p = sym.Params[i];
            var vs = p.Symbol;
            if (bound is not null && bound[i] is { } resource)
            {
                // A specialization: the parameter names this resource directly.
                if (vs is not null)
                {
                    vs.Ir = RebuildResource(resource);
                }

                continue;
            }

            var arg = f.Parameters[next++];
            if (vs is null)
            {
                continue;
            }

            if (arg.Type is PointerType && (p.Type is ResourceType or SamplerType or ArrayType || p.IsOut))
            {
                vs.Ir = arg;
            }
            else
            {
                var slot = _b.Alloca(p.Type, p.Name);
                _b.Store(slot, arg);
                vs.Ir = slot;
            }
        }

        LowerBlockBody(sym.Decl.Body!.Statements);
        if (!_terminated)
        {
            _b.Span = sym.Decl.Body.Span;
            if (sym.ReturnType.IsVoid)
            {
                _b.Return();
            }
            else if (_reached.Contains(_b.Block!))
            {
                _b.Return(Constant.Zero(sym.ReturnType));
            }
            else
            {
                _b.Unreachable();
            }
        }
    }


    // ------------------------------------------------------------ entry points

    private void BuildEntryWrapper(EntryInfo e)
    {
        var bodySym = e.Function;
        var body = _fns[bodySym];
        var wrapper = new IrFunction(UniqueFunctionName(bodySym.Name + "_entry"), HlslType.Void) { Span = bodySym.Span };
        // The wrapper carries the entry point's name; make that name available by renaming it after creation.
        wrapper.Name = bodySym.Name;
        body.Name = UniqueFunctionName(bodySym.Name + "_body");
        _mod.Functions.Add(wrapper);
        var ep = new EntryPoint(wrapper, e.Stage) { WorkgroupSize = e.NumThreads, WritesDepth = e.WritesDepth };
        BeginFunction(wrapper);
        _span = bodySym.Span;
        _b.Span = _span;

        var distanceSlots = new Dictionary<InterfaceVar, int>();
        foreach (var v in e.Inputs)
        {
            v.Global = IsDistance(v) ? DistanceGlobal(e, e.Inputs, v, GlobalKind.Input, AddressSpace.Input, ep.Inputs, distanceSlots) : MakeInterfaceGlobal(e, v, GlobalKind.Input, AddressSpace.Input);
            if (!ep.Inputs.Contains(v.Global))
            {
                ep.Inputs.Add(v.Global);
            }
        }

        foreach (var v in e.Outputs)
        {
            v.Global = IsDistance(v) ? DistanceGlobal(e, e.Outputs, v, GlobalKind.Output, AddressSpace.Output, ep.Outputs, distanceSlots) : MakeInterfaceGlobal(e, v, GlobalKind.Output, AddressSpace.Output);
            if (!ep.Outputs.Contains(v.Global))
            {
                ep.Outputs.Add(v.Global);
            }
        }

        if (EnsureInit() is { } init)
        {
            _b.Call(init);
        }

        var slots = new IrValue[bodySym.Params.Count];
        var args = new IrValue[bodySym.Params.Count];
        for (int i = 0; i < slots.Length; i++)
        {
            var p = bodySym.Params[i];
            if (p.Type.IsError)
            {
                continue;
            }

            if (p.Type is ResourceType or SamplerType)
            {
                Fail("An entry point cannot take a resource as a parameter.", p.Decl?.Span ?? bodySym.Span);
                continue;
            }

            var slot = _b.Alloca(p.Type, p.Name);
            slots[i] = slot;
            if (p.IsIn)
            {
                foreach (var v in e.Inputs)
                {
                    if (v.ParamIndex != i)
                    {
                        continue;
                    }

                    var val = IsDistance(v) ? LoadDistance(v, distanceSlots[v]) : _b.Load(v.Global!);
                    StoreAtPath(slot, p.Type, v.Path, val);
                }
            }

            args[i] = p.IsOut ? slot : _b.Load(slot);
        }

        var call = _b.Call(body, args);
        IrValue? retSlot = null;
        if (!bodySym.ReturnType.IsVoid)
        {
            retSlot = _b.Alloca(bodySym.ReturnType, "result");
            _b.Store(retSlot, call);
        }

        foreach (var v in e.Outputs)
        {
            IrValue slot;
            HlslType t;
            if (v.ParamIndex < 0)
            {
                slot = retSlot!;
                t = bodySym.ReturnType;
            }
            else
            {
                slot = slots[v.ParamIndex];
                t = bodySym.Params[v.ParamIndex].Type;
            }

            var value = LoadAtPath(slot, t, v.Path);
            if (IsDistance(v))
            {
                StoreDistance(v, distanceSlots[v], value);
            }
            else
            {
                _b.Store(v.Global!, value);
            }
        }

        _b.Return();
        _mod.EntryPoints.Add(ep);
    }

    private void StoreAtPath(IrValue root, HlslType rootType, List<int> path, IrValue value) =>
        _b.Store(PointerAtPath(root, rootType, path), value);

    private IrValue LoadAtPath(IrValue root, HlslType rootType, List<int> path) =>
        _b.Load(PointerAtPath(root, rootType, path));

    /// <summary>The address inside a parameter or result for a flattened stage variable: struct member, array element or matrix row.</summary>
    private IrValue PointerAtPath(IrValue root, HlslType rootType, List<int> path)
    {
        var ptr = root;
        var t = rootType;
        foreach (int idx in path)
        {
            var next = t is StructType st ? st.Fields[idx].Type : TypeOps.IndexedElement(t)!;
            ptr = _b.AccessChain(ptr, next, Constant.Int(idx));
            t = next;
        }

        return ptr;
    }

    private static bool IsDistance(InterfaceVar v) => v.BuiltIn is BuiltIn.ClipDistance or BuiltIn.CullDistance;

    /// <summary>
    /// Clip and cull distances are one built-in array each in SPIR-V; every variable with that semantic owns a slice of it,
    /// ordered by semantic index as DXC does. Returns the shared array variable and records the variable's first slot.
    /// </summary>
    private GlobalVariable DistanceGlobal(EntryInfo e, List<InterfaceVar> all, InterfaceVar first, GlobalKind kind, AddressSpace space, List<GlobalVariable> already, Dictionary<InterfaceVar, int> slots)
    {
        foreach (var g in already)
        {
            if (g.BuiltIn == first.BuiltIn)
            {
                return g;
            }
        }

        var members = new List<InterfaceVar>();
        foreach (var v in all)
        {
            if (v.BuiltIn != first.BuiltIn)
            {
                continue;
            }

            int at = members.Count;
            while (at > 0 && members[at - 1].SemanticIndex > v.SemanticIndex)
            {
                at--;
            }

            members.Insert(at, v);
        }

        int total = 0;
        foreach (var v in members)
        {
            slots[v] = total;
            total += TypeOps.Count(v.Type);
        }

        var array = new GlobalVariable(e.Function.Name + "." + (first.BuiltIn == BuiltIn.ClipDistance ? "ClipDistance" : "CullDistance"), kind, space, HlslType.Array(ScalarType.Float, total))
        {
            BuiltIn = first.BuiltIn,
            Semantic = first.Semantic,
            Span = first.Span,
        };
        _mod.Globals.Add(array);
        return array;
    }

    private void StoreDistance(InterfaceVar v, int slot, IrValue value)
    {
        int n = TypeOps.Count(v.Type);
        for (int j = 0; j < n; j++)
        {
            var ptr = _b.AccessChain(v.Global!, ScalarType.Float, Constant.Int(slot + j));
            _b.Store(ptr, n == 1 ? value : Extract(value, ScalarType.Float, j));
        }
    }

    private IrValue LoadDistance(InterfaceVar v, int slot)
    {
        int n = TypeOps.Count(v.Type);
        var parts = new IrValue[n];
        for (int j = 0; j < n; j++)
        {
            parts[j] = _b.Load(_b.AccessChain(v.Global!, ScalarType.Float, Constant.Int(slot + j)));
        }

        return n == 1 ? parts[0] : _b.Construct(v.Type, parts);
    }

    private GlobalVariable MakeInterfaceGlobal(EntryInfo e, InterfaceVar v, GlobalKind kind, AddressSpace space)
    {
        var g = new GlobalVariable(e.Function.Name + "." + v.Name, kind, space, v.Type)
        {
            Location = v.Location,
            BuiltIn = v.BuiltIn,
            Interpolation = v.Interpolation,
            Semantic = v.Semantic,
            Span = v.Span,
        };
        _mod.Globals.Add(g);
        return g;
    }
}
