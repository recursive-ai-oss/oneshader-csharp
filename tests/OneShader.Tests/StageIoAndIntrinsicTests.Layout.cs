using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace OneShader.Tests;

public sealed partial class StageIoAndIntrinsicTests
{
    // ------------------------------------------------------------------ structured buffer element strides

    public static TheoryData<string> ElementTypes() => new()
    {
        "float", "float2", "float3", "float4", "int3", "uint3", "uint2", "float3x3", "float2x3", "float3x2", "float4x4",
        "row_major float3x3", "double3", "double2", "S3", "S3A", "Sm",
    };

    private const string StructPrelude = "struct S3 { float3 v; }; struct S3A { float3 v[2]; float k; }; struct Sm { float3x3 m; float3 c; };\n";

    [Theory]
    [MemberData(nameof(ElementTypes))]
    public void StructuredBufferElementStrideMatchesDxc(string type)
    {
        string src = StructPrelude + "[[vk::binding(0, 0)]] StructuredBuffer<" + type + "> In;\n[[vk::binding(1, 0)]] RWStructuredBuffer<" + type + "> Out;\n"
            + "[numthreads(8, 1, 1)] void CS(uint3 id : SV_DispatchThreadID) { Out[id.x] = In[id.x]; }\n";
        string dis = Disassemble(Valid(src, "CS", ShaderStage.Compute));
        Assert.Equal(Strides(DxcDisassemble(src, "CS", "cs_6_0")), Strides(dis));
    }

    [Fact]
    public void StructuredBufferOfFloat3ReflectsTheSixteenByteStride()
    {
        const string Src = "[[vk::binding(0, 0)]] StructuredBuffer<float3> In;\n[numthreads(1, 1, 1)] void CS(uint3 id : SV_DispatchThreadID) { float3 x = In[id.x]; }\n";
        var r = Compile(Src, "CS", ShaderStage.Compute);
        Assert.True(r.Success);
        Assert.Equal(16u, r.Reflection!.Resources[0].Layout!.Size);
    }

    // ------------------------------------------------------------------ bool in buffers

    public static TheoryData<string> BoolShaders() => new()
    {
        "cbuffer C { float4 tint; bool enabled; }; float4 PS(float4 c : TEXCOORD0) : SV_Target0 { return enabled ? c * tint : c; }",
        "cbuffer C { float a; bool3 flags; bool one; float b[2]; }; float4 PS(float4 c : TEXCOORD0) : SV_Target0 { return (flags.y && one) ? c * a : c * b[1]; }",
        "cbuffer C { bool arr[3]; float k; }; float4 PS(float4 c : TEXCOORD0) : SV_Target0 { return arr[2] ? c : c * k; }",
        "struct S { bool on; float3 v; bool2 pair; }; ConstantBuffer<S> cb; float4 PS(float4 c : TEXCOORD0) : SV_Target0 { return (cb.on && cb.pair.x) ? c + float4(cb.v, 0) : c; }",
        "struct P { bool alive; float x; }; StructuredBuffer<P> In; float4 PS(float4 c : TEXCOORD0) : SV_Target0 { return In[0].alive ? c * In[0].x : c; }",
        "struct P { bool alive; float x; bool2 q; }; StructuredBuffer<P> In; float4 PS(float4 c : TEXCOORD0) : SV_Target0 { P p = In[1]; return (p.alive && p.q.y) ? c * p.x : c; }",
    };

    [Theory]
    [MemberData(nameof(BoolShaders))]
    public void BoolInBufferIsAThirtyTwoBitIntegerLikeDxc(string body)
    {
        // Bindings are explicit so DXC and this compiler number resources the same way.
        string src = body.Replace("cbuffer C", "[[vk::binding(0, 0)]] cbuffer C", StringComparison.Ordinal)
            .Replace("ConstantBuffer<S> cb", "[[vk::binding(0, 0)]] ConstantBuffer<S> cb", StringComparison.Ordinal)
            .Replace("StructuredBuffer<P> In", "[[vk::binding(0, 0)]] StructuredBuffer<P> In", StringComparison.Ordinal);
        string dis = Disassemble(Valid(src, "PS", ShaderStage.Pixel));
        string dxc = DxcDisassemble(src, "PS", "ps_6_0");
        Assert.Equal(Offsets(dxc), Offsets(dis));
        Assert.Equal(Strides(dxc), Strides(dis));
        Assert.DoesNotContain("_ptr_Uniform_bool", dis, StringComparison.Ordinal);
    }

    private static string Offsets(string dis)
    {
        var all = new List<string>();
        foreach (Match m in OffsetLine().Matches(dis))
        {
            all.Add(m.Groups[1].Value + "@" + m.Groups[2].Value);
        }

        return string.Join(",", all);
    }

    [GeneratedRegex(@"OpMemberDecorate %\S+ (\d+) Offset (\d+)")]
    private static partial Regex OffsetLine();

    private static string Strides(string dis)
    {
        var all = new List<string>();
        foreach (Match m in StrideLine().Matches(dis))
        {
            all.Add(m.Groups[1].Value);
        }

        all.Sort(StringComparer.Ordinal);
        return string.Join(",", all.Distinct());
    }

    [GeneratedRegex(@"ArrayStride (\d+)")]
    private static partial Regex StrideLine();
}
