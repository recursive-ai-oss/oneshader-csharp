using System;

namespace OneShader.IR;

/// <summary>The storage-texture formats <c>[[vk::image_format("...")]]</c> accepts.</summary>
public static class ImageFormats
{
    // The index of each name is its SPIR-V ImageFormat enumerant, so the SPIR-V backend can use it directly.
    private static readonly string[] Table =
    [
        "unknown", "rgba32f", "rgba16f", "r32f", "rgba8", "rgba8snorm", "rg32f", "rg16f", "r11g11b10f", "r16f",
        "rgba16", "rgb10a2", "rg16", "rg8", "r16", "r8", "rgba16snorm", "rg16snorm", "rg8snorm", "r16snorm",
        "r8snorm", "rgba32i", "rgba16i", "rgba8i", "r32i", "rg32i", "rg16i", "rg8i", "r16i", "r8i",
        "rgba32ui", "rgba16ui", "rgba8ui", "r32ui", "rgb10a2ui", "rg32ui", "rg16ui", "rg8ui", "r16ui", "r8ui",
    ];

    /// <summary>Looks up a format name; returns its SPIR-V ImageFormat value (never 0), or 0 when unknown.</summary>
    public static int Find(string name)
    {
        for (int i = 1; i < Table.Length; i++)
        {
            if (string.Equals(Table[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>True for the formats every Vulkan device supports for storage images without the extended-format capability.</summary>
    public static bool IsCore(int format) => format is 1 or 2 or 3 or 4 or 5 or 21 or 22 or 23 or 24 or 30 or 31 or 32 or 33;

    /// <summary>The sampled scalar a format reads and writes: 'f' float, 'i' signed, 'u' unsigned.</summary>
    public static char Class(int format) => format switch
    {
        >= 21 and <= 29 => 'i',
        >= 30 => 'u',
        _ => 'f',
    };
}
