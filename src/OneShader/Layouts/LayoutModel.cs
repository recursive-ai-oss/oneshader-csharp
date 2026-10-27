using System;
using System.Collections.Generic;
using OneShader.Reflection;
using OneShader.Types;

namespace OneShader.Layouts;

/// <summary>Why a layout could not be built or converted.</summary>
public enum LayoutErrorKind
{
    /// <summary>No entry point was given.</summary>
    NoEntryPoints,

    /// <summary>The reflection has no entry point of the requested name.</summary>
    UnknownEntryPoint,

    /// <summary>The reflection is missing information the layout needs (for example a binding slot).</summary>
    IncompleteReflection,

    /// <summary>Two entry points of the same stage were combined; a pipeline has one entry point per stage.</summary>
    DuplicateStage,

    /// <summary>Two entry points declare different resources at the same set and binding.</summary>
    ResourceConflict,

    /// <summary>Two entry points declare push-constant blocks that cannot share one pipeline layout.</summary>
    PushConstantConflict,

    /// <summary>The shader was not built for the target the description is for.</summary>
    MissingTarget,

    /// <summary>The target cannot express something the shader declares.</summary>
    Unsupported,
}

/// <summary>A layout problem returned as a value; the message says what is wrong and what to change.</summary>
/// <param name="Kind">The category.</param>
/// <param name="Message">What is wrong and how to fix it.</param>
public sealed record LayoutError(LayoutErrorKind Kind, string Message)
{
    /// <inheritdoc />
    public override string ToString() => Message;
}

/// <summary>
/// One resource as one entry point uses it: the slots on every target and what the WebGPU and Metal descriptions need to know about its type.
/// </summary>
/// <param name="Name">The HLSL name (<c>Hits.counter</c> for the hidden counter of an append or consume buffer).</param>
/// <param name="Set">Descriptor set (Vulkan) and bind group (WebGPU).</param>
/// <param name="Binding">Binding number in the set.</param>
/// <param name="Count">Array size; 1 for a single resource.</param>
/// <param name="Kind">The descriptor kind.</param>
/// <param name="Stages">The stages that use it; for an <see cref="EntryLayout"/> that is the entry point's own stage.</param>
/// <param name="MetalIndex">The Metal argument index within the resource's class, or <see cref="LayoutSlots.None"/> when Metal was not built.</param>
/// <param name="Type">For textures and buffers, the HLSL resource type; ignored for samplers.</param>
/// <param name="Texel">For textures, the scalar type of the texel (decides the WebGPU sample type).</param>
/// <param name="Comparison">For samplers: true for <c>SamplerComparisonState</c>.</param>
/// <param name="ImageFormat">For storage textures, the <c>[[vk::image_format]]</c> name (<c>rgba8</c>); empty if none.</param>
public readonly record struct ResourceLayout(
    string Name,
    uint Set,
    uint Binding,
    uint Count,
    ResourceKind Kind,
    ShaderStages Stages,
    uint MetalIndex = LayoutSlots.None,
    ResourceTypeKind Type = ResourceTypeKind.ConstantBuffer,
    ScalarKind Texel = ScalarKind.Float,
    bool Comparison = false,
    string ImageFormat = "");

/// <summary>Sentinel values shared by the layout types.</summary>
public static class LayoutSlots
{
    /// <summary>"No slot": the shader was not built for that target.</summary>
    public const uint None = uint.MaxValue;

    /// <summary>The Metal buffer index vertex attributes are read from when the shader does not say otherwise.</summary>
    public const uint DefaultMetalVertexBufferIndex = 30;
}

/// <summary>A push-constant block as one entry point declares it.</summary>
/// <param name="Name">The HLSL variable name.</param>
/// <param name="Size">Bytes to push.</param>
/// <param name="Stages">The stages that read it.</param>
/// <param name="MetalIndex">The Metal buffer index, or <see cref="LayoutSlots.None"/>.</param>
/// <param name="WgslGroup">The WebGPU bind group of the emulating uniform buffer, or <see cref="LayoutSlots.None"/>.</param>
/// <param name="WgslBinding">The WebGPU binding of the emulating uniform buffer, or <see cref="LayoutSlots.None"/>.</param>
public readonly record struct PushConstantLayout(
    string Name,
    uint Size,
    ShaderStages Stages,
    uint MetalIndex = LayoutSlots.None,
    uint WgslGroup = LayoutSlots.None,
    uint WgslBinding = LayoutSlots.None);

