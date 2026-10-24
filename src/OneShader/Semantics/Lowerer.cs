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
        foreach (var f in _ck.Functions)
        {
            Declare(f);
        }

        foreach (var f in _ck.Functions)
        {
            LowerFunction(f);
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
            var g = new GlobalVariable(v.Name, GlobalKind.Private, AddressSpace.Private, v.Type) { Span = v.Span };
            v.Ir = g;
            _mod.Globals.Add(g);
            if (v.Initializer is null)
            {
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
        foreach (var v in _pendingInit)
        {
            _span = v.Span;
            var val = LowerInitializer(v.Initializer!, v.Type);
            _b.Store((IrValue)v.Ir!, val);
        }

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

    private void Declare(FunctionSymbol sym)
    {
        string name = sym.Name;
        var f = new IrFunction(UniqueFunctionName(name), sym.ReturnType) { Span = sym.Span };
        if (sym.HasResourceParams)
        {
            f.Inline = InlineHint.Always;
        }

        foreach (var p in sym.Params)
        {
            HlslType t;
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

        _fns[sym] = f;
        _mod.Functions.Add(f);
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
    }

    private void LowerFunction(FunctionSymbol sym)
    {
        var f = _fns[sym];
        EnsureInit();
        BeginFunction(f);
        _span = sym.Span;
        _b.Span = _span;
        for (int i = 0; i < sym.Params.Count; i++)
        {
            var p = sym.Params[i];
            var arg = f.Parameters[i];
            var vs = p.Symbol;
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

        foreach (var v in e.Inputs)
        {
            v.Global = MakeInterfaceGlobal(e, v, GlobalKind.Input, AddressSpace.Input);
            ep.Inputs.Add(v.Global);
        }

        foreach (var v in e.Outputs)
        {
            v.Global = MakeInterfaceGlobal(e, v, GlobalKind.Output, AddressSpace.Output);
            ep.Outputs.Add(v.Global);
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

                    var val = _b.Load(v.Global!);
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

            _b.Store(v.Global!, LoadAtPath(slot, t, v.Path));
        }

        _b.Return();
        _mod.EntryPoints.Add(ep);
    }

    private void StoreAtPath(IrValue root, HlslType rootType, List<int> path, IrValue value)
    {
        if (path.Count == 0)
        {
            _b.Store(root, value);
            return;
        }

        var ptr = root;
        var t = rootType;
        foreach (int idx in path)
        {
            var field = ((StructType)t).Fields[idx].Type;
            ptr = _b.AccessChain(ptr, field, Constant.Int(idx));
            t = field;
        }

        _b.Store(ptr, value);
    }

    private IrValue LoadAtPath(IrValue root, HlslType rootType, List<int> path)
    {
        var ptr = root;
        var t = rootType;
        foreach (int idx in path)
        {
            var field = ((StructType)t).Fields[idx].Type;
            ptr = _b.AccessChain(ptr, field, Constant.Int(idx));
            t = field;
        }

        return _b.Load(ptr);
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
