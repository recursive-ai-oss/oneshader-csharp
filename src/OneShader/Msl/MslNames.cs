using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OneShader.Msl;

/// <summary>
/// Turns source names into identifiers that are legal in MSL and cannot collide with the language or the standard library,
/// and hands out each name once so the output never depends on declaration quirks.
/// </summary>
internal sealed class MslNames
{
    private static readonly HashSet<string> Reserved = BuildReserved();

    private static readonly string[] ReservedPrefixes =
    [
        "atomic_", "simd_", "quad_", "packed_", "as_type", "mem_", "memory_", "threadgroup_", "thread_", "FLT_", "INT_",
        "UINT_", "M_", "HUGE_", "texture1", "texture2", "texture3", "texture_", "texturecube", "depth2", "depthcube",
        "imageblock", "ray_", "intersect", "acceleration", "visible_function", "indirect_", "tile_", "primitive_", "object_",
        "mesh_", "os_",
    ];

    private static bool IsTypeLike(string name)
    {
        // float, float3, half4, float4x4, uint2 ... are all types in the metal namespace.
        int i = 0;
        while (i < name.Length && name[i] is >= 'a' and <= 'z')
        {
            i++;
        }

        string stem = name[..i];
        if (stem is not ("bool" or "char" or "uchar" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" or "half" or "float" or "bfloat"))
        {
            return false;
        }

        string rest = name[i..];
        return rest.Length == 0 || (rest.Length == 1 && rest[0] is '2' or '3' or '4') || (rest.Length == 3 && rest[0] is '2' or '3' or '4' && rest[1] == 'x' && rest[2] is '2' or '3' or '4');
    }

    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    private static HashSet<string> BuildReserved()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        const string words =
            "alignas alignof and and_eq asm auto bitand bitor bool break case catch char class compl const constexpr const_cast " +
            "continue decltype default delete do double dynamic_cast else enum explicit export extern false float for friend goto " +
            "if inline int long mutable namespace new noexcept not not_eq nullptr operator or or_eq private protected public " +
            "register reinterpret_cast return short signed sizeof static static_assert static_cast struct switch template this " +
            "thread_local throw true try typedef typeid typename union unsigned using virtual void volatile wchar_t while xor xor_eq " +
            "kernel vertex fragment device constant thread threadgroup main metal sampler half uint uchar ushort ulong " +
            "abs absdiff acos acosh addsat all any asin asinh atan atan2 atanh ceil clamp clz copysign cos cosh cospi cross ctz " +
            "determinant dfdx dfdy discard_fragment distance distance_squared dot exp exp2 exp10 extract_bits fabs fast fdim floor fma " +
            "fmax fmax3 fmin fmin3 fmod fract frexp fwidth hadd insert_bits isfinite isinf isnan isnormal ldexp length length_squared " +
            "log log10 log2 mad madhi madsat max max3 median min min3 mix modf mulhi normalize popcount pow powr precise radians read " +
            "reflect refract reverse_bits rhadd rint rotate round rsqrt sample saturate select sign signbit sin sincos sinh sinpi smoothstep " +
            "sqrt step subsat tan tanh tanpi transpose trunc write gather sample_compare gather_compare printf assert qualifier " +
            "NAN INFINITY MAXFLOAT MAXHALF EOF NULL level bias gradient2d gradient3d gradientcube min_lod_clamp lod_options component " +
            "bit_cast";
        foreach (var w in words.Split(' '))
        {
            set.Add(w);
        }

        return set;
    }

    private static bool IsReserved(string name)
    {
        if (Reserved.Contains(name) || IsTypeLike(name))
        {
            return true;
        }

        foreach (var p in ReservedPrefixes)
        {
            if (name.StartsWith(p, StringComparison.Ordinal))
            {
                // Names such as "texturing" are fine for C++ but not worth the risk: they would shadow library types.
                return true;
            }
        }

        return false;
    }

    /// <summary>Maps text to a legal identifier: letters, digits and single underscores, not starting with a digit or underscore, never a reserved word.</summary>
    public static string Sanitize(string hint, string fallback)
    {
        var sb = new StringBuilder(hint.Length + 2);
        foreach (char c in hint)
        {
            bool ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9');
            if (ok)
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '_')
            {
                sb.Append('_');
            }
            else if (sb.Length == 0)
            {
                // A leading separator would give a leading underscore, which C++ reserves; drop it.
            }
        }

        while (sb.Length > 0 && sb[^1] == '_')
        {
            sb.Length--;
        }

        if (sb.Length == 0)
        {
            return fallback;
        }

        if (sb[0] is >= '0' and <= '9')
        {
            sb.Insert(0, 'n');
        }

        string s = sb.ToString();
        return IsReserved(s) ? s + "_" : s;
    }

    /// <summary>The MSL function name for an entry point: the source name unless it is reserved (Metal does not allow <c>main</c>).</summary>
    public static string EntryName(string hlslName)
    {
        string s = Sanitize(hlslName, "entry");
        if (IsReserved(hlslName) || s != hlslName)
        {
            // Keep a reserved name recognisable: "main" becomes "main0".
            string baseName = s.EndsWith('_') ? s[..^1] : s;
            return baseName.Length == 0 ? "entry" : baseName + (IsReserved(baseName) ? "0" : string.Empty);
        }

        return s;
    }

    /// <summary>Returns a name derived from <paramref name="hint"/> that has not been handed out before.</summary>
    public string Claim(string hint, string fallback = "v")
    {
        string s = Sanitize(hint, fallback);
        return Unique(s);
    }

    /// <summary>Claims exactly <paramref name="exact"/> if free, otherwise a numbered variant.</summary>
    public string ClaimExact(string exact) => Unique(exact);

    private string Unique(string s)
    {
        if (_used.Add(s))
        {
            return s;
        }

        for (int n = 1; ; n++)
        {
            string candidate = s + "_" + n.ToString(CultureInfo.InvariantCulture);
            if (_used.Add(candidate))
            {
                return candidate;
            }
        }
    }
}
