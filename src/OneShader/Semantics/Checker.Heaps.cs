using System;
using System.Collections.Generic;
using OneShader.Diagnostics;
using OneShader.Syntax;
using OneShader.Types;

namespace OneShader.Semantics;

// Shader Model 6.6 dynamic resources: ResourceDescriptorHeap[i] and SamplerDescriptorHeap[i]. The element has no type of its own;
// it becomes a resource when it initializes, is assigned to, or is cast to a resource variable. Like DXC's SPIR-V output, each
// resource type read from a heap becomes an unbounded descriptor array of that type, and all arrays of one heap share one binding.
internal sealed partial class Checker
{
    private readonly List<ResourceDecl> _heapDecls = [];
    private readonly Dictionary<string, ResourceDecl> _heapByType = new(StringComparer.Ordinal);

    /// <summary>The heap variables created for the types the shader reads, in order of first use.</summary>
    public IReadOnlyList<ResourceDecl> HeapDecls => _heapDecls;

    private static bool IsHeapName(QualifiedName name, out DescriptorHeapType heap)
    {
        heap = DescriptorHeapType.ResourceHeap;
        if (name.Parts.Count != 1)
        {
            return false;
        }

        switch (name.Last.Text)
        {
            case "ResourceDescriptorHeap":
                return true;
            case "SamplerDescriptorHeap":
                heap = DescriptorHeapType.SamplerHeap;
                return true;
            default:
                return false;
        }
    }

    private HlslType CheckHeapIndex(IndexExpression ix, DescriptorHeapType heap, HlslType index)
    {
        if (heap.IsElement)
        {
            Err(SemanticCodes.BadDescriptorHeap, "A descriptor from " + (heap.IsSamplerHeap ? "SamplerDescriptorHeap" : "ResourceDescriptorHeap") + " cannot be indexed again. Assign it to a variable of a resource type first.", ix.Span);
            return HlslType.Error;
        }

        if (index is not ScalarType { IsInteger: true })
        {
            Err(SemanticCodes.TypeMismatch, "The index into " + heap + " must be an integer, not '" + index + "'.", ix.Index.Span);
            return HlslType.Error;
        }

        return heap.IsSamplerHeap ? DescriptorHeapType.SamplerElement : DescriptorHeapType.ResourceElement;
    }

    private static Expression StripParens(Expression e)
    {
        while (e is ParenthesizedExpression p)
        {
            e = p.Inner;
        }

        return e;
    }

    private static string HeapUsageHint(DescriptorHeapType element) => element.IsSamplerHeap
        ? "Assign it to a SamplerState variable, for example 'SamplerState s = SamplerDescriptorHeap[i];'."
        : "Assign it to a variable of the resource type you want, for example 'Texture2D<float4> t = ResourceDescriptorHeap[i];'.";

    /// <summary>Gives <c>Heap[i]</c> the resource type <paramref name="target"/> it is being read as, creating the heap variable of that type on first use.</summary>
    private bool ConvertHeapElement(Expression e, DescriptorHeapType from, HlslType target)
    {
        if (StripParens(e) is not IndexExpression ix || !from.IsElement)
        {
            Err(SemanticCodes.BadDescriptorHeap, "The heap " + from + " is not a value. Index it, as in ResourceDescriptorHeap[i], and assign the result to a resource variable.", e.Span);
            return false;
        }

        if (target is SamplerType != from.IsSamplerHeap || target is not (ResourceType or SamplerType))
        {
            string want = from.IsSamplerHeap ? "a SamplerState or SamplerComparisonState" : "a texture or buffer type";
            Err(SemanticCodes.BadDescriptorHeap, "Cannot use " + (from.IsSamplerHeap ? "SamplerDescriptorHeap" : "ResourceDescriptorHeap") + "[...] as '" + target + "': it holds " + want + ". "
                + (from.IsSamplerHeap ? "Samplers live in SamplerDescriptorHeap and textures and buffers in ResourceDescriptorHeap." : "Samplers live in SamplerDescriptorHeap; use that for sampler variables."), e.Span);
            return false;
        }

        if (target is ResourceType { ResourceKind: ResourceTypeKind.AppendStructuredBuffer or ResourceTypeKind.ConsumeStructuredBuffer })
        {
            Err(SemanticCodes.Unsupported, "'" + target + "' cannot be read from ResourceDescriptorHeap: it needs a hidden counter buffer of its own. Use RWStructuredBuffer and keep the count in a buffer you manage.", e.Span);
            return false;
        }

        var info = InfoOf(ix);
        info.HeapDecl = HeapDeclFor(from.IsSamplerHeap, target, ix.Span);
        info.TargetType = target;
        return info.HeapDecl is not null;
    }

    private ResourceDecl? HeapDeclFor(bool sampler, HlslType target, SourceSpan span)
    {
        string key = (sampler ? "S:" : "R:") + target;
        if (_heapByType.TryGetValue(key, out var existing))
        {
            return existing;
        }

        int ordinal = 0;
        foreach (var d in _heapDecls)
        {
            if ((d.Heap == HeapKind.Sampler) == sampler)
            {
                ordinal++;
            }
        }

        // DXC names its first variable after the heap and the following ones _0, _1, ...
        string baseName = sampler ? "SamplerDescriptorHeap" : "ResourceDescriptorHeap";
        var rd = new ResourceDecl
        {
            Name = ordinal == 0 ? baseName : baseName + "_" + (ordinal - 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Span = span,
            Type = new ArrayType(target, 0),
            Count = 0,
            Kind = KindOf(target),
            ResourceType = (target as ResourceType)?.ResourceKind ?? ResourceTypeKind.Texture2D,
            HlslTypeText = target.ToString(),
            Heap = sampler ? HeapKind.Sampler : HeapKind.Resource,
        };
        ApplyResourceLayout(rd, target, rd.Name, span, false);
        _heapDecls.Add(rd);
        _heapByType[key] = rd;
        return rd;
    }

    /// <summary>
    /// Binds the heaps once every other resource has its binding: DXC puts the resource heap on the first binding of set 0 that
    /// nothing else uses, then the sampler heap on the next one, unless the options name them. All variables of a heap share its binding.
    /// </summary>
    private void AssignHeapBindings(Dictionary<(int, int), ResourceDecl> owners, Func<int, int> take)
    {
        foreach (var kind in new[] { HeapKind.Resource, HeapKind.Sampler })
        {
            ResourceDecl? first = null;
            foreach (var d in _heapDecls)
            {
                if (d.Heap != kind)
                {
                    continue;
                }

                first ??= d;
            }

            if (first is null)
            {
                continue;
            }

            var requested = kind == HeapKind.Resource ? _opt.ResourceHeapBinding : _opt.SamplerHeapBinding;
            int set, binding;
            if (requested is { } r)
            {
                set = (int)r.Set;
                binding = (int)r.Binding;
                if (owners.TryGetValue((set, binding), out var clash))
                {
                    Err(SemanticCodes.BindingCollision, "The " + (kind == HeapKind.Resource ? "resource" : "sampler") + " heap was given set " + Num(set) + ", binding " + Num(binding)
                        + ", which '" + clash.Name + "' already uses. Choose another binding for the heap (-fvk-bind-" + (kind == HeapKind.Resource ? "resource" : "sampler") + "-heap) or move the resource.", first.Span);
                }
            }
            else
            {
                set = 0;
                binding = take(0);
            }

            owners[(set, binding)] = first;
            foreach (var d in _heapDecls)
            {
                if (d.Heap == kind)
                {
                    d.Set = set;
                    d.Binding = binding;
                    Resources.Add(d);
                }
            }
        }
    }
}
