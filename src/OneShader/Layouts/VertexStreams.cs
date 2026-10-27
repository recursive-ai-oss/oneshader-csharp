using System;
using System.Collections.Generic;
using System.Globalization;
using OneShader.Layouts.Metal;
using OneShader.Layouts.Vulkan;
using OneShader.Layouts.WebGpu;

namespace OneShader.Layouts;

/// <summary>
/// One vertex buffer of a split vertex input: it feeds the attributes from <see cref="FirstLocation"/> up to the next stream's first location
/// (the last stream takes all higher locations).
/// </summary>
/// <param name="FirstLocation">The lowest shader input location the stream feeds.</param>
/// <param name="Stride">Bytes between elements; a positive multiple of 4.</param>
/// <param name="StepMode">Whether the buffer advances per vertex or per instance.</param>
public readonly record struct VertexStream(uint FirstLocation, uint Stride, VertexStepMode StepMode = VertexStepMode.Vertex);

/// <summary>One vertex buffer of a <see cref="VertexStreamLayout"/> with its attributes.</summary>
/// <param name="FirstLocation">The lowest location the stream feeds.</param>
/// <param name="Stride">Bytes between elements.</param>
/// <param name="StepMode">Per vertex or per instance.</param>
/// <param name="Attributes">The attributes read from this stream in location order; offsets are relative to the start of one element of the stream. Empty when the entry point reads nothing from it.</param>
public sealed record VertexStreamInfo(uint FirstLocation, uint Stride, VertexStepMode StepMode, IReadOnlyList<VertexAttribute> Attributes);

/// <summary>
/// A vertex input spread over several vertex buffers, for example a per-vertex mesh stream and a per-instance transform stream. Stream
/// <c>i</c> is vertex buffer slot <c>i</c> in WebGPU, Vulkan binding <c>firstBinding + i</c> and Metal buffer <c>baseBufferIndex - i</c>. Streams
/// the entry point reads nothing from are kept, so slot numbers do not depend on the entry point.
/// </summary>
public sealed class VertexStreamLayout
{
    internal VertexStreamLayout(VertexStreamInfo[] streams) => Streams = streams;

    /// <summary>The streams, ordered by first location.</summary>
    public IReadOnlyList<VertexStreamInfo> Streams { get; }

    /// <summary>The Vulkan vertex input state: one binding description per stream and one attribute description per attribute.</summary>
    /// <param name="firstBinding">The vertex buffer binding of the first stream; default 0.</param>
    /// <returns>The binding and attribute descriptions.</returns>
    public VertexInputInfo ToVulkan(uint firstBinding = 0)
    {
        var bindings = new List<VertexInputBindingInfo>();
        var attributes = new List<VertexInputAttributeInfo>();
        for (int i = 0; i < Streams.Count; i++)
        {
            var s = Streams[i];
            uint binding = firstBinding + (uint)i;
            bindings.Add(new VertexInputBindingInfo(binding, s.Stride, s.StepMode == VertexStepMode.Instance ? VertexInputRate.Instance : VertexInputRate.Vertex));
            foreach (var a in s.Attributes)
            {
                attributes.Add(new VertexInputAttributeInfo(a.Location, binding, VertexLayout.VulkanFormat(a.Format), a.Offset));
            }
        }

        return new VertexInputInfo(bindings, attributes);
    }

