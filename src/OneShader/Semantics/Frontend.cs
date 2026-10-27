using System;
using System.Collections.Generic;
using System.Threading;
using OneShader.Diagnostics;
using OneShader.IR;
using OneShader.Lexing;
using OneShader.Msl;
using OneShader.Preprocessing;
using OneShader.Reflection;
using OneShader.Syntax;
using OneShader.Types;
using OneShader.Wgsl;

namespace OneShader.Semantics;

/// <summary>What the front end produces: the IR module and reflection, or just diagnostics.</summary>
internal sealed class FrontendResult
{
    public FrontendResult(IrModule? module, ShaderReflection? reflection, DiagnosticBag diagnostics)
    {
        Module = module;
        Reflection = reflection;
        Diagnostics = diagnostics;
    }

    /// <summary>The lowered module; null when there were errors.</summary>
    public IrModule? Module { get; }

    /// <summary>Reflection; null when there were errors.</summary>
    public ShaderReflection? Reflection { get; }

    /// <summary>Every diagnostic, in report order.</summary>
    public DiagnosticBag Diagnostics { get; }
}

/// <summary>Runs preprocessing, parsing, semantic analysis, lowering and reflection for one shader file.</summary>
internal static class Frontend
{
    private sealed class DelegateResolver(IncludeResolver resolver) : IIncludeResolver
    {
        public SourceFile? Resolve(string name, bool isAngled, SourceFile includer, IList<string> searched)
        {
            var r = resolver(name, includer.Path, isAngled);
            searched.Add("the include resolver");
            return r is null ? null : new SourceFile(r.Path, r.Text);
        }
    }

    /// <summary>Remembers which files the wrapped resolver found, so the caller can tell a file watcher what the compile read.</summary>
    private sealed class RecordingResolver(IIncludeResolver inner, ICollection<string> found) : IIncludeResolver
    {
        public SourceFile? Resolve(string name, bool isAngled, SourceFile includer, IList<string> searched)
        {
            var file = inner.Resolve(name, isAngled, includer, searched);
            if (file is not null)
            {
                found.Add(file.Identity);
            }

            return file;
        }
    }

    /// <summary>The resolver a compile with these options uses for <c>#include</c>: the internal override, the host's hook, or the file system.</summary>
    internal static IIncludeResolver CreateResolver(CompileOptions options)
    {
        if (options.ResolverOverride is { } over)
        {
            return over;
        }

        return options.IncludeResolver is { } hook ? new DelegateResolver(hook) : new FileSystemIncludeResolver(options.IncludeDirectories);
    }

    private static bool ListHas(IReadOnlyList<CompileTarget> list, CompileTarget t)
    {
        foreach (var x in list)
        {
            if (x == t)
            {
                return true;
            }
        }

        return false;
    }

    public static FrontendResult Run(string source, string path, CompileOptions options, CancellationToken cancellationToken = default) =>
        Run(source, path, options, null, cancellationToken);

