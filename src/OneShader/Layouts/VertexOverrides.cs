using System;
using System.Collections.Generic;
using System.Globalization;

namespace OneShader.Layouts;

/// <summary>What the host feeds one vertex attribute instead of what the shader's own type implies. Unset members keep the layout's value.</summary>
/// <param name="Format">The format of the data in the vertex buffer, for example <see cref="VertexFormat.Unorm8x4"/> for a <c>float4</c> colour.</param>
/// <param name="Offset">Byte offset of the attribute inside the vertex.</param>
public readonly record struct VertexAttributeOverride(VertexFormat? Format = null, uint? Offset = null);

/// <summary>
/// Host choices for a vertex layout: the stride of the engine's own vertex and, by shader input location, the format and offset of
/// attributes whose data is stored differently from the shader's declared type. Nothing is overridden by default.
/// </summary>
public sealed class VertexLayoutOverrides
{
    /// <summary>Bytes between vertices. When null the layout's stride is kept.</summary>
    public uint? Stride { get; init; }

    /// <summary>Overrides by shader input location. A location the shader does not read is an error, since it is almost always a typo.</summary>
    public IReadOnlyDictionary<uint, VertexAttributeOverride> Attributes { get; init; } = new Dictionary<uint, VertexAttributeOverride>();
}

public sealed partial class VertexLayout
{
    private enum ComponentClass
    {
        Float,
        Sint,
        Uint,
    }

    /// <summary>Bytes one value of the format occupies in a vertex buffer.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The size in bytes; 0 for a value that is not a <see cref="VertexFormat"/>.</returns>
    public static uint FormatSize(VertexFormat format) => format switch
    {
        VertexFormat.Float32 or VertexFormat.Sint32 or VertexFormat.Uint32 or VertexFormat.Float16x2 or VertexFormat.Unorm8x4 or VertexFormat.Snorm8x4
            or VertexFormat.Uint8x4 or VertexFormat.Sint8x4 or VertexFormat.Unorm16x2 or VertexFormat.Snorm16x2 or VertexFormat.Uint16x2 or VertexFormat.Sint16x2 => 4,
        VertexFormat.Float32x2 or VertexFormat.Sint32x2 or VertexFormat.Uint32x2 or VertexFormat.Float16x4 or VertexFormat.Unorm16x4 or VertexFormat.Snorm16x4
            or VertexFormat.Uint16x4 or VertexFormat.Sint16x4 => 8,
        VertexFormat.Float32x3 or VertexFormat.Sint32x3 or VertexFormat.Uint32x3 => 12,
        VertexFormat.Float32x4 or VertexFormat.Sint32x4 or VertexFormat.Uint32x4 => 16,
        _ => 0,
    };

    private static ComponentClass ClassOf(VertexFormat format) => format switch
    {
        VertexFormat.Sint32 or VertexFormat.Sint32x2 or VertexFormat.Sint32x3 or VertexFormat.Sint32x4 or VertexFormat.Sint8x4 or VertexFormat.Sint16x2 or VertexFormat.Sint16x4 => ComponentClass.Sint,
        VertexFormat.Uint32 or VertexFormat.Uint32x2 or VertexFormat.Uint32x3 or VertexFormat.Uint32x4 or VertexFormat.Uint8x4 or VertexFormat.Uint16x2 or VertexFormat.Uint16x4 => ComponentClass.Uint,
        _ => ComponentClass.Float,
    };

    /// <summary>
    /// A copy of this layout with the host's stride, formats and offsets applied. A format must suit the shader's input type: float,
    /// unorm and snorm formats feed float inputs, uint formats feed uint inputs and sint formats feed int inputs. A format with a different
    /// number of components than the shader type is accepted: Vulkan, Metal and WebGPU all drop extra components and fill missing ones
    /// with 0 (and 1 for the fourth). Every attribute must also lie inside the stride, and its offset must be a multiple of
    /// min(4, format size), the strictest of the three APIs. Returns false with <paramref name="error"/> set (never throws) otherwise.
    /// The shader's type is taken from the layout's current formats, which are the ones derived from the shader.
    /// </summary>
    /// <param name="overrides">The host's choices.</param>
    /// <param name="layout">The new layout on success; this instance is not changed.</param>
    /// <param name="error">What is wrong and what to change.</param>
    /// <returns>True on success.</returns>
    public bool TryWithOverrides(VertexLayoutOverrides overrides, out VertexLayout? layout, out LayoutError? error)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        layout = null;
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

        uint stride = overrides.Stride ?? Stride;
        if (stride == 0 || (stride & 3) != 0)
        {
            error = Unsupported("The vertex stride " + Num(stride) + " must be a positive multiple of 4 (WebGPU's rule, the strictest of the three APIs).");
            return false;
        }

        var result = new VertexAttribute[_attributes.Length];
        for (int i = 0; i < result.Length; i++)
        {
            var a = _attributes[i];
            if (overrides.Attributes.TryGetValue(a.Location, out var o))
            {
                var format = o.Format ?? a.Format;
                if (CheckFormat(a, format) is { } formatError)
                {
                    error = formatError;
                    return false;
                }

                a = new VertexAttribute(a.Location, format, o.Offset ?? a.Offset);
            }

            uint size = FormatSize(a.Format);
            uint align = Math.Min(4, size);
            if (a.Offset % align != 0)
            {
                error = Unsupported("Location " + Num(a.Location) + ": offset " + Num(a.Offset) + " must be a multiple of " + Num(align) + " for " + a.Format + ".");
                return false;
            }

            if ((ulong)a.Offset + size > stride)
            {
                error = Unsupported("Location " + Num(a.Location) + ": " + a.Format + " at offset " + Num(a.Offset) + " ends at byte " + Num(a.Offset + size) + ", past the stride of " + Num(stride) + ". Raise the stride or lower the offset.");
                return false;
            }

            result[i] = a;
        }

        layout = new VertexLayout(stride, result);
        error = null;
        return true;
    }

    // Null when the host's format can feed the attribute as the shader reads it.
    private static LayoutError? CheckFormat(VertexAttribute a, VertexFormat format)
    {
        if (FormatSize(format) == 0)
        {
            return Unsupported("Location " + Num(a.Location) + ": " + ((int)format).ToString(CultureInfo.InvariantCulture) + " is not a VertexFormat.");
        }

        if (ClassOf(format) != ClassOf(a.Format))
        {
            string shaderType = ClassOf(a.Format) switch { ComponentClass.Float => "float", ComponentClass.Sint => "int", _ => "uint" };
            string suits = ClassOf(a.Format) switch { ComponentClass.Float => "a float, unorm, snorm or half format", ComponentClass.Sint => "a Sint format", _ => "a Uint format" };
            return Unsupported("Location " + Num(a.Location) + " is read as " + shaderType + " by the shader, so the format " + format + " does not fit; use " + suits + ", or change the type in the shader.");
        }

        return null;
    }

    private static LayoutError Unsupported(string message) => new(LayoutErrorKind.InvalidVertexInput, message);

    private static string Num(uint v) => v.ToString(CultureInfo.InvariantCulture);
}
