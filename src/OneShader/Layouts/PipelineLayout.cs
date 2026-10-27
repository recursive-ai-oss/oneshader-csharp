using System;
using System.Collections.Generic;
using System.Globalization;
using OneShader.Reflection;
using OneShader.Types;

namespace OneShader.Layouts;

/// <summary>One binding of a merged bind group: the resource, every stage that uses it, and its Metal index per stage.</summary>
public sealed class BindingEntry
{
    private readonly uint[] _metal = [LayoutSlots.None, LayoutSlots.None, LayoutSlots.None];

    internal BindingEntry(ResourceLayout resource, string firstEntry)
    {
        Resource = resource;
        FirstEntry = firstEntry;
    }

    // Union layouts only: the entry point that declared the slot first, and per stage why two entry points disagree on its Metal index.
    internal string FirstEntry { get; }

    internal string?[] MetalConflict { get; } = new string?[3];

    /// <summary>The resource; <see cref="ResourceLayout.Stages"/> is the union over the combined entry points.</summary>
    public ResourceLayout Resource { get; internal set; }

    /// <summary>The Metal argument index the given stage uses, or <see cref="LayoutSlots.None"/>.</summary>
    /// <param name="stage">The stage.</param>
    /// <returns>The index within the resource's argument class.</returns>
    public uint MetalIndexFor(ShaderStage stage) => _metal[(int)stage];

    internal void SetMetal(ShaderStage stage, uint index) => _metal[(int)stage] = index;
}

/// <summary>One descriptor set / bind group of a merged layout.</summary>
public sealed class BindGroupLayout
{
    internal BindGroupLayout(uint set, BindingEntry[] entries)
    {
        Set = set;
        Entries = entries;
    }

    /// <summary>The set number (Vulkan) / group index (WebGPU).</summary>
    public uint Set { get; }

    /// <summary>The bindings, sorted by binding number. A set no shader uses (a gap below a used set) has none.</summary>
    public IReadOnlyList<BindingEntry> Entries { get; }
}

/// <summary>A push-constant block of a merged layout.</summary>
public sealed class PushConstantBlock
{
    private readonly uint[] _metal = [LayoutSlots.None, LayoutSlots.None, LayoutSlots.None];

    internal PushConstantBlock(string name, uint size)
    {
        Name = name;
        Size = size;
    }

    // Union layouts only: every entry point's view of the block, and why entry points disagree on a target slot.
    internal List<(string Entry, string Name, uint Size, ReflectedType? Type)> Sources { get; } = [];

    internal string?[] MetalConflict { get; } = new string?[3];

    internal string? WgslConflict { get; set; }

    /// <summary>The HLSL variable name; when blocks of several structs were merged, the name of the largest.</summary>
    public string Name { get; internal set; }

    /// <summary>Bytes to push; the size of the largest when blocks were merged.</summary>
    public uint Size { get; internal set; }

    /// <summary>The stages that read the block.</summary>
    public ShaderStages Stages { get; internal set; }

    /// <summary>The WebGPU bind group of the emulating uniform buffer, or <see cref="LayoutSlots.None"/>.</summary>
    public uint WgslGroup { get; internal set; } = LayoutSlots.None;

    /// <summary>The WebGPU binding of the emulating uniform buffer, or <see cref="LayoutSlots.None"/>.</summary>
    public uint WgslBinding { get; internal set; } = LayoutSlots.None;

    /// <summary>The Metal buffer index the given stage reads the block from, or <see cref="LayoutSlots.None"/>.</summary>
    /// <param name="stage">The stage.</param>
    /// <returns>The buffer index.</returns>
    public uint MetalIndexFor(ShaderStage stage) => _metal[(int)stage];

    internal void SetMetal(ShaderStage stage, uint index) => _metal[(int)stage] = index;
}

/// <summary>
/// The pipeline layout of one or more entry points that run together (a vertex and a pixel entry point, or one compute entry point):
/// bind groups with merged stage visibility and push-constant blocks. It is plain data with no graphics API behind it; convert it with
/// <see cref="ToVulkan"/>, <see cref="TryToMetal"/> or <see cref="TryToWebGpu"/>. Creating it and converting it allocate; do both once
/// at start-up or when a shader loads, not per frame.
/// </summary>
public sealed partial class PipelineLayout
{
    private readonly EntryLayout[] _entries;