    /// <summary>As above; every included file the resolver finds is added to <paramref name="includesRead"/> (by its identity).</summary>
    public static FrontendResult Run(string source, string path, CompileOptions options, ICollection<string>? includesRead, CancellationToken cancellationToken)
    {
        var bag = new DiagnosticBag();
        try
        {
            return RunCore(source, path, options, bag, includesRead, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            bag.Error(SemanticCodes.Internal, "Internal compiler error (" + InternalError.Describe(ex) + ") while compiling this file. This is a bug in the compiler; please report it with the shader.", new SourceSpan(path, 1, 1));
            return new FrontendResult(null, null, bag);
        }
    }

    private static FrontendResult RunCore(string source, string path, CompileOptions options, DiagnosticBag bag, ICollection<string>? includesRead, CancellationToken ct)
    {
        var pp = new PreprocessorOptions { IncludeResolver = CreateResolver(options) };

        if (includesRead is not null)
        {
            pp.IncludeResolver = new RecordingResolver(pp.IncludeResolver, includesRead);
        }

        foreach (var d in options.Defines)
        {
            pp.Define(d.Key, d.Value);
        }

        var preprocessed = Preprocessor.Preprocess(new SourceFile(path, source), pp, bag, ct);
        foreach (var d in bag.Items)
        {
            if (d.Code is PreprocessorCodes.LimitExceeded or PreprocessorCodes.ExpansionTooDeep)
            {
                // The preprocessor gave up part-way; the token stream is cut off at an arbitrary place, so parsing it would only pile up follow-on errors.
                return new FrontendResult(null, null, bag);
            }
        }

        var unit = Parser.Parse(preprocessed.Tokens, bag, path, ct);
        var layout = new LayoutOptions { Enable16BitTypes = options.Enable16BitTypes, DefaultMatrixPacking = options.DefaultMatrixPacking };
        var targets = new List<CompileTarget>();
        foreach (var t in Enum.GetValues<CompileTarget>())
        {
            if (options.Targets.Count == 0 ? t == CompileTarget.SpirV : ListHas(options.Targets, t))
            {
                targets.Add(t);
            }
        }

        var checker = new Checker(bag, new FrontendOptions { EntryPoints = options.EntryPoints, Layout = layout, Targets = targets, ResourceHeapBinding = options.ResourceHeapBinding, SamplerHeapBinding = options.SamplerHeapBinding }, path, ct);
        checker.CheckUnit(unit);
        if (bag.HasErrors)
        {
            return new FrontendResult(null, null, bag);
        }

        if (checker.Entries.Count == 0 && options.EntryPoints.Count == 0)
        {
            bag.Warning(SemanticCodes.BadEntryPoint, "The file has no entry point: mark a function with [shader(\"vertex\")], [shader(\"pixel\")] or [shader(\"compute\")].", new SourceSpan(path, 1, 1));
        }

        var lowerer = new Lowerer(checker, bag, System.IO.Path.GetFileName(path));
        var module = lowerer.Lower();
        if (bag.HasErrors)
        {
            return new FrontendResult(null, null, bag);
        }

        if (options.SpecializationOverrides.Count > 0)
        {
            SpecBaker.Bake(module, options.SpecializationOverrides, bag, path);
            if (bag.HasErrors)
            {
                return new FrontendResult(null, null, bag);
            }
        }

        var reflector = new Reflector(checker, module, targets, path);
        reflector.CheckPushConstants(bag);
        if (bag.HasErrors)
        {
            return new FrontendResult(null, null, bag);
        }

        var reflection = reflector.Build();
        return new FrontendResult(module, reflection, bag);
    }
}

internal sealed class Reflector
{
    private readonly Checker _ck;
    private readonly IrModule _mod;
    private readonly List<CompileTarget> _targets;
    private readonly string _path;

    public Reflector(Checker ck, IrModule mod, List<CompileTarget> targets, string path)
    {
        _ck = ck;
        _mod = mod;
        _targets = targets;
        _path = path;
    }

    // Walks the call graph with an explicit stack: call chains can be thousands deep, and a recursive
    // walk would overflow the stack of a thread with a small default stack.
    private static void Collect(IrFunction root, HashSet<GlobalVariable> into, HashSet<IrFunction> seen)
    {
        var pending = new Stack<IrFunction>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var f = pending.Pop();
            if (!seen.Add(f))
            {
                continue;
            }

            foreach (var b in f.Blocks)
            {
                foreach (var i in b.Instructions)
                {
                    foreach (var o in i.Operands)
                    {
                        if (o is GlobalVariable { GlobalKind: GlobalKind.Resource } g)
                        {
                            into.Add(g);
                        }
                    }

                    if (i.Callee is { } callee)
                    {
                        pending.Push(callee);
                    }
                }
            }
        }
    }

    /// <summary>Vulkan gives a pipeline layout one push-constant block per stage, so an entry point that reads two is an error at the second block.</summary>
    public void CheckPushConstants(DiagnosticBag bag)
    {
        foreach (var ep in _mod.EntryPoints)
        {
            var set = new HashSet<GlobalVariable>();
            Collect(ep.Function, set, []);
            ResourceDecl? first = null;
            foreach (var r in _ck.Resources)
            {
                if (!r.IsPushConstant || r.Global is null || !set.Contains(r.Global))
                {
                    continue;
                }

                if (first is null)
                {
                    first = r;
                    continue;
                }

                bag.Error(
                    SemanticCodes.BadPushConstant,
                    "The entry point '" + ep.Function.Name + "' uses the push-constant blocks '" + first.Name + "' and '" + r.Name
                    + "', but Vulkan allows one push-constant block per stage. Merge them into one struct.",
                    r.Span);
            }
        }
    }

