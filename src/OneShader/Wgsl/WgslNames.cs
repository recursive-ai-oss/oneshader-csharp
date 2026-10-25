using System;
using System.Collections.Generic;
using System.Text;

namespace OneShader.Wgsl;

/// <summary>
/// Turns source names into valid WGSL identifiers. A WGSL module may not use a keyword, a reserved word, or
/// a name that starts with two underscores, and the compiler also keeps clear of every predeclared type and
/// built-in function, so a user function called <c>select</c> cannot capture the backend's own calls.
/// </summary>
internal static class WgslNames
{
    // WGSL 16.1 (keywords), 16.2 (reserved words) and the predeclared names of chapters 6 and 17.
    private static readonly HashSet<string> Avoid = new(StringComparer.Ordinal)
    {
        // keywords
        "alias", "break", "case", "const", "const_assert", "continue", "continuing", "default", "diagnostic", "discard",
        "else", "enable", "false", "fn", "for", "if", "let", "loop", "override", "requires", "return", "struct", "switch",
        "true", "var", "while",
        // reserved words
        "NULL", "Self", "abstract", "active", "alignas", "alignof", "as", "asm", "asm_fragment", "async", "attribute",
        "auto", "await", "become", "cast", "catch", "class", "co_await", "co_return", "co_yield", "coherent",
        "column_major", "common", "compile", "compile_fragment", "concept", "const_cast", "consteval", "constexpr",
        "constinit", "crate", "debugger", "decltype", "delete", "demote", "demote_to_helper", "do", "dynamic_cast",
        "enum", "explicit", "export", "extends", "extern", "external", "fallthrough", "filter", "final", "finally",
        "friend", "from", "fxgroup", "get", "goto", "groupshared", "highp", "impl", "implements", "import", "inline",
        "instanceof", "interface", "layout", "lowp", "macro", "macro_rules", "match", "mediump", "meta", "mod", "module",
        "move", "mut", "mutable", "namespace", "new", "nil", "noexcept", "noinline", "nointerpolation", "non_coherent",
        "noncoherent", "noperspective", "null", "nullptr", "of", "operator", "package", "packoffset", "partition",
        "pass", "patch", "pixelfragment", "precise", "precision", "premerge", "priv", "protected", "pub", "public",
        "readonly", "ref", "regardless", "register", "reinterpret_cast", "require", "resource", "restrict", "self",
        "set", "shared", "sizeof", "smooth", "snorm", "static", "static_assert", "static_cast", "std", "subroutine",
        "super", "target", "template", "this", "thread_local", "throw", "trait", "try", "type", "typedef", "typeid",
        "typename", "typeof", "union", "unless", "unorm", "unsafe", "unsized", "use", "using", "varying", "virtual",
        "volatile", "wgsl", "where", "with", "writeonly", "yield",
        // predeclared types and enumerants
        "bool", "f16", "f32", "i32", "u32", "vec2", "vec3", "vec4", "mat2x2", "mat2x3", "mat2x4", "mat3x2", "mat3x3",
        "mat3x4", "mat4x2", "mat4x3", "mat4x4", "array", "atomic", "ptr", "sampler", "sampler_comparison",
        "texture_1d", "texture_2d", "texture_2d_array", "texture_3d", "texture_cube", "texture_cube_array",
        "texture_multisampled_2d", "texture_depth_2d", "texture_depth_2d_array", "texture_depth_cube",
        "texture_depth_cube_array", "texture_depth_multisampled_2d", "texture_storage_1d", "texture_storage_2d",
        "texture_storage_2d_array", "texture_storage_3d", "vec2f", "vec3f", "vec4f", "vec2i", "vec3i", "vec4i",
        "vec2u", "vec3u", "vec4u", "mat2x2f", "mat3x3f", "mat4x4f", "read", "write", "read_write", "function",
        "private", "workgroup", "uniform", "storage", "handle", "rgba8unorm", "rgba8snorm", "rgba8uint", "rgba8sint",
        "rgba16uint", "rgba16sint", "rgba16float", "r32uint", "r32sint", "r32float", "rg32uint", "rg32sint",
        "rg32float", "rgba32uint", "rgba32sint", "rgba32float", "bgra8unorm", "perspective", "linear", "flat", "center",
        "centroid", "sample", "first", "either", "vertex", "fragment", "compute", "position", "front_facing",
        "frag_depth", "sample_index", "sample_mask", "vertex_index", "instance_index", "local_invocation_id",
        "local_invocation_index", "global_invocation_id", "workgroup_id", "num_workgroups", "location", "builtin",
        "group", "binding", "workgroup_size", "interpolate", "align", "size", "id", "invariant",
        // built-in functions
        "abs", "acos", "acosh", "all", "any", "arrayLength", "asin", "asinh", "atan", "atan2", "atanh", "atomicAdd",
        "atomicAnd", "atomicCompareExchangeWeak", "atomicExchange", "atomicLoad", "atomicMax", "atomicMin", "atomicOr",
        "atomicStore", "atomicSub", "atomicXor", "bitcast", "ceil", "clamp", "cos", "cosh", "countLeadingZeros",
        "countOneBits", "countTrailingZeros", "cross", "degrees", "determinant", "distance", "dot", "dot4I8Packed",
        "dot4U8Packed", "dpdx", "dpdxCoarse", "dpdxFine", "dpdy", "dpdyCoarse", "dpdyFine", "exp", "exp2", "extractBits",
        "faceForward", "firstLeadingBit", "firstTrailingBit", "floor", "fma", "fract", "frexp", "insertBits",
        "inverseSqrt", "ldexp", "length", "log", "log2", "max", "min", "mix", "modf", "normalize", "pack2x16float",
        "pack2x16snorm", "pack2x16unorm", "pack4x8snorm", "pack4x8unorm", "pack4xI8", "pack4xU8", "pack4xI8Clamp",
        "pack4xU8Clamp", "pow", "quantizeToF16", "radians", "reflect", "refract", "reverseBits", "round", "saturate",
        "select", "sign", "sin", "sinh", "smoothstep", "sqrt", "step", "storageBarrier", "tan", "tanh",
        "textureBarrier", "textureDimensions", "textureGather", "textureGatherCompare", "textureLoad",
        "textureNumLayers", "textureNumLevels", "textureNumSamples", "textureSample", "textureSampleBias",
        "textureSampleBaseClampToEdge", "textureSampleCompare", "textureSampleCompareLevel", "textureSampleGrad",
        "textureSampleLevel", "textureStore", "transpose", "trunc", "unpack2x16float", "unpack2x16snorm",
        "unpack2x16unorm", "unpack4x8snorm", "unpack4x8unorm", "unpack4xI8", "unpack4xU8", "workgroupBarrier",
        "workgroupUniformLoad",
    };

