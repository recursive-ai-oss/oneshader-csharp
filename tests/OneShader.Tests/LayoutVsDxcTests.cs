using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using OneShader.Types;
using Xunit;

namespace OneShader.Tests;

/// <summary>
/// Checks the buffer layout rules against what DXC's SPIR-V path decorates for the same
/// declarations. Skipped when dxc or spirv-dis is not installed.
/// </summary>
public sealed partial class LayoutVsDxcTests
{
    private sealed record LayoutCase(string Name, string Prelude, string Members, bool ConstantBuffer, bool Sixteen = false, bool PackOffset = false);

    private static readonly LayoutCase[] Cases =
    [
        new LayoutCase("scalar-then-vec3", "", "float a; float3 b;", true),
        new LayoutCase("vec3-then-scalar", "", "float3 a; float b;", true),
        new LayoutCase("vec2-then-vec3", "", "float2 a; float3 b;", true),
        new LayoutCase("float-array", "", "float a[4];", true),
        new LayoutCase("float-array-then-members", "", "float a[2]; float2 b; float c;", true),
        new LayoutCase("nested-struct", "struct S { float3 v; float f; };", "float q; S s; float r; S t[2];", true),
        new LayoutCase("nested-struct-odd-size", "struct S0 { float3 x; float y; float z; };", "float q; S0 s; float f;", true),
        new LayoutCase("mat3x3-both-packings", "", "float3x3 m; row_major float3x3 r; float f;", true),
        new LayoutCase("mat4x3-both-packings", "", "float f; float4x3 m; row_major float4x3 r;", true),
        new LayoutCase("mat2x3-3x2", "", "float f; float2x3 a; row_major float2x3 b; float3x2 c; row_major float3x2 d;", true),
        new LayoutCase("mat4x4", "", "float f; float4x4 m;", true),
        new LayoutCase("mat2x2-array", "", "float f; float2x2 a[2]; float g;", true),
        new LayoutCase("mat3x3-array", "", "float f; float3x3 a[2]; float g;", true),
        new LayoutCase("vec-arrays", "", "float f; float2 a[2]; float3 b[2]; float4 c[3];", true),
        new LayoutCase("doubles", "", "float f; double2 d; double3 e; double g;", true),
        new LayoutCase("int-bool-vectors", "", "float f; int3 i; uint2 u; bool3 b; bool c;", true),
        new LayoutCase("packoffset", "", "float pz : packoffset(c0.w); float4 pk : packoffset(c1); float3 py : packoffset(c3.y);", true, PackOffset: true),
        new LayoutCase("half-types", "", "half h; half3 h3; half k; half2 h2; half3 ha[3]; half4x4 m; half2x2 m2; row_major half3x3 r3;", true, Sixteen: true),
        new LayoutCase("half-arrays", "", "half h; half a[3]; half2 b[2]; half3x3 m[2];", true, Sixteen: true),
        new LayoutCase("sb-scalar-vec", "", "float a; float2 b; float c;", false),
        new LayoutCase("sb-vec3-float", "", "float3 v; float f; float2 u; float3 w; float arr[3];", false),
        new LayoutCase("sb-struct-with-float3", "struct S { float3 v; float f; };", "float q; S s; S t[2]; float2 zz;", false),
        new LayoutCase("sb-vec-arrays", "", "float f; float2 a[2]; float3 b[2]; float4 c[3];", false),
        new LayoutCase("sb-matrices", "", "float f; float2x2 a; float2x3 b; row_major float2x3 c; float3x2 d; row_major float3x2 e; float4x4 g;", false),
        new LayoutCase("sb-matrix-arrays", "", "float f; float2x2 a[2]; float3x3 b[2];", false),
        new LayoutCase("sb-doubles", "", "float f; double2 d; double3 e; double g;", false),
        new LayoutCase("sb-half", "", "half h; half3 h3; half k; half2 h2; half3 ha[3]; half4x4 m; half2x2 m2; row_major half3x3 r3; half a[3]; half2 b[2];", false, Sixteen: true),
        new LayoutCase("sb-mixed", "struct S { float3 v; float f; };", "float3 v; float f; float2 u; float3 w; float arr[3]; S s; float3x3 m; row_major float2x3 rm;", false),
    ];