    public ShaderReflection Build()
    {
        var entryUses = new List<HashSet<GlobalVariable>>();
        foreach (var ep in _mod.EntryPoints)
        {
            var set = new HashSet<GlobalVariable>();
            Collect(ep.Function, set, []);
            entryUses.Add(set);
        }

        // MSL argument indices count per class in declaration order.
        int bufferIndex = 0, textureIndex = 0, samplerIndex = 0;
        var resources = new List<ResourceInfo>();
        var pushes = new List<PushConstantInfo>();
        var heapInfo = new Dictionary<HeapKind, ResourceDecl>();
        foreach (var r in _ck.Resources)
        {
            var stages = ShaderStages.None;
            for (int i = 0; i < _mod.EntryPoints.Count; i++)
            {
                if (r.Global is not null && entryUses[i].Contains(r.Global))
                {
                    stages |= _mod.EntryPoints[i].Stage switch
                    {
                        ShaderStage.Vertex => ShaderStages.Vertex,
                        ShaderStage.Pixel => ShaderStages.Pixel,
                        _ => ShaderStages.Compute,
                    };
                }
            }

            if (r.Heap != HeapKind.None && heapInfo.TryGetValue(r.Heap, out var firstOfHeap))
            {
                // The variables of one heap share a binding and are reported as one resource: widen its stages.
                int at = resources.FindIndex(x => x.Name == firstOfHeap.Name);
                var prev = resources[at];
                resources[at] = new ResourceInfo
                {
                    Name = prev.Name,
                    Kind = prev.Kind,
                    Count = prev.Count,
                    Stages = prev.Stages | stages,
                    HlslType = "",
                    ResourceType = prev.ResourceType,
                    Heap = prev.Heap,
                    HeapKinds = AddKind(prev.HeapKinds, r.Kind),
                    Slots = prev.Slots,
                };
                continue;
            }

            int mslIndex;
            if (r.Count == 0)
            {
                // An unbounded array or a heap is one argument buffer of descriptors, whatever it holds.
                mslIndex = bufferIndex++;
            }
            else if (r.Kind == ResourceKind.Sampler)
            {
                mslIndex = samplerIndex;
                samplerIndex += Math.Max(1, r.Count);
            }
            else if (r.Kind is ResourceKind.SampledTexture or ResourceKind.StorageTexture or ResourceKind.UniformTexelBuffer or ResourceKind.StorageTexelBuffer)
            {
                mslIndex = textureIndex;
                textureIndex += Math.Max(1, r.Count);
            }
            else
            {
                mslIndex = bufferIndex;
                bufferIndex += Math.Max(1, r.Count);
            }

            if (r.IsPushConstant)
            {
                // The block takes the next Metal buffer index in declaration order, like any other constant buffer.
                var pushSlots = new List<TargetSlot>();
                foreach (var t in _targets)
                {
                    if (t == CompileTarget.Msl)
                    {
                        pushSlots.Add(new TargetSlot(t, 0, (uint)mslIndex));
                    }
                    else if (t == CompileTarget.Wgsl)
                    {
                        pushSlots.Add(new TargetSlot(t, (uint)r.Set, (uint)r.Binding));
                    }
                }

                pushes.Add(new PushConstantInfo { Name = r.Name, Stages = stages, Slots = pushSlots, Layout = LayoutOf(r) ?? ReflectedType.FromLayout(r.Layout!) });
                continue;
            }

            var slots = new List<TargetSlot>();
            var counterSlots = new List<TargetSlot>();
            foreach (var t in _targets)
            {
                slots.Add(t == CompileTarget.Msl ? new TargetSlot(t, 0, (uint)mslIndex) : new TargetSlot(t, (uint)r.Set, (uint)r.Binding));
                if (r.HasCounter)
                {
                    // Metal has no sets: the counter takes the buffer index after its buffer.
                    counterSlots.Add(t == CompileTarget.Msl ? new TargetSlot(t, 0, (uint)bufferIndex) : new TargetSlot(t, (uint)r.Set, (uint)r.CounterBinding));
                }
            }

            if (r.HasCounter)
            {
                bufferIndex++;
            }

            resources.Add(new ResourceInfo
            {
                Name = r.Name,
                Kind = r.Kind,
                Count = (uint)r.Count,
                Stages = stages,
                HlslType = r.Buffer is not null || r.Heap != HeapKind.None ? "" : r.HlslTypeText,
                ResourceType = r.ResourceType,
                ImageFormat = r.ImageFormat ?? "",
                Heap = r.Heap == HeapKind.Resource ? DescriptorHeaps.Resource : r.Heap == HeapKind.Sampler ? DescriptorHeaps.Sampler : DescriptorHeaps.None,
                HeapKinds = r.Heap == HeapKind.None ? [] : [r.Kind],
                Slots = slots,
                CounterSlots = counterSlots,
                Layout = r.Heap == HeapKind.None ? LayoutOf(r) : null,
            });
            if (r.Heap != HeapKind.None)
            {
                heapInfo[r.Heap] = r;
            }
        }

        var specs = new List<SpecConstantInfo>();
        foreach (var sc in _mod.SpecConstants)
        {
            var specSlots = new List<SpecConstantSlot>();
            foreach (var t in _targets)
            {
                string tn = t switch
                {
                    CompileTarget.Msl => MslBackend.SpecConstantName(_mod, sc),
                    CompileTarget.Wgsl => WgslBackend.SpecConstantName(_mod, sc),
                    _ => sc.Name!,
                };
                specSlots.Add(new SpecConstantSlot(t, sc.Id, tn));
            }

            specs.Add(new SpecConstantInfo
            {
                Name = sc.Name!,
                Id = sc.Id,
                Scalar = ((ScalarType)sc.Type).ScalarKind,
                DefaultBits = (uint)sc.Default.Bits,
                Slots = specSlots,
            });
        }

        var entries = new List<EntryPointInfo>();
        for (int i = 0; i < _ck.Entries.Count; i++)
        {
            var e = _ck.Entries[i];
            var used = new List<string>();
            string? pushUse = null;
            foreach (var r in _ck.Resources)
            {
                if (r.Global is not null && entryUses[i].Contains(r.Global))
                {
                    if (r.IsPushConstant)
                    {
                        pushUse ??= r.Name;
                    }
                    else
                    {
                        // The variables read from one heap are reported as one resource, named after the heap.
                        string usedName = r.Heap == HeapKind.Resource ? "ResourceDescriptorHeap" : r.Heap == HeapKind.Sampler ? "SamplerDescriptorHeap" : r.Name;
                        if (!used.Contains(usedName))
                        {
                            used.Add(usedName);
                        }
                    }
                }
            }

            var bindless = BindlessUse.Scan(_mod.EntryPoints[i]);
            var features = WideTypeUse.Scan(_mod, _mod.EntryPoints[i]).Features | bindless.Features;
            foreach (var info in resources)
            {
                if (info.Heap != DescriptorHeaps.None && info.HeapKinds.Count > 1 && (bindless.Heaps & info.Heap) != 0)
                {
                    features |= DeviceFeatures.MutableDescriptorType;
                }
            }

            entries.Add(new EntryPointInfo
            {
                Name = e.Function.Name,
                Stage = e.Stage,
                Inputs = Vars(e.Inputs, e.Stage == ShaderStage.Vertex),
                Outputs = Vars(e.Outputs, false),
                WorkgroupSize = e.Stage == ShaderStage.Compute ? e.NumThreads : default,
                VertexStride = e.Stage == ShaderStage.Vertex ? e.VertexStride : 0,
                UsedResources = used,
                PushConstant = pushUse,
                UsedSpecConstants = SpecNames(SpecUse.Scan(_mod, _mod.EntryPoints[i].Function)),
                SubgroupOperations = SubgroupUse.Scan(_mod.EntryPoints[i].Function, out _, out _),
                RequiredFeatures = features,
                DescriptorHeaps = bindless.Heaps,
            });
        }

        return new ShaderReflection { SourceName = _path, Targets = _targets, Resources = resources, PushConstants = pushes, SpecConstants = specs, EntryPoints = entries };
    }

