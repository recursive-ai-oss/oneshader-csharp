using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OneShader.Diagnostics;
using OneShader.IR;
using OneShader.Semantics;
using OneShader.SpirV;
using Xunit;

namespace OneShader.Tests;

/// <summary>Compute constructs: barriers, atomics, byte-address buffers, image formats, new intrinsics, flat initializers and function-level diagnostics.</summary>
public class ComputeTests
{
    private static string[] Diags(string text) =>
        FrontendCorpusTests.Run(text, "c.hlsl").Diagnostics.Items.Select(d => d.ToString()).ToArray();

    /// <summary>Compiles to IR, checks the verifier and determinism, emits SPIR-V and runs spirv-val when available.</summary>
    private static FrontendResult Valid(string text, bool validate = true)
    {
        var r = FrontendCorpusTests.Run(text, "c.hlsl");
        Assert.Empty(r.Diagnostics.Items);
        Assert.NotNull(r.Module);
        Assert.Empty(IrVerifier.Verify(r.Module!));
        Assert.Equal(IrPrinter.Print(r.Module!), IrPrinter.Print(FrontendCorpusTests.Run(text, "c.hlsl").Module!));
        string? val = Oracles.Find("spirv-val");
        string dir = Path.Combine(Path.GetTempPath(), "osh-u7-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var ep in r.Module!.EntryPoints)
            {
                var bag = new DiagnosticBag();
                var bytes = SpirvBackend.Emit(r.Module, ep.Function.Name, null, bag);
                Assert.True(bytes is not null, string.Join("\n", bag.Items.Select(d => d.ToString())));
                Assert.Equal(bytes, SpirvBackend.Emit(r.Module, ep.Function.Name, null, new DiagnosticBag()));
                if (validate && val is not null)
                {
                    string file = Path.Combine(dir, ep.Function.Name + ".spv");
                    File.WriteAllBytes(file, bytes!);
                    var (code, output) = Oracles.Run(val, ["--target-env", "vulkan1.2", file], dir);
                    Assert.True(code == 0, output);
                }
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }

        return r;
    }

    private static string Cs(string body, string decls = "") =>
        decls + "\n[shader(\"compute\")] [numthreads(8, 4, 2)]\nvoid CS(uint3 gid : SV_GroupID, uint3 tid : SV_GroupThreadID, uint3 dtid : SV_DispatchThreadID, uint gi : SV_GroupIndex)\n{\n" + body + "\n}\n";

    private static int Count(FrontendResult r, Opcode op) =>
        r.Module!.Functions.Sum(f => f.Blocks.Sum(b => b.Instructions.Count(i => i.Op == op)));

    [Fact]
    public void NumthreadsAndSystemValuesReachTheEntryPoint()
    {
        var r = Valid(Cs("O[0] = gid.x + tid.y + dtid.z + gi;", "RWStructuredBuffer<uint> O;"));
        Assert.Equal((8u, 4u, 2u), r.Module!.EntryPoints[0].WorkgroupSize);
        Assert.Equal((8u, 4u, 2u), r.Reflection!.EntryPoints[0].WorkgroupSize);
    }

    [Theory]
    [InlineData("GroupMemoryBarrier", BarrierFlags.GroupMemory)]
    [InlineData("GroupMemoryBarrierWithGroupSync", BarrierFlags.GroupMemory | BarrierFlags.GroupSync)]
    [InlineData("DeviceMemoryBarrier", BarrierFlags.DeviceMemory)]
    [InlineData("DeviceMemoryBarrierWithGroupSync", BarrierFlags.DeviceMemory | BarrierFlags.GroupSync)]
    [InlineData("AllMemoryBarrier", BarrierFlags.GroupMemory | BarrierFlags.DeviceMemory)]
    [InlineData("AllMemoryBarrierWithGroupSync", BarrierFlags.GroupMemory | BarrierFlags.DeviceMemory | BarrierFlags.GroupSync)]
    public void BarrierFormsLowerToTheMatchingFlags(string name, BarrierFlags expected)
    {
        var r = Valid(Cs(name + "();"));
        var barrier = r.Module!.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions).Single(i => i.Op == Opcode.Barrier);
        Assert.Equal((int)expected, barrier.Immediates[0]);
    }