/// <summary>The interleaved vertex input of a vertex entry point.</summary>
public sealed partial class VertexLayout
{
    private readonly VertexAttribute[] _attributes;

    /// <summary>Creates a vertex layout.</summary>
    /// <param name="stride">Bytes of one vertex.</param>
    /// <param name="attributes">The attributes in location order.</param>
    public VertexLayout(uint stride, ReadOnlySpan<VertexAttribute> attributes)
    {
        Stride = stride;
        _attributes = attributes.ToArray();
    }

    /// <summary>Bytes of one vertex.</summary>
    public uint Stride { get; }

    /// <summary>The attributes, in shader order.</summary>
    public ReadOnlySpan<VertexAttribute> Attributes => _attributes;
}

/// <summary>
/// Everything a host needs from one entry point to describe a pipeline: the resources it uses, its push-constant block, its vertex input and
/// its thread-group size, with the slot on every target the shader was built for. Build it from reflection
/// (<see cref="TryCreate"/>) or take it from a generated entry class (<c>Sprite.VSMain.Layout</c>); both give equal values.
/// </summary>
public sealed class EntryLayout
{
    /// <summary>Creates an entry layout from parts; the generated code does this.</summary>
    /// <param name="name">The entry point name.</param>
    /// <param name="stage">The stage.</param>
    /// <param name="resources">The resources the entry point uses, with <see cref="ResourceLayout.Stages"/> set to this stage.</param>
    /// <param name="pushConstant">The push-constant block it reads, if any.</param>
    /// <param name="vertex">The vertex input, for vertex entry points.</param>
    /// <param name="workgroupSize">The thread-group size for compute entry points.</param>
    /// <param name="subgroupOperations">The subgroup (wave) operations the entry point uses.</param>
    public EntryLayout(string name, ShaderStage stage, ReadOnlySpan<ResourceLayout> resources, PushConstantLayout? pushConstant = null, VertexLayout? vertex = null, (uint X, uint Y, uint Z) workgroupSize = default, SubgroupOperations subgroupOperations = SubgroupOperations.None)
    {
        SubgroupOperations = subgroupOperations;
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
        Stage = stage;
        Resources = resources.ToArray();
        PushConstant = pushConstant;
        Vertex = vertex;
        WorkgroupSize = workgroupSize;
    }

    /// <summary>The entry point name as written in the HLSL.</summary>
    public string Name { get; }

    /// <summary>The stage.</summary>
    public ShaderStage Stage { get; }

    /// <summary>The resources this entry point uses (not the whole file's), in declaration order.</summary>
    public IReadOnlyList<ResourceLayout> Resources { get; }

    /// <summary>The push-constant block it reads, or null.</summary>
    public PushConstantLayout? PushConstant { get; }

    /// <summary>The vertex input of a vertex entry point; null for other stages.</summary>
    public VertexLayout? Vertex { get; }

    /// <summary>The thread-group size of a compute entry point; zeros otherwise.</summary>
    public (uint X, uint Y, uint Z) WorkgroupSize { get; }

    /// <summary>
    /// The subgroup (wave) operations the entry point uses, or <see cref="SubgroupOperations.None"/>. A Vulkan host checks them against
    /// <c>subgroupSupportedOperations</c>, a Metal host needs a GPU family with SIMD-group functions, a WebGPU host the <c>subgroups</c> feature.
    /// </summary>
    public SubgroupOperations SubgroupOperations { get; }

    /// <summary>The <see cref="ShaderStages"/> bit of <see cref="Stage"/>.</summary>
    public ShaderStages StageFlag => StageBit(Stage);

    internal static ShaderStages StageBit(ShaderStage s) => s switch
    {
        ShaderStage.Vertex => ShaderStages.Vertex,
        ShaderStage.Pixel => ShaderStages.Pixel,
        _ => ShaderStages.Compute,
    };

