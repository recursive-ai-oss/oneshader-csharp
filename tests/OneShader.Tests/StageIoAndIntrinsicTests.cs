using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using OneShader.Types;
using Xunit;

namespace OneShader.Tests;

/// <summary>
/// Stage input/output forms (clip and cull distances, arrays, matrices), buffer element layouts,
/// texture queries and intrinsics that used to fail. Each shader compiles without diagnostics and passes
/// spirv-val; where locations or layouts matter the decorations are compared with what DXC emits.
/// </summary>
public sealed partial class StageIoAndIntrinsicTests
{
    private static string Dir()
    {
        string d = Path.Combine(Path.GetTempPath(), "osh-u10a-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static CompileResult Compile(string hlsl, string entry, ShaderStage stage, CompileOptions? baseOptions = null)
    {
        var options = new CompileOptions
        {
            EntryPoints = [new EntryPointRequest(entry, stage)],
            Enable16BitTypes = baseOptions?.Enable16BitTypes ?? false,
        };
        return ShaderCompiler.Compile(hlsl, "t.hlsl", options);
    }

    private static string Validate(byte[] spv)
    {
        string? val = Oracles.Find("spirv-val");
        if (val is null)
        {
            return "";
        }

        string dir = Dir();
        try
        {
            string file = Path.Combine(dir, "m.spv");
            File.WriteAllBytes(file, spv);
            var (code, output) = Oracles.Run(val, ["--target-env", "vulkan1.2", file], dir);
            return code == 0 ? "" : output;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>Compiles one entry with the optimizer on and off, expects no diagnostics, validates both, returns the optimized module.</summary>
    private static byte[] Valid(string hlsl, string entry, ShaderStage stage)
    {
        byte[]? first = null;
        foreach (bool optimize in new[] { true, false })
        {
            var options = new CompileOptions { EntryPoints = [new EntryPointRequest(entry, stage)], Optimize = optimize };
            var r = ShaderCompiler.Compile(hlsl, "t.hlsl", options);
            Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
            Assert.Empty(r.Diagnostics);
            var blob = r.Find(entry, CompileTarget.SpirV)!;
            string verdict = Validate(blob.ToArray());
            Assert.True(verdict.Length == 0, "optimize=" + optimize + ": " + verdict);
            first ??= blob.ToArray();
        }

        return first!;
    }

    private static string Disassemble(byte[] spv)
    {
        string? dis = Oracles.Find("spirv-dis");
        Assert.SkipUnless(dis is not null, "spirv-dis is not installed");
        string dir = Dir();
        try
        {
            string file = Path.Combine(dir, "m.spv");
            File.WriteAllBytes(file, spv);
            return Oracles.Run(dis!, [file], dir).Output;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static string DxcDisassemble(string hlsl, string entry, string profile)
    {
        string? dxc = Oracles.Find("dxc");
        Assert.SkipUnless(dxc is not null && Oracles.Find("spirv-dis") is not null, "dxc and spirv-dis are not installed");
        string dir = Dir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.hlsl"), hlsl);
            var (code, output) = Oracles.Run(dxc!, ["-spirv", "-fspv-target-env=vulkan1.2", "-HV", "2021", "-T", profile, "-E", entry, "t.hlsl", "-Fo", "d.spv"], dir);
            Assert.True(code == 0, output);
            return Oracles.Run(Oracles.Find("spirv-dis")!, [Path.Combine(dir, "d.spv")], dir).Output;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>The set of "builtin or location" decorations of the interface variables, by storage class, as sorted text.</summary>
    private static string[] InterfaceDecorations(string dis)
    {
        var result = new List<string>();
        foreach (Match m in DecorationLine().Matches(dis))
        {
            string text = m.Groups[2].Value;
            if (text.StartsWith("Location ", StringComparison.Ordinal))
            {
                // Expand a location on an array or matrix variable to every slot it covers.
                int first = int.Parse(text.AsSpan(9), System.Globalization.CultureInfo.InvariantCulture);
                int count = SlotsOf(dis, m.Groups[1].Value);
                for (int i = 0; i < count; i++)
                {
                    result.Add("Location " + (first + i).ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            else
            {
                result.Add(text);
            }
        }

        result.Sort(StringComparer.Ordinal);
        return [.. result];
    }

    /// <summary>Locations a variable occupies: the array length times the matrix columns of its pointer type name.</summary>
    private static int SlotsOf(string dis, string variable)
    {
        var m = Regex.Match(dis, Regex.Escape(variable) + @" = OpVariable %(\S+)");
        if (!m.Success)
        {
            return 1;
        }

        string type = m.Groups[1].Value;
        int count = 1;
        var arr = Regex.Match(type, @"_arr_.*_uint_(\d+)$");
        if (arr.Success)
        {
            count = int.Parse(arr.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        var mat = Regex.Match(type, @"mat(\d)v");
        return mat.Success ? count * (mat.Groups[1].Value[0] - '0') : count;
    }

    [GeneratedRegex(@"OpDecorate (%\S+) (BuiltIn \w+|Location \d+)")]
    private static partial Regex DecorationLine();

    // ------------------------------------------------------------------ clip and cull distances

    private const string ClipShader = """
        struct VSOut { float4 pos : SV_Position; float2 c1 : SV_ClipDistance1; float c0 : SV_ClipDistance0; float cu1[2] : SV_CullDistance1; float cu0 : SV_CullDistance0; };
        VSOut VS(float4 p : POSITION) { VSOut o; o.pos = p; o.c0 = 1; o.c1 = float2(2, 3); o.cu0 = 4; o.cu1[0] = 5; o.cu1[1] = 6; return o; }
        struct PSIn { float4 pos : SV_Position; float c[2] : SV_ClipDistance; float cu : SV_CullDistance; };
        float4 PS(PSIn i) : SV_Target { return i.c[0] + i.c[1] + i.cu; }
        """;

    [Fact]
    public void ClipAndCullDistancesAreMergedInSemanticOrder()
    {
        byte[] vs = Valid(ClipShader, "VS", ShaderStage.Vertex);
        string dis = Disassemble(vs);

        // One array per built-in, sized by the total, with the capabilities DXC declares.
        Assert.Contains("OpCapability ClipDistance", dis, StringComparison.Ordinal);
        Assert.Contains("OpCapability CullDistance", dis, StringComparison.Ordinal);
        Assert.Equal(["BuiltIn ClipDistance", "BuiltIn CullDistance", "BuiltIn Position", "Location 0"], InterfaceDecorations(dis));

        // c0 lands in slot 0, c1 in slots 1 and 2: the same slots DXC stores to.
        string dxc = DxcDisassemble(ClipShader, "VS", "vs_6_0");
        Assert.Equal(StoredSlots(dxc, "gl_ClipDistance"), StoredSlots(dis, "ClipDistance"));
        Assert.Equal(StoredSlots(dxc, "gl_CullDistance"), StoredSlots(dis, "CullDistance"));
    }

    [Fact]
    public void ClipAndCullDistancesAreReadableInPixelShaders()
    {
        byte[] ps = Valid(ClipShader, "PS", ShaderStage.Pixel);
        string dis = Disassemble(ps);
        Assert.Contains("OpCapability ClipDistance", dis, StringComparison.Ordinal);
        Assert.Contains("Input", dis, StringComparison.Ordinal);
    }

    [Fact]
    public void ClipDistanceArrayOutput()
    {
        const string Src = """
            struct VSOut { float4 pos : SV_Position; float Clip[2] : SV_ClipDistance; };
            VSOut VS(float4 p : POSITION) { VSOut o; o.pos = p; o.Clip[0] = p.x; o.Clip[1] = p.y; return o; }
            """;
        string dis = Disassemble(Valid(Src, "VS", ShaderStage.Vertex));
        Assert.Equal(["BuiltIn ClipDistance", "BuiltIn Position", "Location 0"], InterfaceDecorations(dis));
        Assert.Equal(StoredSlots(DxcDisassemble(Src, "VS", "vs_6_0"), "gl_ClipDistance"), StoredSlots(dis, "ClipDistance"));
    }

    [Fact]
    public void ClipDistanceMustBeFloat()
    {
        const string Src = "struct O { float4 p : SV_Position; int c : SV_ClipDistance; };\nO VS(float4 p : POSITION) { O o; o.p = p; o.c = 1; return o; }\n";
        var r = Compile(Src, "VS", ShaderStage.Vertex);
        var d = Assert.Single(r.Diagnostics);
        Assert.Equal("OSH0321", d.Code);
        Assert.Equal(1, d.Span.Line);
    }

    // ------------------------------------------------------------------ arrays and matrices as stage variables

    private static void SameLocationsAsDxc(string src, string entry, ShaderStage stage, string profile)
    {
        string dis = Disassemble(Valid(src, entry, stage));
        // DXC declares an array or matrix as one variable whose Location covers several slots; we declare one variable per
        // element. Either way the same locations are occupied, which is what matches stages together.
        Assert.Equal(string.Join(", ", InterfaceDecorations(DxcDisassemble(src, entry, profile))), string.Join(", ", InterfaceDecorations(dis)));
    }

    [Fact]
    public void ArrayVertexOutputGetsOneLocationPerElement()
    {
        const string Src = """
            struct VO { float4 Position : SV_Position; float4 ShadowUv[4] : TEXCOORD0; float2 Uv : TEXCOORD4; };
            VO VS(float3 position : POSITION) { VO o; o.Position = float4(position, 1.0); o.Uv = position.xy; [unroll] for (int i = 0; i < 4; ++i) { o.ShadowUv[i] = float4(position * i, 1.0); } return o; }
            struct PI { float4 ShadowUv[4] : TEXCOORD0; float2 Uv : TEXCOORD4; };
            float4 PS(PI i) : SV_Target { return i.ShadowUv[0] + i.ShadowUv[3] + float4(i.Uv, 0, 0); }
            """;
        SameLocationsAsDxc(Src, "VS", ShaderStage.Vertex, "vs_6_0");
        SameLocationsAsDxc(Src, "PS", ShaderStage.Pixel, "ps_6_0");
    }

    [Fact]
    public void MatrixPixelInputGetsOneLocationPerRow()
    {
        const string Src = """
            struct PI { float3x3 Tbn : TEXCOORD1; float2 Uv : TEXCOORD4; };
            float4 PS(PI i) : SV_Target0 { return float4(mul(float3(i.Uv, 1.0), i.Tbn), 1.0); }
            struct VO { float4 Position : SV_Position; float3x3 Tbn : TEXCOORD1; float2 Uv : TEXCOORD4; };
            VO VS(float3 p : POSITION, float3x3 m : TEXCOORD0) { VO o; o.Position = float4(p, 1); o.Tbn = m; o.Uv = p.xy; return o; }
            """;
        SameLocationsAsDxc(Src, "PS", ShaderStage.Pixel, "ps_6_0");
        SameLocationsAsDxc(Src, "VS", ShaderStage.Vertex, "vs_6_0");
    }

    [Fact]
    public void MatrixAndArrayInputsReflectOneVariablePerElement()
    {
        const string Src = """
            float4 VS(float4x4 world : WORLD, float2 uv[2] : UV, float3 p : POSITION) : SV_Position { return mul(world, float4(p, 1)) + float4(uv[1], 0, 0); }
            """;
        var r = Compile(Src, "VS", ShaderStage.Vertex);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var inputs = r.Reflection!.EntryPoints[0].Inputs;
        Assert.Equal(["world[0]", "world[1]", "world[2]", "world[3]", "uv[0]", "uv[1]", "p"], inputs.Select(v => v.Name).ToArray());
        Assert.Equal([0u, 1u, 2u, 3u, 4u, 5u, 6u], inputs.Select(v => v.Location!.Value).ToArray());
        Assert.Equal([0u, 16u, 32u, 48u, 64u, 72u, 80u], inputs.Select(v => v.Offset).ToArray());
        Assert.Equal(92u, r.Reflection.EntryPoints[0].VertexStride);
        Assert.Equal(("WORLD", 3u), (inputs[3].Semantic, inputs[3].SemanticIndex));
        Assert.Equal(("UV", 1u), (inputs[5].Semantic, inputs[5].SemanticIndex));
    }

    [Fact]
    public void ExplicitLocationOnAnArrayCoversConsecutiveLocations()
    {
        const string Src = """
            struct VO { float4 Position : SV_Position; [[vk::location(3)]] float4 A[2] : TEXCOORD0; [[vk::location(7)]] float B : TEXCOORD2; };
            VO VS(float3 p : POSITION) { VO o; o.Position = float4(p, 1); o.A[0] = 1; o.A[1] = 2; o.B = 3; return o; }
            """;
        SameLocationsAsDxc(Src, "VS", ShaderStage.Vertex, "vs_6_0");
    }

    [Fact]
    public void OneRowMatrixStageVariableSaysToUseAVector()
    {
        const string Src = "float4 PS(float4 p : SV_Position, float1x4 m : TEXCOORD0) : SV_Target { return m[0]; }\n";
        var r = Compile(Src, "PS", ShaderStage.Pixel);
        var d = Assert.Single(r.Diagnostics);
        Assert.Equal("OSH0321", d.Code);
        Assert.Contains("use a vector", d.Message, StringComparison.Ordinal);
    }

    /// <summary>Slot numbers stored through access chains into the named variable, in program order, taken from the constants bound by OpConstant.</summary>
    private static int[] StoredSlots(string dis, string variable)
    {
        var constants = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in ConstantLine().Matches(dis))
        {
            constants[m.Groups[1].Value] = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        var vars = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in VariableLine().Matches(dis))
        {
            if (m.Groups[1].Value.Contains(variable, StringComparison.OrdinalIgnoreCase))
            {
                vars.Add(m.Groups[1].Value);
            }
        }

        var slots = new List<int>();
        foreach (Match m in AccessLine().Matches(dis))
        {
            if (vars.Contains(m.Groups[1].Value) && constants.TryGetValue(m.Groups[2].Value, out int slot))
            {
                slots.Add(slot);
            }
        }

        slots.Sort();
        return [.. slots];
    }

    [GeneratedRegex(@"(%\S+) = OpConstant %\S+ (\d+)")]
    private static partial Regex ConstantLine();

    [GeneratedRegex(@"(%\S+) = OpVariable ")]
    private static partial Regex VariableLine();

    [GeneratedRegex(@"= OpAccessChain %\S+ (%\S+) (%\S+)")]
    private static partial Regex AccessLine();
}
