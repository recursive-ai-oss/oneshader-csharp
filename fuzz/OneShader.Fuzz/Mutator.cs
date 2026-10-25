using System;
using System.Collections.Generic;
using System.Text;

/// <summary>Token-level and byte-level mutations of HLSL text, plus splicing between inputs.</summary>
internal sealed class Mutator(Random rng)
{
    private static readonly string[] Inserts =
    [
        "(", ")", "{", "}", "[", "]", "[[", "]]", ";", ",", "<", ">", ">>", "::", ":", "=", "+", "-", "*", "/", "?", "!", "0", "1.5", "0xFFFFFFFFu", "-1", "2147483648", "1e38", "0.0", "1.#INF",
        "float4", "float3x3", "float", "uint", "int2", "bool", "half", "double", "Texture2D<float4>", "SamplerState", "RWStructuredBuffer<float4>", "StructuredBuffer<float>", "ByteAddressBuffer", "cbuffer", "struct", "template<typename T>", "typedef", "return", "if", "else", "for", "while", "do", "switch", "case",
        "default", "break", "continue", "discard", "register(t0)", "register(b0, space1)", "packoffset(c0)", "SV_Position", "SV_Target", "SV_DispatchThreadID", "[shader(\"pixel\")]", "[shader(\"vertex\")]", "[shader(\"compute\")]",
        "[numthreads(8,8,1)]", "[numthreads(0,1,1)]", "[loop]", "[unroll]", "[branch]", "[[vk::binding(1,2)]]", "static", "const", "groupshared", "row_major", "column_major", "precise", "inline", "out", "inout", "x", "Frame", "mul", "sin", "pow", "saturate",
        "InterlockedAdd", "GroupMemoryBarrierWithGroupSync", "Sample", "Load", "Store", "operator+", "this", "sizeof", ".xyzw", ".x", "[0]", "[1000000]", "#define X 1\n", "#include \"f1.h\"\n", "#if 0\n", "#endif\n", "\n", "\u0001",
    ];

    private string _text = "";

    public string Mutate(string text, string? other)
    {
        _text = text;
        int n = rng.Next(1, 6);
        var sb = new StringBuilder(text);
        for (int k = n; k > 0 && sb.Length > 2; k--)
        {
            int at = rng.Next(sb.Length);
            switch (rng.Next(10))
            {
                case 0:
                    sb.Remove(at, Math.Min(rng.Next(1, 40), sb.Length - at));
                    break;
                case 1:
                {
                    int len = Math.Min(rng.Next(1, 60), sb.Length - at);
                    sb.Insert(rng.Next(sb.Length), sb.ToString(at, len));
                    break;
                }

                case 2:
                case 3:
                    sb.Insert(at, " " + Inserts[rng.Next(Inserts.Length)] + " ");
                    break;
                case 4:
                {
                    int len = Math.Min(rng.Next(1, 30), sb.Length - at);
                    int to = rng.Next(sb.Length - len);
                    string chunk = sb.ToString(at, len);
                    sb.Remove(at, len);
                    sb.Insert(Math.Min(to, sb.Length), chunk);
                    break;
                }

                case 5:
                    sb.Length = Math.Max(1, at);
                    break;
                case 6:
                    sb[at] = (char)rng.Next(32, 127);
                    break;
                case 7:
                    ReplaceToken(sb, at);
                    break;
                case 8:
                    if (other is not null && other.Length > 4)
                    {
                        int o = rng.Next(other.Length - 2);
                        sb.Insert(at, other.AsSpan(o, Math.Min(rng.Next(1, 200), other.Length - o)));
                    }

                    break;
                default:
                    BumpNumber(sb, at);
                    break;
            }
        }

        return sb.ToString();
    }

    public string LastInput => _text;

    private void ReplaceToken(StringBuilder sb, int at)
    {
        int s = at;
        while (s > 0 && char.IsLetterOrDigit(sb[s - 1]))
        {
            s--;
        }

        int e = at;
        while (e < sb.Length && char.IsLetterOrDigit(sb[e]))
        {
            e++;
        }

        if (e > s)
        {
            sb.Remove(s, e - s);
            sb.Insert(s, Inserts[rng.Next(Inserts.Length)]);
        }
    }

    private void BumpNumber(StringBuilder sb, int at)
    {
        while (at < sb.Length && !char.IsDigit(sb[at]))
        {
            at++;
        }

        if (at < sb.Length)
        {
            string[] vals = ["0", "1", "2", "3", "4", "7", "8", "16", "255", "256", "65535", "2147483647", "4294967295", "-1", "99999999999"];
            int e = at;
            while (e < sb.Length && char.IsDigit(sb[e]))
            {
                e++;
            }

            sb.Remove(at, e - at);
            sb.Insert(at, vals[rng.Next(vals.Length)]);
        }
    }
}
