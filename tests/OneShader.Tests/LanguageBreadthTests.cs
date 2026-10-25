using System.Linq;
using Xunit;

namespace OneShader.Tests;

public class LanguageBreadthTests
{
    private static string[] Lines(string source) =>
        FrontendCorpusTests.Run(source).Diagnostics.Items
            .Select(d => $"{d.Span.Line},{d.Span.Column} {d.Severity} {d.Code}: {d.Message}").ToArray();

    [Fact]
    public void TemplateInstantiatedWithTypeLackingMember()
    {
        var l = Lines("template<typename T> T Sum(T a, T b) { return a.value + b.value; }\nstruct Plain { float x; };\n[shader(\"compute\")] [numthreads(1,1,1)] void CSMain() { Plain p; p.x = 1; Plain q = Sum(p, p); }\n");
        Assert.Contains("1,49 Error OSH0308: The struct 'Plain' has no member 'value'.", l);
        Assert.Contains("3,85 Info OSH0354: In instantiation of 'Sum<Plain>', requested here.", l);
    }

    [Fact]
    public void MissingMemberFunction()
    {
        var l = Lines("struct Counter { uint n; void Add() { n++; } uint Get() { return n; } };\n[shader(\"compute\")] [numthreads(1,1,1)] void CSMain() { Counter c; c.n = 0; c.Reset(); }\n");
        Assert.Equal(["2,79 Error OSH0308: The struct 'Counter' has no member function 'Reset'. Its member functions are: Add, Get."], l);
    }

    [Fact]
    public void ResourceLocalFromTwoResources()
    {
        var l = Lines("Texture2D<float4> A : register(t0); Texture2D<float4> B : register(t1);\nRWStructuredBuffer<float4> O : register(u2);\n[shader(\"compute\")] [numthreads(1,1,1)] void CSMain(uint3 id : SV_DispatchThreadID) {\n  Texture2D<float4> t = A;\n  if (id.x > 0) { t = B; }\n  O[0] = t.Load(int3(0,0,0));\n}\n");
        Assert.Single(l);
        Assert.StartsWith("4,21 Error OSH0331: The resource variable 't' refers to 'B' on one path and to 'A' on another", l[0]);
    }

    [Fact]
    public void GroupSyncBarrierInPixelShader()
    {
        var l = Lines("[shader(\"pixel\")] float4 PSMain() : SV_Target { GroupMemoryBarrierWithGroupSync(); return 0; }\n");
        Assert.Equal(["1,49 Error OSH0314: 'GroupMemoryBarrierWithGroupSync' synchronizes a thread group, which only compute shaders have, but the pixel shader 'PSMain' reaches it. Remove the call, or keep it in code that only compute entry points run."], l);
    }
}