    private PipelineLayout(EntryLayout[] entries, BindGroupLayout[] groups, PushConstantBlock[] pushes)
    {
        _entries = entries;
        Groups = groups;
        PushConstants = pushes;
    }

    /// <summary>The combined entry points, in the order given.</summary>
    public IReadOnlyList<EntryLayout> Entries => _entries;

    /// <summary>
    /// Bind groups indexed by set number: <c>Groups[i].Set == i</c>, from 0 to the highest set any entry point uses. Unused sets in between
    /// are present and empty, as Vulkan needs them.
    /// </summary>
    public IReadOnlyList<BindGroupLayout> Groups { get; }

    /// <summary>
    /// Push-constant blocks. Entry points that read a block of the same name and size share one (their stages are combined); a pipeline
    /// has at most one block per stage, so this is usually zero or one block.
    /// </summary>
    public IReadOnlyList<PushConstantBlock> PushConstants { get; }

    /// <summary>The subgroup (wave) operations any entry point of the pipeline uses.</summary>
    public SubgroupOperations SubgroupOperations
    {
        get
        {
            var all = SubgroupOperations.None;
            foreach (var e in _entries)
            {
                all |= e.SubgroupOperations;
            }

            return all;
        }
    }

    /// <summary>The entry point of the given stage, or null.</summary>
    /// <param name="stage">The stage.</param>
    /// <returns>The entry layout.</returns>
    public EntryLayout? EntryFor(ShaderStage stage)
    {
        foreach (var e in _entries)
        {
            if (e.Stage == stage)
            {
                return e;
            }
        }

        return null;
    }