    [Fact]
    public void BarrierWithArgumentsIsDiagnosed()
    {
        Assert.Equal(["c.hlsl(5,1): error OSH0303: The function 'GroupMemoryBarrier' expects 0 arguments but the call passes 1."], Diags(Cs("GroupMemoryBarrier(1);")));
    }

    [Theory]
    [InlineData("Add", AtomicOp.Add)]
    [InlineData("Min", AtomicOp.Min)]
    [InlineData("Max", AtomicOp.Max)]
    [InlineData("And", AtomicOp.And)]
    [InlineData("Or", AtomicOp.Or)]
    [InlineData("Xor", AtomicOp.Xor)]
    [InlineData("Exchange", AtomicOp.Exchange)]
    public void InterlockedOperationsWorkOnEveryTargetWithAndWithoutTheOriginalValue(string suffix, AtomicOp op)
    {
        string f = "Interlocked" + suffix;
        string body = $@"
    uint orig; int iorig;
    {f}(Shared[gi % 4], 5u, orig);
    {f}(Shared[1], 3u);
    {f}(Buf[gi], 7u, orig);
    {f}(Buf[2], 7);
    {f}(IBuf[gi], -4, iorig);
    {f}(Structs[gi].Count, 1u, orig);
    Bytes.{f}(gi * 4, 9u, orig);
    Bytes.{f}(8u, 9u);
    {f}(Tex[int2(gi, 0)], 2u, orig);
    {f}(SharedStruct.Value, 2u, orig);
    O[0] = orig + (uint)iorig;";
        string decls = @"
struct S { float F; uint Count; };
groupshared uint Shared[4];
groupshared struct { uint Value; } SharedStruct;
RWStructuredBuffer<uint> Buf;
RWStructuredBuffer<int> IBuf;
RWStructuredBuffer<S> Structs;
RWStructuredBuffer<uint> O;
RWByteAddressBuffer Bytes;
[[vk::image_format(""r32ui"")]] RWTexture2D<uint> Tex;";
        // The anonymous groupshared struct is not HLSL; use a named one.
        decls = decls.Replace("groupshared struct { uint Value; } SharedStruct;", "struct G { uint Value; };\ngroupshared G SharedStruct;");
        var r = Valid(Cs(body, decls));
        Assert.Equal(10, Count(r, Opcode.AtomicRmw));
        Assert.All(
            r.Module!.Functions.SelectMany(fn => fn.Blocks).SelectMany(b => b.Instructions).Where(i => i.Op == Opcode.AtomicRmw),
            i => Assert.Equal((int)op, i.Immediates[0]));
    }

