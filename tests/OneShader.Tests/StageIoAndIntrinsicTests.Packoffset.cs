using System;
using System.Linq;
using Xunit;

namespace OneShader.Tests;

public sealed partial class StageIoAndIntrinsicTests
{
    // ------------------------------------------------------------------ packoffset overlap

    private static string[] Errors(string src, string entry = "VS", ShaderStage stage = ShaderStage.Vertex) =>
        [.. Compile(src, entry, stage).Diagnostics.Select(d => d.ToString())];

    [Fact]
    public void PackoffsetOverlapIsAnErrorOnTheLaterMember()
    {
        const string Src = "cbuffer Overlap { float4 A : packoffset(c0);\n float4 B : packoffset(c0); };\nfloat4 VS() : SV_Position { return A + B; }\n";
        var r = Compile(Src, "VS", ShaderStage.Vertex);
        var d = Assert.Single(r.Diagnostics);
        Assert.Equal("t.hlsl(2,9): error OSH0332: packoffset overlap between 'B', 'A': 'B' occupies bytes 0 to 15 of 'Overlap', which 'A' already uses (bytes 0 to 15). Move 'B' to a register or component that 'A' does not use.", d.ToString());
        Assert.False(r.Success);
        Assert.Empty(r.Blobs);
    }

    [Theory]
    [InlineData("float4 A : packoffset(c0); float2 B : packoffset(c0.z); float Z : packoffset(c0.w);", 2)]
    [InlineData("float4 A : packoffset(c0); float B : packoffset(c1.x); float G : packoffset(c0.y);", 1)]
    [InlineData("float4 A : packoffset(c0); float3 B : packoffset(c0.y);", 1)]
    [InlineData("float4 A : packoffset(c0); float4 B : packoffset(c1); float3x3 M : packoffset(c1);", 1)]
    [InlineData("float3x3 M : packoffset(c0); float x : packoffset(c2.z);", 1)]
    [InlineData("float3x3 M : packoffset(c0); float x : packoffset(c2.w);", 1)]
    [InlineData("float a[2] : packoffset(c0); float b : packoffset(c1.y);", 1)]
    public void PackoffsetOverlapsLikeDxc(string members, int expectedErrors)
    {
        string src = "cbuffer C { " + members + " };\nfloat4 VS() : SV_Position { return 0; }\n";
        var errors = Errors(src);
        Assert.True(expectedErrors == errors.Length, string.Join("\n", errors));
        Assert.All(errors, e => Assert.Contains("packoffset overlap between", e, StringComparison.Ordinal));
        string? dxc = Oracles.Find("dxc");
        if (dxc is not null)
        {
            // DXC rejects each of these too.
            string dir = Dir();
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "t.hlsl"), src);
                var (code, output) = Oracles.Run(dxc, ["-spirv", "-T", "vs_6_0", "-E", "VS", "t.hlsl", "-Fo", "o.spv"], dir);
                Assert.NotEqual(0, code);
                Assert.Contains("overlap", output, StringComparison.Ordinal);
            }
            finally
            {
                System.IO.Directory.Delete(dir, true);
            }
        }
    }

    [Theory]
    [InlineData("float4 A : packoffset(c0); float B : packoffset(c1.x); float3 E : packoffset(c1.y);")]
    [InlineData("float a[2] : packoffset(c0); float b : packoffset(c2.y); float c : packoffset(c2.z);")]
    [InlineData("float3x3 M : packoffset(c0); float x : packoffset(c3.w);")]
    [InlineData("float pz : packoffset(c0.w); float4 pk : packoffset(c1); float3 py : packoffset(c3.y);")]
    public void NonOverlappingPackoffsetsCompile(string members)
    {
        string src = "[[vk::binding(0, 0)]] cbuffer C { " + members + " };\nfloat4 VS() : SV_Position { return 0; }\n";
        Valid(src, "VS", ShaderStage.Vertex);
        string? dxc = Oracles.Find("dxc");
        if (dxc is not null)
        {
            DxcDisassemble(src, "VS", "vs_6_0");
        }
    }
}