    /// <summary>
    /// Combines entry points into one pipeline layout. Resources at the same set and binding must agree on kind, count and type
    /// (names may differ) and get the union of the stages; push-constant blocks of the same name and size are shared.
    /// </summary>
    /// <param name="entries">The entry points; at most one per stage.</param>
    /// <param name="layout">The layout on success.</param>
    /// <param name="error">What is wrong on failure; never an exception.</param>
    /// <returns>True on success.</returns>
    public static bool TryCreate(ReadOnlySpan<EntryLayout> entries, out PipelineLayout? layout, out LayoutError? error)
    {
        layout = null;
        if (entries.IsEmpty)
        {
            error = new LayoutError(LayoutErrorKind.NoEntryPoints, "A pipeline layout needs at least one entry point.");
            return false;
        }

        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i] is null)
            {
                throw new ArgumentException("entries must not contain null", nameof(entries));
            }

            for (int j = 0; j < i; j++)
            {
                if (entries[j].Stage == entries[i].Stage)
                {
                    error = new LayoutError(LayoutErrorKind.DuplicateStage, "The entry points '" + entries[j].Name + "' and '" + entries[i].Name + "' are both " + entries[i].Stage + " shaders; a pipeline has one entry point per stage.");
                    return false;
                }
            }
        }

        return Build(entries, false, out layout, out error);
    }

    /// <summary>
    /// Combines any number of entry points into one pipeline layout that every pipeline of a material system can share: several entry
    /// points per stage are allowed (for example ten forward-pass pixel shaders and three post-effect ones), unlike <see cref="TryCreate"/>.
    /// </summary>
    /// <remarks>
    /// Resources at the same set and binding must agree on kind, count, type, texel type and storage format (names may differ); they merge and
    /// their stages are united. A disagreement is a <see cref="LayoutErrorKind.ResourceConflict"/> naming both entry points and the slot.
    /// Push-constant blocks of the same name and size are shared; blocks of different structs merge into one block (one range, all stages) as large as the largest
    /// when their members that overlap in bytes have the same type at the same offset (needs the layouts built by
    /// <see cref="EntryLayout.TryCreate"/>, which carry <see cref="EntryLayout.PushConstantType"/>), otherwise
    /// <see cref="LayoutErrorKind.PushConstantConflict"/>. Entry points compiled from different files have different Metal indices and WebGPU
    /// push-constant slots; that is not an error here (Vulkan does not care) but <see cref="TryToMetal"/> and <see cref="TryToWebGpu"/> report
    /// a slot on which two entry points of one stage disagree. <see cref="EntryFor"/> returns the first entry point of a stage; use
    /// <see cref="Entries"/> for all of them.
    /// </remarks>
    /// <param name="entries">The entry points, at least one.</param>
    /// <param name="layout">The layout on success.</param>
    /// <param name="error">What is wrong on failure; never an exception.</param>
    /// <returns>True on success.</returns>
    public static bool TryCreateUnion(ReadOnlySpan<EntryLayout> entries, out PipelineLayout? layout, out LayoutError? error)
    {
        layout = null;
        if (entries.IsEmpty)
        {
            error = new LayoutError(LayoutErrorKind.NoEntryPoints, "A pipeline layout needs at least one entry point.");
            return false;
        }

        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i] is null)
            {
                throw new ArgumentException("entries must not contain null", nameof(entries));
            }
        }

        return Build(entries, true, out layout, out error);
    }

    private static bool Build(ReadOnlySpan<EntryLayout> entries, bool union, out PipelineLayout? layout, out LayoutError? error)
    {
        layout = null;
        var bindings = new List<BindingEntry>();
        var pushes = new List<PushConstantBlock>();
        foreach (var e in entries)
        {
            foreach (var r in e.Resources)
            {
                BindingEntry? existing = null;
                foreach (var b in bindings)
                {
                    // A Metal-only build has no sets: the argument index stands in for the binding, and a buffer, a texture
                    // and a sampler can all be number 0 without being the same slot.
                    bool metalOnly = r.Set == 0 && r.Binding == r.MetalIndex && b.Resource.Binding == b.Resource.MetalIndex;
                    if (b.Resource.Set == r.Set && b.Resource.Binding == r.Binding && (!metalOnly || MetalClass(b.Resource.Kind) == MetalClass(r.Kind)))
                    {
                        existing = b;
                        break;
                    }
                }

                if (existing is null)
                {
                    existing = new BindingEntry(r, e.Name);
                    bindings.Add(existing);
                }
                else
                {
                    var o = existing.Resource;
                    bool differs = o.Kind != r.Kind || o.Count != r.Count || (r.Kind != ResourceKind.Sampler && o.Type != r.Type) || o.Comparison != r.Comparison;
                    if (union && !differs)
                    {
                        // The texel type decides the WebGPU sample type and the image format the storage format; a union shared by pipelines must agree.
                        differs = (r.Kind == ResourceKind.SampledTexture && o.Texel != r.Texel) || (r.Kind == ResourceKind.StorageTexture && (o.Texel != r.Texel || o.ImageFormat != r.ImageFormat));
                    }

                    if (differs)
                    {
                        error = union
                            ? new LayoutError(
                                LayoutErrorKind.ResourceConflict,
                                "Set " + Num(r.Set) + " binding " + Num(r.Binding) + " is '" + o.Name + "' (" + Describe(o) + ") in entry point '" + existing.FirstEntry + "' and '" + r.Name + "' (" + Describe(r)
                                + ") in entry point '" + e.Name + "'. Renumber one of them with [[vk::binding]] or declare them identically.")
                            : new LayoutError(
                                LayoutErrorKind.ResourceConflict,
                                "Set " + Num(r.Set) + " binding " + Num(r.Binding) + " is '" + o.Name + "' (" + o.Kind + ", count " + Num(o.Count) + ") in one entry point and '" + r.Name + "' ("
                                + r.Kind + ", count " + Num(r.Count) + ") in '" + e.Name + "'. Renumber one of them with [[vk::binding]] or declare them identically.");
                        return false;
                    }

                    existing.Resource = o with { Stages = o.Stages | r.Stages, DynamicOffset = o.DynamicOffset | r.DynamicOffset };
                }

                uint previous = existing.MetalIndexFor(e.Stage);
                if (union && previous != LayoutSlots.None && r.MetalIndex != LayoutSlots.None && previous != r.MetalIndex)
                {
                    existing.MetalConflict[(int)e.Stage] ??= "Set " + Num(r.Set) + " binding " + Num(r.Binding) + " has Metal index " + Num(previous) + " in entry point '" + existing.FirstEntry + "' and " + Num(r.MetalIndex)
                        + " in '" + e.Name + "', both " + e.Stage + " shaders of one pipeline. Metal has no sets, so such entry points cannot share a binding table; compile them from one file, or give the resources the same declaration order.";
                }
                else
                {
                    existing.SetMetal(e.Stage, r.MetalIndex);
                }
            }

            if (e.PushConstant is { } p)
            {
                PushConstantBlock? block = null;
                foreach (var b in pushes)
                {
                    if (b.Name == p.Name && b.Size == p.Size)
                    {
                        block = b;
                        break;
                    }
                }

                // A union is shared by pipelines of any stage mix and pushed with one range: every block merges into the first.
                bool merging = false;
                if (union && block is null && pushes.Count > 0)
                {
                    block = pushes[0];
                    merging = true;
                }

                if (union && block is not null)
                {
                    foreach (var src in block.Sources)
                    {
                        string? problem = PushProblem(src.Entry, src.Name, src.Size, src.Type, e.Name, p.Name, p.Size, e.PushConstantType, merging);
                        if (problem is not null)
                        {
                            error = new LayoutError(LayoutErrorKind.PushConstantConflict, problem);
                            return false;
                        }
                    }
                }

                if (block is null)
                {
                    block = new PushConstantBlock(p.Name, p.Size) { WgslGroup = p.WgslGroup, WgslBinding = p.WgslBinding };
                    pushes.Add(block);
                }
                else if (block.WgslGroup != p.WgslGroup || block.WgslBinding != p.WgslBinding)
                {
                    if (!union)
                    {
                        error = new LayoutError(LayoutErrorKind.PushConstantConflict, "The push-constant block '" + p.Name + "' is at a different WebGPU group or binding in '" + e.Name + "' than in the other entry point; compile both from the same file.");
                        return false;
                    }

                    block.WgslConflict ??= "The push-constant block is at WebGPU group " + Num(block.WgslGroup) + " binding " + Num(block.WgslBinding) + " in entry point '" + block.Sources[0].Entry + "' and at group " + Num(p.WgslGroup)
                        + " binding " + Num(p.WgslBinding) + " in '" + e.Name + "'; compile the entry points from one file.";
                }

                if (union)
                {
                    block.Sources.Add((e.Name, p.Name, p.Size, e.PushConstantType));
                    if (p.Size > block.Size)
                    {
                        block.Size = p.Size;
                        block.Name = p.Name;
                    }

                    uint prevMetal = block.MetalIndexFor(e.Stage);
                    if (prevMetal != LayoutSlots.None && p.MetalIndex != LayoutSlots.None && prevMetal != p.MetalIndex)
                    {
                        block.MetalConflict[(int)e.Stage] ??= "The push-constant block is at Metal buffer index " + Num(prevMetal) + " in one " + e.Stage + " entry point and " + Num(p.MetalIndex) + " in '" + e.Name
                            + "'; compile the entry points from one file.";
                        block.Stages |= p.Stages;
                        continue;
                    }
                }

                block.Stages |= p.Stages;
                block.SetMetal(e.Stage, p.MetalIndex);
            }
        }

        // Vulkan allows each stage in one push-constant range only; two different blocks never share a stage because each entry point reads one.
        uint maxSet = 0;
        foreach (var b in bindings)
        {
            maxSet = Math.Max(maxSet, b.Resource.Set);
        }

        var groups = new BindGroupLayout[maxSet + 1];
        for (uint s = 0; s <= maxSet; s++)
        {
            var list = new List<BindingEntry>();
            foreach (var b in bindings)
            {
                if (b.Resource.Set == s)
                {
                    list.Add(b);
                }
            }

            list.Sort(static (a, b) => a.Resource.Binding.CompareTo(b.Resource.Binding));
            groups[s] = new BindGroupLayout(s, list.ToArray());
        }

        layout = new PipelineLayout(entries.ToArray(), groups, pushes.ToArray());
        error = null;
        return true;
    }

    /// <summary>
    /// As <see cref="TryCreate"/> for entry points known to fit together (all from one shader file, which the compiler has already checked).
    /// </summary>
    /// <param name="entries">The entry points.</param>
    /// <returns>The layout.</returns>
    /// <exception cref="InvalidOperationException">The entry points conflict; the message is the <see cref="LayoutError"/> text.</exception>
    public static PipelineLayout Create(params ReadOnlySpan<EntryLayout> entries)
    {
        if (!TryCreate(entries, out var layout, out var error))
        {
            throw new InvalidOperationException(error!.Message);
        }

        return layout!;
    }

    private static string Describe(ResourceLayout r) => r.Kind + ", " + (r.Kind == ResourceKind.Sampler ? (r.Comparison ? "comparison" : "sampler") : r.Type.ToString()) + (r.Kind == ResourceKind.SampledTexture || r.Kind == ResourceKind.StorageTexture ? " of " + r.Texel : "")
        + (r.ImageFormat.Length > 0 ? " [" + r.ImageFormat + "]" : "") + ", count " + Num(r.Count);

    private static string TypeText(ReflectedType t) => t.Kind switch
    {
        ReflectedTypeKind.Scalar => t.Scalar.ToString().ToLowerInvariant(),
        ReflectedTypeKind.Vector => t.Scalar.ToString().ToLowerInvariant() + Num(t.Rows),
        ReflectedTypeKind.Matrix => t.Scalar.ToString().ToLowerInvariant() + Num(t.Rows) + "x" + Num(t.Columns),
        ReflectedTypeKind.Array => (t.Element is null ? "?" : TypeText(t.Element)) + "[" + Num(t.ArrayLength) + "]",
        _ => "struct " + t.Name,
    };

    private static bool SameShape(ReflectedType a, ReflectedType b) =>
        a.Kind == b.Kind && a.Scalar == b.Scalar && a.Rows == b.Rows && a.Columns == b.Columns && a.Size == b.Size && a.ArrayLength == b.ArrayLength && a.ArrayStride == b.ArrayStride
        && (a.Kind != ReflectedTypeKind.Struct || a.Name == b.Name);

    private static IReadOnlyList<ReflectedMember> MembersOf(ReflectedType t) =>
        t.Kind == ReflectedTypeKind.Struct ? t.Members : [new ReflectedMember("(block)", 0, t)];

    // Null when the two views of a push-constant block can share one range: same bytes where their members overlap.
    private static string? PushProblem(string entryA, string nameA, uint sizeA, ReflectedType? typeA, string entryB, string nameB, uint sizeB, ReflectedType? typeB, bool differentBlocks)
    {
        if (typeA is null || typeB is null)
        {
            return differentBlocks
                ? "The push-constant blocks '" + nameA + "' (" + Num(sizeA) + " bytes, entry point '" + entryA + "') and '" + nameB + "' (" + Num(sizeB) + " bytes, entry point '" + entryB
                    + "') differ, and there is no member layout to check that they agree where they overlap. Give the entry points one push-constant struct, or build their layouts with EntryLayout.TryCreate from reflection."
                : null;
        }

        foreach (var ma in MembersOf(typeA))
        {
            foreach (var mb in MembersOf(typeB))
            {
                bool overlap = ma.Offset < mb.Offset + mb.Type.Size && mb.Offset < ma.Offset + ma.Type.Size;
                if (overlap && (ma.Offset != mb.Offset || !SameShape(ma.Type, mb.Type)))
                {
                    return "The push-constant blocks '" + nameA + "' (entry point '" + entryA + "') and '" + nameB + "' (entry point '" + entryB + "') overlap differently: '" + ma.Name + "' is " + TypeText(ma.Type) + " at offset "
                        + Num(ma.Offset) + " in the first and '" + mb.Name + "' is " + TypeText(mb.Type) + " at offset " + Num(mb.Offset) + " in the second. Make the members that share bytes the same type at the same offset.";
                }
            }
        }

        return null;
    }

    internal static int MetalClass(ResourceKind kind) => kind switch
    {
        ResourceKind.Sampler => 2,
        ResourceKind.SampledTexture or ResourceKind.StorageTexture or ResourceKind.UniformTexelBuffer or ResourceKind.StorageTexelBuffer => 1,
        _ => 0,
    };

    private static string Num(uint v) => v.ToString(CultureInfo.InvariantCulture);
}