    /// <summary>
    /// Builds the layout of one entry point from reflection. Returns false with <paramref name="error"/> set (never throws) when the
    /// entry point is missing or a resource it uses has no slot.
    /// </summary>
    /// <param name="reflection">The reflection of the shader file, from <c>CompileResult.Reflection</c> or a generated class.</param>
    /// <param name="entryPoint">The entry point name.</param>
    /// <param name="layout">The layout on success.</param>
    /// <param name="error">The reason on failure.</param>
    /// <returns>True on success.</returns>
    public static bool TryCreate(ShaderReflection reflection, string entryPoint, out EntryLayout? layout, out LayoutError? error)
    {
        ArgumentNullException.ThrowIfNull(reflection);
        ArgumentNullException.ThrowIfNull(entryPoint);
        layout = null;
        EntryPointInfo? entry = null;
        foreach (var e in reflection.EntryPoints)
        {
            if (e.Name == entryPoint)
            {
                entry = e;
                break;
            }
        }

        if (entry is null)
        {
            error = new LayoutError(LayoutErrorKind.UnknownEntryPoint, "The shader '" + reflection.SourceName + "' has no entry point '" + entryPoint + "'. Check the name and the [shader(...)] attribute.");
            return false;
        }

        var stage = StageBit(entry.Stage);
        var resources = new List<ResourceLayout>();
        foreach (var used in entry.UsedResources)
        {
            ResourceInfo? r = null;
            foreach (var candidate in reflection.Resources)
            {
                if (candidate.Name == used)
                {
                    r = candidate;
                    break;
                }
            }

            if (r is null)
            {
                error = new LayoutError(LayoutErrorKind.IncompleteReflection, "The entry point '" + entryPoint + "' uses '" + used + "', which the reflection does not list.");
                return false;
            }

            if (!SlotsOf(r.Slots, out uint set, out uint binding, out uint metal))
            {
                error = new LayoutError(LayoutErrorKind.IncompleteReflection, "The resource '" + r.Name + "' has no slot in the reflection; compile with at least one target.");
                return false;
            }

            var texel = r.Layout is { Kind: ReflectedTypeKind.Scalar or ReflectedTypeKind.Vector } t ? t.Scalar : ScalarKind.Float;
            resources.Add(new ResourceLayout(r.Name, set, binding, r.Count, r.Kind, stage, metal, r.ResourceType, texel, r.HlslType == "SamplerComparisonState", r.ImageFormat));
            if (r.CounterSlots.Count > 0 && SlotsOf(r.CounterSlots, out uint cset, out uint cbinding, out uint cmetal))
            {
                resources.Add(new ResourceLayout(r.Name + ".counter", cset, cbinding, 1, ResourceKind.StorageBuffer, stage, cmetal, ResourceTypeKind.RWStructuredBuffer));
            }
        }

        PushConstantLayout? push = null;
        if (entry.PushConstant is { } pushName)
        {
            foreach (var p in reflection.PushConstants)
            {
                if (p.Name != pushName)
                {
                    continue;
                }

                uint metal = LayoutSlots.None, group = LayoutSlots.None, wbinding = LayoutSlots.None;
                foreach (var s in p.Slots)
                {
                    if (s.Target == CompileTarget.Msl)
                    {
                        metal = s.Binding;
                    }
                    else if (s.Target == CompileTarget.Wgsl)
                    {
                        group = s.Set;
                        wbinding = s.Binding;
                    }
                }

                push = new PushConstantLayout(p.Name, p.Layout.Size, stage, metal, group, wbinding);
            }
        }

        VertexLayout? vertex = null;
        if (entry.Stage == ShaderStage.Vertex)
        {
            var attributes = new List<VertexAttribute>();
            foreach (var v in entry.Inputs)
            {
                if (v.Location is { } location && v.Format is { } format)
                {
                    attributes.Add(new VertexAttribute(location, format, v.Offset));
                }
            }

            vertex = new VertexLayout(entry.VertexStride, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(attributes));
        }

        layout = new EntryLayout(entry.Name, entry.Stage, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(resources), push, vertex, entry.WorkgroupSize, entry.SubgroupOperations);
        error = null;
        return true;
    }

    // Vulkan and WebGPU share the SPIR-V numbers; a Metal-only build keeps the argument index in the slot (set 0), as the generated bindings do.
    private static bool SlotsOf(IReadOnlyList<TargetSlot> slots, out uint set, out uint binding, out uint metal)
    {
        set = 0;
        binding = 0;
        metal = LayoutSlots.None;
        bool found = false;
        bool primary = false;
        foreach (var s in slots)
        {
            if (s.Target == CompileTarget.Msl)
            {
                metal = s.Binding;
                if (!primary)
                {
                    set = 0;
                    binding = s.Binding;
                    found = true;
                }
            }
            else if (s.Target == CompileTarget.SpirV || !primary)
            {
                set = s.Set;
                binding = s.Binding;
                found = true;
                primary = true;
            }
        }

        return found;
    }
}
