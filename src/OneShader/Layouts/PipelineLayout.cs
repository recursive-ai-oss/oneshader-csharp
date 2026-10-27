using System;
using System.Collections.Generic;
using System.Globalization;
using OneShader.Types;

namespace OneShader.Layouts;

/// <summary>One binding of a merged bind group: the resource, every stage that uses it, and its Metal index per stage.</summary>
public sealed class BindingEntry
{
    private readonly uint[] _metal = [LayoutSlots.None, LayoutSlots.None, LayoutSlots.None];

    internal BindingEntry(ResourceLayout resource)
    {
        Resource = resource;
    }

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

    /// <summary>The HLSL variable name.</summary>
    public string Name { get; }

    /// <summary>Bytes to push.</summary>
    public uint Size { get; }

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
                    existing = new BindingEntry(r);
                    bindings.Add(existing);
                }
                else
                {
                    var o = existing.Resource;
                    if (o.Kind != r.Kind || o.Count != r.Count || (r.Kind != ResourceKind.Sampler && o.Type != r.Type) || o.Comparison != r.Comparison)
                    {
                        error = new LayoutError(
                            LayoutErrorKind.ResourceConflict,
                            "Set " + Num(r.Set) + " binding " + Num(r.Binding) + " is '" + o.Name + "' (" + o.Kind + ", count " + Num(o.Count) + ") in one entry point and '" + r.Name + "' ("
                            + r.Kind + ", count " + Num(r.Count) + ") in '" + e.Name + "'. Renumber one of them with [[vk::binding]] or declare them identically.");
                        return false;
                    }

                    existing.Resource = o with { Stages = o.Stages | r.Stages };
                }

                existing.SetMetal(e.Stage, r.MetalIndex);
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

                if (block is null)
                {
                    block = new PushConstantBlock(p.Name, p.Size) { WgslGroup = p.WgslGroup, WgslBinding = p.WgslBinding };
                    pushes.Add(block);
                }
                else if (block.WgslGroup != p.WgslGroup || block.WgslBinding != p.WgslBinding)
                {
                    error = new LayoutError(LayoutErrorKind.PushConstantConflict, "The push-constant block '" + p.Name + "' is at a different WebGPU group or binding in '" + e.Name + "' than in the other entry point; compile both from the same file.");
                    return false;
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

    internal static int MetalClass(ResourceKind kind) => kind switch
    {
        ResourceKind.Sampler => 2,
        ResourceKind.SampledTexture or ResourceKind.StorageTexture or ResourceKind.UniformTexelBuffer or ResourceKind.StorageTexelBuffer => 1,
        _ => 0,
    };

    private static string Num(uint v) => v.ToString(CultureInfo.InvariantCulture);
}
