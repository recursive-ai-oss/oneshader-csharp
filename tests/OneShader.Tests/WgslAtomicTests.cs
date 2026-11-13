using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace OneShader.Tests;

/// <summary>
/// Signed atomic min/max and compare-exchange on every kind of target. WGSL has no signed atomics on storage words and only a weak
/// compare-exchange, so the backend writes loops; they used to be helper functions taking a pointer into a storage buffer, which
/// WGSL does not allow (naga rejected them).
/// </summary>
public sealed partial class WgslBackendTests
{
    // memory kind, HLSL element type, (declaration, address of word 0, atomic call form)
    private static readonly string[] AtomicMemories = ["int_buffer", "uint_buffer", "byte_address", "shared_int", "shared_uint"];

    public static IEnumerable<object[]> AtomicCases()
    {
        foreach (string memory in AtomicMemories)
        {
            foreach (string op in new[] { "Min", "Max", "CompareExchange", "CompareStore" })
            {
                foreach (bool withOriginal in new[] { true, false })
                {
                    if (op is "CompareExchange" && !withOriginal)
                    {
                        continue; // HLSL's CompareExchange always has the original-value argument; CompareStore is the form without
                    }

                    if (op is "CompareStore" && withOriginal)
                    {
                        continue;
                    }

                    foreach (bool optimize in new[] { true, false })
                    {
                        yield return new object[] { memory, op, withOriginal, optimize };
                    }
                }
            }
        }
    }

    internal static string AtomicShader(string memory, string op, bool withOriginal)
    {
        bool isInt = memory is "int_buffer" or "shared_int";
        string t = isInt ? "int" : "uint";
        bool shared = memory.StartsWith("shared", StringComparison.Ordinal);
        string decl = memory switch
        {
            "int_buffer" or "uint_buffer" => "RWStructuredBuffer<" + t + "> B : register(u1);",
            "byte_address" => "RWByteAddressBuffer B : register(u1);",
            _ => "groupshared " + t + " G[4];",
        };
        string target = memory switch
        {
            "byte_address" => "0",
            _ => (shared ? "G" : "B") + "[0]",
        };
        string a = isInt ? "-3" : "0xFFFFFFFDu";
        string b = isInt ? "-7" : "0xFFFFFFF9u";
        string call = op switch
        {
            "CompareExchange" or "CompareStore" => a + ", " + b,
            _ => b,
        };
        string name = "Interlocked" + op;
        string stmt = memory == "byte_address"
            ? "B." + name + "(" + target + ", " + call + (withOriginal ? ", o" : "") + ");"
            : name + "(" + target + ", " + call + (withOriginal ? ", o" : "") + ");";
        string init = shared ? "    if (id.x == 0) { G[0] = " + a + "; }\n    GroupMemoryBarrierWithGroupSync();\n" : "";
        return "RWStructuredBuffer<uint> O : register(u0);\n" + decl + "\n" + (shared ? "" : "") +
            "[shader(\"compute\")][numthreads(8,1,1)]\nvoid CSMain(uint3 id : SV_DispatchThreadID)\n{\n" +
            init +
            "    " + (withOriginal ? t + " o = 0;\n    " : "") + stmt + "\n" +
            (withOriginal ? "    O[id.x] = (uint)o;\n" : "") +
            (shared ? "    GroupMemoryBarrierWithGroupSync();\n    O[8 + id.x] = (uint)G[0];\n" : "") +
            "}\n";
    }

    [Theory]
    [MemberData(nameof(AtomicCases))]
    public void SignedMinMaxAndCompareExchangeValidateInNaga(string memory, string op, bool withOriginal, bool optimize)
    {
        string? naga = Oracles.Find("naga");
        Assert.SkipUnless(naga is not null, "naga is not installed");
        var r = ShaderCompiler.Compile(AtomicShader(memory, op, withOriginal), "atomic.hlsl",
            new CompileOptions { Targets = [CompileTarget.Wgsl], Optimize = optimize }, TestContext.Current.CancellationToken);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        string text = r.Find("CSMain", CompileTarget.Wgsl)!.AsText();

        // Pointer parameters into storage or workgroup memory are not core WGSL; nothing may take one.
        Assert.DoesNotContain("ptr<", text, StringComparison.Ordinal);
        if (op == "Min" && memory == "int_buffer")
        {
            Assert.Contains("min(bitcast<i32>(", text, StringComparison.Ordinal);
        }

        ValidateWithNaga(naga!, r);
    }
}