    /// <summary>
    /// The Metal vertex descriptor data, one <see cref="MetalVertexDescriptor"/> per stream. Metal has 31 buffer slots (0 to 30) and the compiler
    /// puts vertex data at the top, so stream <c>i</c> is bound at <c>baseBufferIndex - i</c>, counting down from the base and clear of the
    /// resource slots, which count up from 0.
    /// </summary>
    /// <param name="error">Set when there are more streams than slots below the base.</param>
    /// <param name="baseBufferIndex">The buffer index of the first stream; default <see cref="LayoutSlots.DefaultMetalVertexBufferIndex"/> (30), or pass the generated <c>MslVertexBufferIndex</c>.</param>
    /// <returns>The descriptors, or null on error.</returns>
    public IReadOnlyList<MetalVertexDescriptor>? ToMetal(out LayoutError? error, uint baseBufferIndex = LayoutSlots.DefaultMetalVertexBufferIndex)
    {
        if ((uint)(Streams.Count - 1) > baseBufferIndex)
        {
            error = new LayoutError(LayoutErrorKind.InvalidVertexInput, "There are " + Num((uint)Streams.Count) + " vertex streams but the first is at Metal buffer index " + Num(baseBufferIndex) + ", leaving room for " + Num(baseBufferIndex + 1) + ". Use fewer streams or a higher base index.");
            return null;
        }

        var result = new MetalVertexDescriptor[Streams.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var s = Streams[i];
            uint buffer = baseBufferIndex - (uint)i;
            var attributes = new MetalVertexAttribute[s.Attributes.Count];
            for (int j = 0; j < attributes.Length; j++)
            {
                var a = s.Attributes[j];
                attributes[j] = new MetalVertexAttribute(a.Location, VertexLayout.MetalFormat(a.Format), a.Offset, buffer);
            }

            result[i] = new MetalVertexDescriptor(buffer, s.Stride, s.StepMode == VertexStepMode.Instance ? VertexStepFunction.PerInstance : VertexStepFunction.PerVertex, attributes);
        }

        error = null;
        return result;
    }

    /// <summary>The WebGPU <c>GPUVertexBufferLayout</c> list for <c>vertex.buffers</c>: stream <c>i</c> is slot <c>i</c>.</summary>
    /// <returns>One layout per stream, with <c>stepMode</c> set.</returns>
    public IReadOnlyList<VertexBufferLayoutInfo> ToWebGpu()
    {
        var result = new VertexBufferLayoutInfo[Streams.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var s = Streams[i];
            var attributes = new VertexAttributeInfo[s.Attributes.Count];
            for (int j = 0; j < attributes.Length; j++)
            {
                var a = s.Attributes[j];
                attributes[j] = new VertexAttributeInfo(VertexLayout.WebGpuFormat(a.Format), a.Offset, a.Location);
            }

            result[i] = new VertexBufferLayoutInfo(s.Stride, s.StepMode, attributes);
        }

        return result;
    }

    private static string Num(uint v) => v.ToString(CultureInfo.InvariantCulture);
}