    public static TheoryData<string> CaseNames()
    {
        var d = new TheoryData<string>();
        foreach (var c in Cases)
        {
            d.Add(c.Name);
        }

        return d;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void MatchesDxc(string name)
    {
        string? dxc = Oracles.Find("dxc");
        string? dis = Oracles.Find("spirv-dis");
        Assert.SkipUnless(dxc is not null && dis is not null, "dxc and spirv-dis are not installed");

        var c = Array.Find(Cases, x => x.Name == name)!;
        var structs = new Dictionary<string, StructType>();
        foreach (Match m in StructDef().Matches(c.Prelude))
        {
            structs[m.Groups[1].Value] = new StructType(m.Groups[1].Value, ParseMembers(m.Groups[2].Value, structs));
        }

        // A trailing float is used by the shader so DXC keeps the whole buffer.
        string members = c.Members + (c.PackOffset ? " float z : packoffset(c4);" : " float z;");
        var root = new StructType(c.ConstantBuffer ? "C" : "SB", ParseMembers(members, structs));

        string hlsl = c.Prelude + "\n" + (c.ConstantBuffer
            ? "cbuffer C : register(b0) { " + members + " };\n"
              + "RWByteAddressBuffer o : register(u1);\n[numthreads(1,1,1)] void main() { o.Store(0, asuint(z)); }\n"
            : "struct SB { " + members + " };\nStructuredBuffer<SB> sb : register(t0);\n"
              + "RWByteAddressBuffer o : register(u1);\n[numthreads(1,1,1)] void main() { o.Store(0, asuint(sb[0].z)); }\n");

        string dir = Path.Combine(Path.GetTempPath(), "oneshader-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.hlsl"), hlsl);
            var args = new List<string> { "-spirv", "-T", "cs_6_2", "-E", "main", "-fspv-target-env=vulkan1.2", "t.hlsl", "-Fo", "t.spv" };
            if (c.Sixteen)
            {
                args.Add("-enable-16bit-types");
            }

            var (code, output) = Oracles.Run(dxc!, args, dir);
            Assert.True(code == 0, "dxc failed: " + output);
            var (dcode, text) = Oracles.Run(dis!, ["t.spv"], dir);
            Assert.True(dcode == 0, text);

            var options = new LayoutOptions { Enable16BitTypes = c.Sixteen };
            Assert.True(
                BufferLayout.TryCompute(root, c.ConstantBuffer ? BufferLayoutRules.ConstantBuffer : BufferLayoutRules.StorageBuffer, options, out var layout, out var error),
                error);

            var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var arrayStrides = new SortedSet<int>();
            Collect(layout!, c.ConstantBuffer ? "type_C" : "SB", expected, arrayStrides, new HashSet<string>());

            var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var actualStrides = new SortedSet<int>();
            long? runtimeStride = null;
            foreach (var line in text.Split('\n'))
            {
                var mm = MemberDecoration().Match(line);
                if (mm.Success)
                {
                    string key = mm.Groups[1].Value + "/" + mm.Groups[2].Value + "/" + mm.Groups[3].Value;
                    if (mm.Groups[3].Value is "Offset" or "MatrixStride")
                    {
                        actual[key] = mm.Groups[4].Value;
                    }
                    else if (mm.Groups[3].Value is "RowMajor" or "ColMajor")
                    {
                        actual[mm.Groups[1].Value + "/" + mm.Groups[2].Value + "/Majorness"] = mm.Groups[3].Value;
                    }

                    continue;
                }

                var am = ArrayDecoration().Match(line);
                if (am.Success)
                {
                    long stride = long.Parse(am.Groups[2].Value, CultureInfo.InvariantCulture);
                    if (am.Groups[1].Value.StartsWith("_runtimearr_SB", StringComparison.Ordinal))
                    {
                        runtimeStride = stride;
                    }
                    else if (!am.Groups[1].Value.StartsWith("_runtimearr_uint", StringComparison.Ordinal))
                    {
                        actualStrides.Add((int)stride);
                    }
                }
            }

            // The byte-address buffer in the shader contributes its own struct; keep only ours.
            foreach (var k in new List<string>(actual.Keys))
            {
                if (k.StartsWith("type_RWByteAddressBuffer/", StringComparison.Ordinal) || k.StartsWith("type_StructuredBuffer", StringComparison.Ordinal))
                {
                    actual.Remove(k);
                }
            }

            Assert.Equal(expected, actual);
            Assert.Equal(arrayStrides, actualStrides);
            if (!c.ConstantBuffer)
            {
                Assert.Equal((long)layout!.Size, runtimeStride);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ReportsUnlayoutableTypes()
    {
        var s = new StructType("S", [new StructField("t", new ResourceType(ResourceTypeKind.Texture2D, HlslType.Vector(ScalarType.Float, 4)))]);
        Assert.False(BufferLayout.TryCompute(s, BufferLayoutRules.ConstantBuffer, null, out _, out var error));
        Assert.Contains("Texture2D", error, StringComparison.Ordinal);
    }

    [Fact]
    public void SizesWithoutDxc()
    {
        // float a; float3 b; -> b packs into the first register.
        var s = new StructType("C", [new StructField("a", ScalarType.Float), new StructField("b", HlslType.Vector(ScalarType.Float, 3))]);
        Assert.True(BufferLayout.TryCompute(s, BufferLayoutRules.ConstantBuffer, null, out var l, out _));
        Assert.Equal(4, l!.Members[1].Offset);
        Assert.Equal(16, l.Size);
    }

    private static void Collect(TypeLayout l, string structName, SortedDictionary<string, string> offsets, SortedSet<int> arrayStrides, HashSet<string> done)
    {
        if (l.Type is StructType st)
        {
            bool first = done.Add(structName);
            for (int i = 0; i < l.Members.Count; i++)
            {
                var m = l.Members[i];
                if (first)
                {
                    offsets[structName + "/" + i + "/Offset"] = m.Offset.ToString(CultureInfo.InvariantCulture);
                    var mat = FindMatrix(m.Layout);
                    if (mat is not null)
                    {
                        offsets[structName + "/" + i + "/MatrixStride"] = mat.MatrixStride.ToString(CultureInfo.InvariantCulture);

                        // SPIR-V decorations name the opposite storage order of HLSL's, because DXC maps HLSL matrices transposed.
                        offsets[structName + "/" + i + "/Majorness"] = mat.MatrixPacking == MatrixPacking.ColumnMajor ? "RowMajor" : "ColMajor";
                    }
                }

                CollectInner(m.Layout, offsets, arrayStrides, done);
            }

            _ = st;
        }
    }

    private static void CollectInner(TypeLayout l, SortedDictionary<string, string> offsets, SortedSet<int> arrayStrides, HashSet<string> done)
    {
        if (l.Type is ArrayType)
        {
            arrayStrides.Add(l.ArrayStride);
            CollectInner(l.Element!, offsets, arrayStrides, done);
        }
        else if (l.Type is StructType st)
        {
            Collect(l, st.Name, offsets, arrayStrides, done);
        }
    }

    private static TypeLayout? FindMatrix(TypeLayout l)
    {
        while (l.Type is ArrayType)
        {
            l = l.Element!;
        }

        return l.Type is MatrixType ? l : null;
    }

    private static List<StructField> ParseMembers(string text, Dictionary<string, StructType> structs)
    {
        var fields = new List<StructField>();
        foreach (var raw in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = MemberDecl().Match(raw);
            Assert.True(m.Success, "bad member: " + raw);
            MatrixPacking? packing = m.Groups[1].Value switch
            {
                "row_major" => MatrixPacking.RowMajor,
                "column_major" => MatrixPacking.ColumnMajor,
                _ => null,
            };
            HlslType t = ParseType(m.Groups[2].Value, structs);
            var dims = new List<int>();
            foreach (Match d in Dim().Matches(m.Groups[4].Value))
            {
                dims.Add(int.Parse(d.Groups[1].Value, CultureInfo.InvariantCulture));
            }

            for (int i = dims.Count - 1; i >= 0; i--)
            {
                t = HlslType.Array(t, dims[i]);
            }

            int? offset = null;
            if (m.Groups[5].Success)
            {
                offset = int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture) * 16
                    + (m.Groups[6].Success ? "xyzw".IndexOf(m.Groups[6].Value[0], StringComparison.Ordinal) * 4 : 0);
            }

            fields.Add(new StructField(m.Groups[3].Value, t) { MatrixPacking = packing, ExplicitOffset = offset });
        }

        return fields;
    }

    private static HlslType ParseType(string name, Dictionary<string, StructType> structs)
    {
        if (structs.TryGetValue(name, out var s))
        {
            return s;
        }

        var m = BuiltinType().Match(name);
        Assert.True(m.Success, "bad type: " + name);
        ScalarType scalar = m.Groups[1].Value switch
        {
            "float" => ScalarType.Float,
            "half" => ScalarType.Half,
            "double" => ScalarType.Double,
            "int" => ScalarType.Int,
            "uint" => ScalarType.UInt,
            "bool" => ScalarType.Bool,
            _ => throw new InvalidOperationException(name),
        };
        if (!m.Groups[2].Success)
        {
            return scalar;
        }

        int a = m.Groups[2].Value[0] - '0';
        return m.Groups[3].Success ? HlslType.Matrix(scalar, a, m.Groups[3].Value[0] - '0') : HlslType.Vector(scalar, a);
    }

    [GeneratedRegex(@"struct\s+(\w+)\s*\{([^}]*)\}\s*;")]
    private static partial Regex StructDef();

    [GeneratedRegex(@"^(row_major|column_major)?\s*(\w+)\s+(\w+)((?:\[\d+\])*)\s*(?::\s*packoffset\(c(\d+)(?:\.([xyzw]))?\))?$")]
    private static partial Regex MemberDecl();

    [GeneratedRegex(@"\[(\d+)\]")]
    private static partial Regex Dim();

    [GeneratedRegex(@"^(float|half|double|int|uint|bool)(?:(\d)(?:x(\d))?)?$")]
    private static partial Regex BuiltinType();

    [GeneratedRegex(@"OpMemberDecorate %(\w+) (\d+) (Offset|MatrixStride|RowMajor|ColMajor)(?: (\d+))?")]
    private static partial Regex MemberDecoration();

    [GeneratedRegex(@"OpDecorate %(\w+) ArrayStride (\d+)")]
    private static partial Regex ArrayDecoration();
}