    /// <summary>True when the name cannot be declared as it is.</summary>
    public static bool IsAvoided(string name) => Avoid.Contains(name);

    /// <summary>
    /// A valid identifier built from <paramref name="hint"/>: characters outside <c>[A-Za-z0-9_]</c> become
    /// underscores (runs collapse, edges trim), a leading digit gets a prefix, and avoided names get a trailing
    /// underscore. Empty hints give <paramref name="fallback"/>.
    /// </summary>
    public static string Sanitize(string? hint, string fallback)
    {
        if (string.IsNullOrEmpty(hint))
        {
            return fallback;
        }

        var sb = new StringBuilder(hint.Length);
        bool pendingSep = false;
        foreach (char ch in hint)
        {
            bool ok = ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
            if (ok)
            {
                if (pendingSep && sb.Length > 0)
                {
                    sb.Append('_');
                }

                pendingSep = false;
                sb.Append(ch);
            }
            else
            {
                pendingSep = true;
            }
        }

        if (sb.Length == 0)
        {
            return fallback;
        }

        if (sb[0] is >= '0' and <= '9')
        {
            sb.Insert(0, 'n');
        }

        string name = sb.ToString();
        return Avoid.Contains(name) ? name + "_" : name;
    }

    /// <summary>Reserves a name in <paramref name="used"/> and returns it, adding a numeric suffix when it is taken.</summary>
    public static string Unique(HashSet<string> used, string name, HashSet<string>? also = null)
    {
        if (!used.Contains(name) && (also is null || !also.Contains(name)))
        {
            used.Add(name);
            return name;
        }

        for (int k = 1; ; k++)
        {
            string candidate = name + "_" + k.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!used.Contains(candidate) && (also is null || !also.Contains(candidate)) && !Avoid.Contains(candidate))
            {
                used.Add(candidate);
                return candidate;
            }
        }
    }
}