public sealed partial class VertexLayout
{
    /// <summary>
    /// Spreads the attributes over several vertex buffers. Stream <c>i</c> feeds the locations from its <see cref="VertexStream.FirstLocation"/>
    /// up to the next stream's. Offsets are relative to the stream: unless <paramref name="overrides"/> gives one, an attribute keeps its place
    /// in the interleaved layout minus the offset of the first attribute of its stream (so a layout packed in location order stays packed).
    /// <paramref name="overrides"/> then apply as in <see cref="TryWithOverrides"/> (format by location, offset relative to the stream);
    /// its <see cref="VertexLayoutOverrides.Stride"/> must be null, since each stream has its own stride. Streams may be given in any order;
    /// the result is ordered by first location.
    /// </summary>
    /// <remarks>
    /// Fails with <see cref="LayoutErrorKind.InvalidVertexInput"/> for: no streams, two streams with one first location, an attribute at a
    /// location below the first stream, a stride that is not a positive multiple of 4, an attribute that does not fit in its stream's stride or
    /// is misaligned (offset a multiple of min(4, format size)), overlapping attributes in a stream, and the override errors of
    /// <see cref="TryWithOverrides"/>. The message names the location and the fix.
    /// </remarks>
    /// <param name="streams">The streams.</param>
    /// <param name="result">The streams with their attributes on success.</param>
    /// <param name="error">What is wrong and what to change.</param>
    /// <param name="overrides">Host formats and relative offsets; none by default.</param>
    /// <returns>True on success.</returns>
    public bool TrySplit(ReadOnlySpan<VertexStream> streams, out VertexStreamLayout? result, out LayoutError? error, VertexLayoutOverrides? overrides = null)
    {
        result = null;
        if (streams.IsEmpty)
        {
            error = Unsupported("A split vertex input needs at least one stream.");
            return false;
        }

        if (overrides?.Stride is not null)
        {
            error = Unsupported("VertexLayoutOverrides.Stride does not apply to a split vertex input; give each stream its own stride in VertexStream.");
            return false;
        }

        var sorted = streams.ToArray();
        Array.Sort(sorted, static (a, b) => a.FirstLocation.CompareTo(b.FirstLocation));
        for (int i = 0; i < sorted.Length; i++)
        {
            if (i > 0 && sorted[i].FirstLocation == sorted[i - 1].FirstLocation)
            {
                error = Unsupported("Two vertex streams start at location " + Num(sorted[i].FirstLocation) + "; give each stream its own first location.");
                return false;
            }

            if (sorted[i].Stride == 0 || (sorted[i].Stride & 3) != 0)
            {
                error = Unsupported("The vertex stream starting at location " + Num(sorted[i].FirstLocation) + " has stride " + Num(sorted[i].Stride) + ", which must be a positive multiple of 4 (WebGPU's rule, the strictest of the three APIs).");
                return false;
            }
        }

        if (overrides is not null)
        {
            foreach (var key in overrides.Attributes.Keys)
            {
                bool found = false;
                foreach (var a in _attributes)
                {
                    found |= a.Location == key;
                }

                if (!found)
                {
                    error = Unsupported("The vertex input has no attribute at location " + Num(key) + ", so its format or offset cannot be overridden. Check the location, or the [[vk::location]] in the shader.");
                    return false;
                }
            }
        }

        var lists = new List<VertexAttribute>[sorted.Length];
        var bases = new uint[sorted.Length];
        for (int i = 0; i < lists.Length; i++)
        {
            lists[i] = [];
            bases[i] = uint.MaxValue;
        }

        // Which stream owns each attribute, and where its stream starts in the interleaved layout.
        var owner = new int[_attributes.Length];
        for (int k = 0; k < _attributes.Length; k++)
        {
            int s = -1;
            for (int i = 0; i < sorted.Length; i++)
            {
                if (sorted[i].FirstLocation <= _attributes[k].Location)
                {
                    s = i;
                }
            }

            if (s < 0)
            {
                error = Unsupported("Location " + Num(_attributes[k].Location) + " is in no vertex stream; the first stream starts at location " + Num(sorted[0].FirstLocation) + ". Start a stream at or below it.");
                return false;
            }

            owner[k] = s;
            bases[s] = Math.Min(bases[s], _attributes[k].Offset);
        }

        for (int k = 0; k < _attributes.Length; k++)
        {
            var a = _attributes[k];
            int s = owner[k];
            uint offset = a.Offset - bases[s];
            if (overrides is not null && overrides.Attributes.TryGetValue(a.Location, out var o))
            {
                var format = o.Format ?? a.Format;
                if (CheckFormat(a, format) is { } formatError)
                {
                    error = formatError;
                    return false;
                }

                a = new VertexAttribute(a.Location, format, a.Offset);
                offset = o.Offset ?? offset;
            }

            a = new VertexAttribute(a.Location, a.Format, offset);
            uint size = FormatSize(a.Format);
            if (a.Offset % Math.Min(4, size) != 0)
            {
                error = Unsupported("Location " + Num(a.Location) + ": offset " + Num(a.Offset) + " in its stream must be a multiple of " + Num(Math.Min(4, size)) + " for " + a.Format + ".");
                return false;
            }

            if ((ulong)a.Offset + size > sorted[s].Stride)
            {
                error = Unsupported("Location " + Num(a.Location) + ": " + a.Format + " at offset " + Num(a.Offset) + " of the stream starting at location " + Num(sorted[s].FirstLocation) + " ends at byte " + Num(a.Offset + size)
                    + ", past that stream's stride of " + Num(sorted[s].Stride) + ". Raise the stride or lower the offset.");
                return false;
            }

            foreach (var other in lists[s])
            {
                if (a.Offset < other.Offset + FormatSize(other.Format) && other.Offset < a.Offset + size)
                {
                    error = Unsupported("Locations " + Num(other.Location) + " and " + Num(a.Location) + " overlap in the stream starting at location " + Num(sorted[s].FirstLocation) + " (bytes " + Num(other.Offset) + ".." + Num(other.Offset + FormatSize(other.Format))
                        + " and " + Num(a.Offset) + ".." + Num(a.Offset + size) + "). Give one of them another offset.");
                    return false;
                }
            }

            lists[s].Add(a);
        }

        var infos = new VertexStreamInfo[sorted.Length];
        for (int i = 0; i < infos.Length; i++)
        {
            infos[i] = new VertexStreamInfo(sorted[i].FirstLocation, sorted[i].Stride, sorted[i].StepMode, lists[i].ToArray());
        }

        result = new VertexStreamLayout(infos);
        error = null;
        return true;
    }
}
