using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace OneShader.ParityTests;

/// <summary>Case parsing, input generation and corpus bookkeeping; no GPU or tools needed.</summary>
public class CaseTests
{
    [Fact]
    public void CorpusHasEveryExpectedCase()
    {
        string[] expected =
        [
            "append_consume", "blit", "bloom_down", "bloom_down_first", "bloom_up", "blur_h", "blur_v", "cluster_build",
            "clustered_lights", "color_grade", "cull_instances", "depth_bounds", "histogram", "ibl_gold",
            "ibl_plastic", "ibl_prefilter", "ibl_sky", "image_filter", "lang_inheritance", "lang_members",
            "lang_operators", "lang_precise_math", "lang_resource_arrays", "lang_resource_locals", "lang_sizeof_static", "lang_statics_and_literals", "lang_switch_flow", "lang_templates", "light_cull", "msdf_text", "particles",
            "pbr_forward", "prefix_sum", "push_kernel", "push_lit", "quad_blur", "quad_grade", "quad_ops", "quad_sprite", "sdf_text",
            "shadow_cascades", "shadow_cascades_poisson", "shadow_depth", "skinned", "sprite", "ssao", "stage_io", "stage_layouts", "stage_layouts_off",
            "tile_flat", "tile_ids", "tilemap", "tonemap_aces", "tonemap_reinhard", "ui", "ui_rect", "wave_ballot", "wave_reduce", "wave_scan_compact",
        ];
        Assert.Equal(expected, Corpus.CaseNames);
    }

    [Theory]
    [MemberData(nameof(Corpus.Cases), MemberType = typeof(Corpus))]
    public void CaseParsesAndItsShaderDeclaresTheEntryPoints(string name)
    {
        var model = Corpus.Load(name);
        Assert.Equal(name, model.Name);
        string source = File.ReadAllText(Path.Combine(Tools.CorpusDir, model.Shader));
        foreach (var (entry, _) in ParityCompilers.Entries(model))
        {
            Assert.Matches(new Regex(@"\b" + entry + @"\s*\("), source);
        }
        // Inputs materialize without throwing and every buffer is a whole number of 16-byte rows.
        var data = CaseData.Build(model);
        Assert.All(data.Buffers.Values, b => Assert.Equal(0, b.Length % 16));
        Assert.True(model.Graphics is not null || model.Dispatches.Count > 0);
    }

    [Theory]
    [MemberData(nameof(Corpus.Cases), MemberType = typeof(Corpus))]
    public void InputsAreDeterministic(string name)
    {
        var a = CaseData.Build(Corpus.Load(name));
        var b = CaseData.Build(Corpus.Load(name));
        Assert.Equal(a.Buffers.Keys, b.Buffers.Keys);
        foreach (var k in a.Buffers.Keys)
        {
            Assert.Equal(a.Buffers[k], b.Buffers[k]);
        }
        foreach (var k in a.Images.Keys)
        {
            Assert.Equal(a.Images[k], b.Images[k]);
        }
    }

    [Fact]
    public void UiRectInstancesFillWholeStructs()
    {
        var model = Corpus.Load("ui_rect");
        int instances = model.Graphics!.Instances;
        int index = model.Resources.FindIndex(r => r.Kind == ResourceKind.Storage);
        byte[] bytes = DataGen.Materialize(model.Resources[index].Data!);
        // UiInstance is 7 float4 rows = 112 bytes; the case must supply exactly one per drawn instance.
        Assert.Equal(instances * 112, bytes.Length);
    }

    [Fact]
    public void DataExpressionsParse()
    {
        byte[] b = DataGen.Materialize("1.5 7u 0xFFu -3i");
        Assert.Equal(16, b.Length);
        Assert.Equal(1.5f, BitConverter.ToSingle(b, 0));
        Assert.Equal(7u, BitConverter.ToUInt32(b, 4));
        Assert.Equal(255u, BitConverter.ToUInt32(b, 8));
        Assert.Equal(-3, BitConverter.ToInt32(b, 12));

        byte[] q = DataGen.Materialize("@quadindex(2)", 2);
        ushort[] idx = new ushort[q.Length / 2];
        Buffer.BlockCopy(q, 0, idx, 0, q.Length);
        Assert.Equal(new ushort[] { 0, 1, 2, 2, 1, 3, 4, 5, 6, 6, 5, 7 }, idx);

        Assert.Equal(DataGen.Materialize("@u32(9,64,10)"), DataGen.Materialize("@u32(9,64,10)"));
        Assert.NotEqual(DataGen.Materialize("@u32(9,64)"), DataGen.Materialize("@u32(10,64)"));
        Assert.Throws<FormatException>(() => DataGen.Materialize("12"));
        Assert.Throws<FormatException>(() => DataGen.Materialize("@nope(1)"));
    }

    [Fact]
    public void MalformedCasesNameTheLine()
    {
        var e = Assert.Throws<CaseFormatException>(() => CaseFile.Parse("case x\nshader a.hlsl\nblend sparkly\n", "bad.case"));
        Assert.Equal("bad.case(3): unknown blend mode 'sparkly' (use none, premul or alpha)", e.Message);
        e = Assert.Throws<CaseFormatException>(() => CaseFile.Parse("case x\nshader a.hlsl\n", "bad.case"));
        Assert.Contains("either 'graphics' and 'draw', or 'dispatch'", e.Message, StringComparison.Ordinal);
        e = Assert.Throws<CaseFormatException>(() => CaseFile.Parse("frobnicate\n", "bad.case"));
        Assert.Equal("bad.case(1): unknown directive 'frobnicate'", e.Message);
    }

    [Fact]
    public void EveryShaderHasLicenceHeaderAndReadmeEntry()
    {
        string readme = File.ReadAllText(Path.Combine(Tools.CorpusDir, "README.md"));
        foreach (string path in Corpus.ShaderPaths)
        {
            string firstLine = File.ReadLines(Path.Combine(Tools.CorpusDir, path)).First();
            Assert.Equal("// SPDX-License-Identifier: MIT OR Apache-2.0", firstLine);
            Assert.Contains("`" + path + "`", readme, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryShaderIsUsedByACase()
    {
        var used = Corpus.CaseNames.Select(n => Corpus.Load(n).Shader).ToHashSet();
        Assert.Equal(Corpus.ShaderPaths.OrderBy(p => p, StringComparer.Ordinal), used.OrderBy(p => p, StringComparer.Ordinal));
    }
}