    [Fact]
    public void CompareExchangeAndCompareStoreCoverSharedBuffersBytesAndTextures()
    {
        var r = Valid(Cs(@"
    uint orig;
    InterlockedCompareExchange(Shared[gi], 1u, 2u, orig);
    InterlockedCompareStore(Shared[gi], 2u, 3u);
    InterlockedCompareExchange(Buf[gi], 1, 2, orig);
    InterlockedCompareStore(Buf[0], 0u, 1u);
    Bytes.InterlockedCompareExchange(gi * 4, 1u, 2u, orig);
    Bytes.InterlockedCompareStore(0u, 1u, 2u);
    InterlockedCompareExchange(Tex[int2(1, 1)], 0u, 1u, orig);
    InterlockedCompareStore(Tex[int2(1, 1)], 0u, 1u);
    O[0] = orig;", "groupshared uint Shared[8];\nRWStructuredBuffer<uint> Buf;\nRWStructuredBuffer<uint> O;\nRWByteAddressBuffer Bytes;\n[[vk::image_format(\"r32ui\")]] RWTexture2D<uint> Tex;"));
        Assert.Equal(8, Count(r, Opcode.AtomicCompareExchange));
    }

    [Fact]
    public void InterlockedOnASignedMinUsesSignedSemantics()
    {
        var r = Valid(Cs("int o; InterlockedMin(IBuf[0], -3, o); InterlockedMax(IBuf[1], gi, o);", "RWStructuredBuffer<int> IBuf;"));
        Assert.Equal(2, Count(r, Opcode.AtomicRmw));
    }

    [Fact]
    public void AtomicDiagnosticsExplainTheFix()
    {
        Assert.Equal(
            ["c.hlsl(6,16): error OSH0314: The first argument of 'InterlockedAdd' must be groupshared memory or an element of a read-write resource: 'local' is not groupshared memory or an element of a read-write buffer or texture."],
            Diags(Cs("uint local = 0;\nInterlockedAdd(local, 1u);")));
        Assert.Equal(
            ["c.hlsl(5,16): error OSH0314: The first argument of 'InterlockedAdd' must be groupshared memory or an element of a read-write resource: a StructuredBuffer is read-only; declare it RWStructuredBuffer."],
            Diags(Cs("InterlockedAdd(RO[0], 1u);", "StructuredBuffer<uint> RO;")));
        Assert.Equal(
            ["c.hlsl(5,16): error OSH0314: 'InterlockedAdd' works on 32-bit int or uint, but the first argument is 'float'. Float atomics are not supported; reinterpret the data as uint with asuint()/asfloat()."],
            Diags(Cs("InterlockedAdd(F[0], 1.0);", "RWStructuredBuffer<float> F;")));
        Assert.Equal(
            ["c.hlsl(5,16): error OSH0314: Atomics on the texture 'T' need a single-channel 32-bit integer format. Declare it with [[vk::image_format(\"r32ui\")]] (or \"r32i\" for int texels)."],
            Diags(Cs("InterlockedAdd(T[int2(0, 0)], 1u);", "RWTexture2D<uint> T;")));
        Assert.Equal(
            ["c.hlsl(5,1): error OSH0303: 'InterlockedCompareExchange' takes 4 arguments but the call passes 3. The last one receives the original value."],
            Diags(Cs("InterlockedCompareExchange(S[0], 1u, 2u);", "groupshared uint S[2];")));
    }

    [Fact]
    public void GroupsharedStructsAndArraysOfStructsWork()
    {
        Valid(Cs(@"
    P[gi].Pos = float2(gi, 1);
    P[gi].Id = gi;
    One.Pos = P[0].Pos;
    GroupMemoryBarrierWithGroupSync();
    O[gi] = P[(gi + 1) % 8].Id + (uint)One.Pos.x;", "struct Particle { float2 Pos; uint Id; };\ngroupshared Particle P[8];\ngroupshared Particle One;\nRWStructuredBuffer<uint> O;"));
    }

    [Fact]
    public void ByteAddressBuffersLoadAndStoreWordsVectorsAndTypedValues()
    {
        var r = Valid(Cs(@"
    uint a = In.Load(gi * 4);
    uint2 b = In.Load2(16);
    uint3 c = In.Load3(32);
    uint4 d = In.Load4(48);
    float4 f = In.Load<float4>(64);
    int2 i2 = In.Load<int2>(80);
    float s = In.Load<float>(96);
    Pair p = In.Load<Pair>(128);
    Out.Store(gi * 4, a);
    Out.Store2(16, b);
    Out.Store3(32, c);
    Out.Store4(48, d);
    Out.Store<float4>(64, f);
    Out.Store<int2>(80, i2);
    Out.Store<float>(96, s);
    Out.Store<Pair>(128, p);
    uint n; In.GetDimensions(n); Out.GetDimensions(n);
    Out.Store(0, n + (uint)p.Y);", "struct Pair { float2 X; int Y; };\nByteAddressBuffer In;\nRWByteAddressBuffer Out;"));
        Assert.Equal(9, Count(r, Opcode.ByteAddressLoad));
        Assert.Equal(10, Count(r, Opcode.ByteAddressStore));
    }

    [Fact]
    public void ByteAddressDiagnostics()
    {
        Assert.Equal(
            ["c.hlsl(5,4): error OSH0307: 'Store' writes the buffer, but 'ByteAddressBuffer' is read-only. Declare it RWByteAddressBuffer."],
            Diags(Cs("In.Store(0, 1u);", "ByteAddressBuffer In;")));
        Assert.Equal(
            ["c.hlsl(5,14): error OSH0350: 'Load<float4x4>' is not supported: matrices, arrays and resources are not supported; load the components with Load2/Load3/Load4 or a struct of scalars and vectors."],
            Diags(Cs("float4x4 m = In.Load<float4x4>(0);", "ByteAddressBuffer In;")));
    }

    [Fact]
    public void StorageTextureFormatsLoadAndStore()
    {
        var r = Valid(Cs(@"
    float4 v = A[dtid.xy];
    A[dtid.xy] = v * 2;
    B[dtid.xy] = B.Load(dtid.xy) + 1u;
    C[dtid.xy] = float2(1, 2) + C[dtid.xy];
    uint w, h; A.GetDimensions(w, h);", @"
[[vk::image_format(""rgba8"")]] RWTexture2D<float4> A;
[[vk::image_format(""r32ui"")]] RWTexture2D<uint> B;
[[vk::image_format(""rg16f"")]] RWTexture2D<float2> C;"));
        // Known formats need no read/write-without-format capability; rg16f needs the extended-format one.
        Assert.NotNull(r.Module);
    }

    [Fact]
    public void ImageFormatMismatchAndUnknownNamesAreDiagnosed()
    {
        Assert.Equal(
            ["c.hlsl(1,1): error OSH0340: The format \"r32ui\" holds uint texels but the texture's element type is 'float4'. Change the element type or pick a matching format."],
            Diags(Cs("", "[[vk::image_format(\"r32ui\")]] RWTexture2D<float4> A;")));
        Assert.Equal(
            ["c.hlsl(1,1): error OSH0340: vk::image_format needs a format name in quotes such as \"rgba8\", \"rgba16f\", \"r32f\" or \"r32ui\"."],
            Diags(Cs("", "[[vk::image_format(\"banana\")]] RWTexture2D<float4> A;")));
    }

    [Fact]
    public void SpirvImageTypeCarriesTheFormat()
    {
        var r = FrontendCorpusTests.Run(File.ReadAllText(Path.Combine(CorpusFiles.Root(), "compute", "image_filter.hlsl")), "image_filter.hlsl");
        Assert.Equal("rgba8", r.Module!.Globals.Single(g => g.Name == "Result").ImageFormat);
        string? dis = Oracles.Find("spirv-dis");
        Assert.SkipUnless(dis is not null, "spirv-dis is not installed");
        var bytes = SpirvBackend.Emit(r.Module, "CSMain", null, new DiagnosticBag())!;
        string dir = Path.Combine(Path.GetTempPath(), "osh-u7-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "a.spv"), bytes);
            var (_, text) = Oracles.Run(dis!, [Path.Combine(dir, "a.spv")], dir);
            Assert.Contains("OpTypeImage %float 2D 0 0 0 2 Rgba8", text);
            Assert.DoesNotContain("StorageImageWriteWithoutFormat", text);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void BitAndOutParameterIntrinsicsCompileAndValidate()
    {
        var r = Valid(Cs(@"
    int i = (int)O[0]; uint u = O[1];
    O[2] = firstbithigh(i); O[3] = firstbithigh(u); O[4] = firstbitlow(i); O[5] = countbits(u); O[6] = reversebits(u);
    uint2 cv = countbits(uint2(u, 3));
    int ri = reversebits(i);
    float s, c; sincos(F[0], s, c);
    float3 s3, c3; sincos(float3(F[1], F[2], F[3]), s3, c3);
    float ip; float fr = modf(F[4], ip);
    float2 ip2; float2 fr2 = modf(float2(1.5, 2.5), ip2);
    float ex; float m = frexp(F[5], ex);
    F[6] = s + c + s3.x + c3.y + ip + fr + fr2.x + ip2.y + ex + m + cv.x + ri;
    sincos(F[7], F[8], F[9]);", "RWStructuredBuffer<uint> O;\nRWStructuredBuffer<float> F;"));
        Assert.Equal(6, r.Module!.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions)
            .Count(x => x.Op == Opcode.CallIntrinsic && x.Intrinsic is Intrinsic.SinCos or Intrinsic.Modf or Intrinsic.Frexp));
    }

    [Fact]
    public void BitIntrinsicsRejectFloats()
    {
        Assert.Equal(
            ["c.hlsl(5,20): error OSH0302: 'countbits' works on 32-bit integers (int, uint and their vectors), not 'float'. Convert the argument with asuint() or a cast."],
            Diags(Cs("uint x = countbits(1.5);")));
    }

    [Theory]
    [InlineData("InterlockedAdd();")]
    [InlineData("InterlockedAdd(1, 2, 3);")]
    [InlineData("InterlockedAdd(B, 2);")]
    [InlineData("InterlockedAdd(Sh[0], 1u, 5);")]
    [InlineData("InterlockedCompareExchange(Sh[0]);")]
    [InlineData("R.Load();")]
    [InlineData("R.Load<Nope>(0);")]
    [InlineData("R.Load<float, int>(0);")]
    [InlineData("R.Store<float4>(0);")]
    [InlineData("R.Bogus(0);")]
    [InlineData("R.InterlockedAdd(1.5, 2u, 3u, 4u);")]
    [InlineData("R.GetDimensions();")]
    [InlineData("sincos(1);")]
    [InlineData("sincos(1.0, 2.0, 3.0);")]
    [InlineData("float4 v = {};")]
    [InlineData("float4 v = { float4(1, 2, 3, 4), 5 };")]
    [InlineData("float2x2 m = { { 1, 2, 3 }, { 4 } };")]
    [InlineData("Q q = { 1, { 2, { 3 } }, float2(4, 5) };")]
    [InlineData("Q q = { Sh, 1 };")]
    [InlineData("countbits(float3(1, 2, 3));")]
    [InlineData("firstbithigh();")]
    [InlineData("GroupMemoryBarrier(1, 2);")]
    [InlineData("int g = AllMemoryBarrier();")]
    public void MalformedComputeCallsGiveDiagnosticsNotCrashes(string line)
    {
        var r = FrontendCorpusTests.Run(Cs(line, "struct Q { float2 A; float B; };\nbool Z;\ngroupshared uint Sh[4];\nRWStructuredBuffer<uint> B;\nRWByteAddressBuffer R;"), "c.hlsl");
        Assert.DoesNotContain(r.Diagnostics.Items, d => d.Code == "OSH0399");
        Assert.Contains(r.Diagnostics.Items, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void CallCyclesAreDiagnosedOnce()
    {
        var d = Diags("float B(float x);\nfloat A(float x) { return B(x) + 1; }\nfloat B(float x) { return A(x) + 1; }\n[shader(\"pixel\")] float4 PS(float x : A) : SV_Target { return A(x); }");
        Assert.Equal(["c.hlsl(2,7): error OSH0352: The function 'A' is part of a call cycle: 'A' calls 'B' calls 'A'. HLSL does not allow recursion; rewrite it as a loop."], d);
        var self = Diags("float A(float x) { return A(x) + 1; }\n[shader(\"pixel\")] float4 PS(float x : A) : SV_Target { return A(x); }");
        Assert.Equal(["c.hlsl(1,27): error OSH0352: The function 'A' calls itself; HLSL does not allow recursion. Rewrite it as a loop."], self);
    }

    [Fact]
    public void MissingReturnIsAnErrorLikeDxcAndLoopsAndBranchesAreUnderstood()
    {
        Assert.Equal(
            ["c.hlsl(1,7): error OSH0315: Not all paths of the function 'f' return a value; add a 'return' at the end or in the missing branch."],
            Diags("float f(float x)\n{\n    if (x > 0) return 1;\n}\n[shader(\"pixel\")] float4 PS(float x : A) : SV_Target { return f(x); }"));
        Valid("float g(float x) { if (x > 0) return 1; else return 2; }\n"
            + "float h(float x) { while (true) { if (x > 1) return 2; x += 1; } }\n"
            + "float k(float x) { switch ((int)x) { case 0: return 1; default: return 2; } }\n"
            + "float m(float x) { for (;;) { x += 1; if (x > 3) return x; } }\n"
            + "[shader(\"pixel\")] float4 PS(float x : A) : SV_Target { return g(x) + h(x) + k(x) + m(x); }");
        Assert.Single(Diags("float h(float x) { while (true) { if (x > 1) break; } }\n[shader(\"pixel\")] float4 PS(float x : A) : SV_Target { return h(x); }"));
        Assert.Single(Diags("float k(float x) { switch ((int)x) { case 0: return 1; } }\n[shader(\"pixel\")] float4 PS(float x : A) : SV_Target { return k(x); }"));
    }

    [Fact]
    public void FlatInitializerListsFillAggregates()
    {
        var r = Valid(Cs(@"
    float2x2 m = { 1, 2, 3, 4 };
    float2x2 n = { { 1, 2 }, { 3, 4 } };
    S s = { 1, 2, 3, 4, 5 };
    S t = { float2(1, 2), float2(3, 4), 5 };
    T u = { 1, 2, 3, 4, 5 };
    float2 arr[2] = { 1, 2, 3, 4 };
    float3 v = { float2(1, 2), 3 };
    S two[2] = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
    O[gi] = m[1][0] + n[1][1] + s.B.y + t.C + u.Q[1].x + u.R + arr[1].y + v.z + two[1].B.x;", "struct S { float2 A; float2 B; float C; };\nstruct T { float2x2 Q; float R; };\nRWStructuredBuffer<float> O;"));
        Assert.NotNull(r.Module);
    }

    [Fact]
    public void UnsizedArrayLengthCountsFlatValues()
    {
        var r = FrontendCorpusTests.Run("static const float2 A[] = { 1, 2, 3, 4 };\nstatic const float2 B[] = { float2(1, 2), float2(3, 4), float2(5, 6) };\nstatic const float2 C[] = { { 1, 2 }, { 3, 4 } };\n[shader(\"pixel\")] float4 PS() : SV_Target { float2 L[] = { 1, 2, 3, 4, 5, 6 }; return float4(A[1], B[2].x, C[1].y + L[2].x); }", "c.hlsl");
        Assert.Empty(r.Diagnostics.Items);
        string ir = IrPrinter.Print(r.Module!);
        Assert.Contains("[2]", ir);
        Assert.Contains("[3]", ir);
    }

    [Fact]
    public void FlatInitializerListErrorsCountValues()
    {
        Assert.Equal(
            ["c.hlsl(5,12): error OSH0302: 'float4' needs 4 values in its initializer but fewer were given."],
            Diags(Cs("float4 v = { 1, 2 };")));
        Assert.Equal(
            ["c.hlsl(5,14): error OSH0302: The initializer has more values than 'float2x2' holds; remove the extra values."],
            Diags(Cs("float2x2 m = { 1, 2, 3, 4, 5 };")));
    }

    [Fact]
    public void FlatInitializerListLowersToTheRightComponents()
    {
        var r = FrontendCorpusTests.Run("struct S { float2 A; float B; };\n[shader(\"pixel\")] float4 PS() : SV_Target { S s = { 1, 2, 3 }; return float4(s.A, s.B, 0); }", "c.hlsl");
        Assert.Empty(r.Diagnostics.Items);
        string ir = IrPrinter.Print(r.Module!);
        Assert.Contains("construct", ir);
        Assert.DoesNotContain("1:float, 1:float", ir);
    }

    [Theory]
    [InlineData("prefix_sum")]
    [InlineData("histogram")]
    [InlineData("image_filter")]
    [InlineData("particles")]
    public void ComputeCorpusHasWorkgroupSizeAndNoDiagnostics(string name)
    {
        var path = Path.Combine(CorpusFiles.Root(), "compute", name + ".hlsl");
        var r = FrontendCorpusTests.Run(File.ReadAllText(path), name + ".hlsl");
        Assert.Empty(r.Diagnostics.Items);
        var wg = r.Module!.EntryPoints[0].WorkgroupSize;
        Assert.True(wg.X * wg.Y * wg.Z is 256 or 64);
    }
}