    private static List<ResourceKind> AddKind(IReadOnlyList<ResourceKind> kinds, ResourceKind kind)
    {
        var list = new List<ResourceKind>(kinds);
        if (!list.Contains(kind))
        {
            list.Add(kind);
        }

        return list;
    }

    private static List<string> SpecNames(List<SpecConstant> used)
    {
        var names = new List<string>(used.Count);
        foreach (var sc in used)
        {
            names.Add(sc.Name!);
        }

        return names;
    }

    private static List<StageVariable> Vars(List<InterfaceVar> vars, bool vertexInput)
    {
        var list = new List<StageVariable>();
        foreach (var v in vars)
        {
            list.Add(new StageVariable
            {
                Name = v.Name,
                Semantic = v.SemanticName,
                SemanticIndex = v.SemanticIndex,
                Location = v.Location is { } l ? (uint)l : null,
                BuiltIn = v.BuiltIn,
                Scalar = v.Type.ScalarElement!.ScalarKind,
                Components = (uint)TypeOps.Count(v.Type),
                Format = vertexInput && v.BuiltIn == BuiltIn.None ? v.Format : null,
                Offset = vertexInput && v.BuiltIn == BuiltIn.None ? v.Offset : 0,
                Interpolation = v.Interpolation,
            });
        }

        return list;
    }

    private static ReflectedType? LayoutOf(ResourceDecl r)
    {
        var element = r.Type is ArrayType at ? at.Element : r.Type;
        if (r.Layout is not null)
        {
            return ReflectedType.FromLayout(r.Layout);
        }

        if (element is ResourceType { Element: ScalarType or VectorType } rt && (rt.IsTexture || rt.ResourceKind is ResourceTypeKind.Buffer or ResourceTypeKind.RWBuffer))
        {
            if (BufferLayout.TryCompute(rt.Element!, BufferLayoutRules.StorageBuffer, null, out var tl, out _) && tl is not null)
            {
                return ReflectedType.FromLayout(tl);
            }
        }

        return null;
    }
}
