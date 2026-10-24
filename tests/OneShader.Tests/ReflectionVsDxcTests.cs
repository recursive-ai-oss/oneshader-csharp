using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using OneShader.Reflection;
using Xunit;

namespace OneShader.Tests;

/// <summary>Compares reflection from the front end with what DXC's SPIR-V path decorates for the same file.</summary>
public partial class ReflectionVsDxcTests
{
    [GeneratedRegex(@"OpMemberName %(\w+) (\d+) ""(\w+)""")]
    private static partial Regex MemberName();

    [GeneratedRegex(@"OpMemberDecorate %(\w+) (\d+) Offset (\d+)")]
    private static partial Regex MemberOffset();

    [GeneratedRegex(@"OpDecorate %(\w+) (Binding|DescriptorSet) (\d+)")]
    private static partial Regex VarDecoration();

    [GeneratedRegex(@"OpDecorate %(\w+) ArrayStride (\d+)")]
    private static partial Regex Stride();

    public static IEnumerable<object[]> Files() => CorpusFiles.Shaders("2d").Select(p => new object[] { p });

    private static string Profile(ShaderStage s) => s switch { ShaderStage.Vertex => "vs_6_0", ShaderStage.Pixel => "ps_6_0", _ => "cs_6_0" };

    [Theory]
    [MemberData(nameof(Files))]
    public void CbufferOffsetsAndBindingsEqualDxc(string path)
    {
        string? dxc = Oracles.Find("dxc");
        string? dis = Oracles.Find("spirv-dis");
        Assert.SkipUnless(dxc is not null && dis is not null, "dxc or spirv-dis is not installed");
        var r = FrontendCorpusTests.Run(File.ReadAllText(path), Path.GetFileName(path));
        Assert.NotNull(r.Reflection);
        string dir = Path.Combine(Path.GetTempPath(), "osh-u4r-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var ep in r.Reflection!.EntryPoints)
            {
                string spv = Path.Combine(dir, ep.Name + ".spv");
                var (code, outp) = Oracles.Run(dxc!, ["-spirv", "-fspv-target-env=vulkan1.2", "-T", Profile(ep.Stage), "-E", ep.Name, path, "-Fo", spv], dir);
                Assert.True(code == 0, outp);
                var (_, text) = Oracles.Run(dis!, [spv], dir);
                var names = new Dictionary<(string, int), string>();
                foreach (Match m in MemberName().Matches(text))
                {
                    names[(m.Groups[1].Value, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))] = m.Groups[3].Value;
                }

                var offsets = new Dictionary<(string, string), uint>();
                foreach (Match m in MemberOffset().Matches(text))
                {
                    if (names.TryGetValue((m.Groups[1].Value, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)), out var mn))
                    {
                        offsets[(m.Groups[1].Value, mn)] = uint.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                    }
                }

                var vars = new Dictionary<(string, string), uint>();
                foreach (Match m in VarDecoration().Matches(text))
                {
                    vars[(m.Groups[1].Value, m.Groups[2].Value)] = uint.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                }

                var strides = new Dictionary<string, uint>();
                foreach (Match m in Stride().Matches(text))
                {
                    strides[m.Groups[1].Value] = uint.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                }

                foreach (var res in r.Reflection.Resources)
                {
                    if (!ep.UsedResources.Contains(res.Name))
                    {
                        continue;
                    }

                    var slot = res.Slots[0];
                    Assert.True(vars.TryGetValue((res.Name, "Binding"), out uint b), $"{res.Name}: DXC has no binding");
                    vars.TryGetValue((res.Name, "DescriptorSet"), out uint s);
                    Assert.Equal((s, b), (slot.Set, slot.Binding));
                    if (res.Layout is { Kind: ReflectedTypeKind.Struct } lay)
                    {
                        string typeName = res.Kind == ResourceKind.ConstantBuffer ? "type_" + lay.Name : lay.Name;
                        foreach (var mem in lay.Members)
                        {
                            Assert.True(offsets.TryGetValue((typeName, mem.Name), out uint off), $"{res.Name}.{mem.Name}: DXC has no offset");
                            Assert.Equal(off, mem.Offset);
                        }

                        if (res.Kind != ResourceKind.ConstantBuffer)
                        {
                            Assert.True(strides.TryGetValue("_runtimearr_" + lay.Name, out uint st), $"{res.Name}: no array stride");
                            Assert.Equal(st, lay.Size);
                        }
                    }
                }
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
