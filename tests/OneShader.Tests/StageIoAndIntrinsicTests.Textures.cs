using System;
using System.Linq;
using Xunit;

namespace OneShader.Tests;

public sealed partial class StageIoAndIntrinsicTests
{
    // ------------------------------------------------------------------ texture queries and gathers

    private const string TexturePrelude = """
        [[vk::binding(0, 0)]] Texture2D<float4> Source;
        [[vk::binding(1, 0)]] SamplerState Linear;
        [[vk::binding(2, 0)]] Texture2D<float> Shadow;
        [[vk::binding(3, 0)]] SamplerComparisonState Cmp;
        [[vk::binding(4, 0)]] Texture2DArray<float> ShadowArray;
        [[vk::binding(5, 0)]] TextureCube<float> ShadowCube;
        [[vk::binding(6, 0)]] TextureCubeArray<float> ShadowCubeArray;
        [[vk::binding(7, 0)]] Texture2DArray<float4> ColorArray;

        """;

    [Theory]
    [InlineData("return Source.CalculateLevelOfDetail(Linear, uv);")]
    [InlineData("return Source.CalculateLevelOfDetailUnclamped(Linear, uv);")]
    [InlineData("return float2(Source.CalculateLevelOfDetail(Linear, uv), Source.CalculateLevelOfDetailUnclamped(Linear, uv)).xyxy;")]
    [InlineData("return ColorArray.CalculateLevelOfDetail(Linear, float3(uv, 0)).xxxx;")]
    public void LevelOfDetailQueries(string body)
    {
        string src = TexturePrelude + "float4 PS(float2 uv : TEXCOORD0) : SV_Target0 { " + body + " }\n";
        string dis = Disassemble(Valid(src, "PS", ShaderStage.Pixel));
        string dxc = DxcDisassemble(src, "PS", "ps_6_0");
        Assert.Equal(Count(dxc, "OpImageQueryLod"), Count(dis, "OpImageQueryLod"));
        Assert.Contains("OpCapability ImageQuery", dis, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("return Shadow.GatherCmp(Cmp, uv, depth);")]
    [InlineData("return Shadow.GatherCmpRed(Cmp, uv, depth);")]
    [InlineData("return Shadow.GatherCmp(Cmp, uv, depth, int2(1, -1));")]
    [InlineData("return ShadowArray.GatherCmp(Cmp, float3(uv, 2), depth);")]
    [InlineData("return ShadowArray.GatherCmp(Cmp, float3(uv, 2), depth, int2(0, 1));")]
    [InlineData("return ShadowCube.GatherCmp(Cmp, float3(uv, 1), depth);")]
    [InlineData("return ShadowCubeArray.GatherCmp(Cmp, float4(uv, 1, 2), depth);")]
    public void GatherCompare(string body)
    {
        string src = TexturePrelude + "float4 PS(float2 uv : TEXCOORD0, float depth : TEXCOORD1) : SV_Target0 { " + body + " }\n";
        string dis = Disassemble(Valid(src, "PS", ShaderStage.Pixel));
        string dxc = DxcDisassemble(src, "PS", "ps_6_0");
        Assert.Equal(Count(dxc, "OpImageDrefGather"), Count(dis, "OpImageDrefGather"));
        Assert.Equal(Count(dxc, "ConstOffset"), Count(dis, "ConstOffset"));
    }

    [Fact]
    public void GatherCompareOfOtherChannelsSaysToUseGatherCmp()
    {
        string src = TexturePrelude + "float4 PS(float2 uv : TEXCOORD0) : SV_Target0 { return Shadow.GatherCmpGreen(Cmp, uv, 0.5); }\n";
        var r = Compile(src, "PS", ShaderStage.Pixel);
        var d = Assert.Single(r.Diagnostics);
        Assert.Equal("OSH0350", d.Code);
        Assert.Contains("Use GatherCmp", d.Message, StringComparison.Ordinal);
        Assert.Equal(TexturePrelude.Count(c => c == '\n') + 1, d.Span.Line);
    }

    [Fact]
    public void GatherCompareOnATextureWithoutGatherSaysSo()
    {
        string src = TexturePrelude + "[[vk::binding(8, 0)]] Texture3D<float> Vol;\nfloat4 PS(float3 uv : TEXCOORD0) : SV_Target0 { return Vol.GatherCmp(Cmp, uv, 0.5); }\n";
        var r = Compile(src, "PS", ShaderStage.Pixel);
        var d = Assert.Single(r.Diagnostics);
        Assert.Equal("OSH0308", d.Code);
    }

    private static int Count(string text, string needle)
    {
        int n = 0;
        int at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            n++;
            at += needle.Length;
        }

        return n;
    }
}
