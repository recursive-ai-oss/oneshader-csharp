using System.Collections.Generic;
using OneShader.IR;
using OneShader.Reflection;
using OneShader.Types;
using Xunit;

namespace OneShader.Tests;

/// <summary>Hand-built IR for the shaders the SPIR-V backend tests compile, with the HLSL they mirror.</summary>
internal static class SpirvTestModules
{
    public sealed record Case(string Name, IrModule Module, string Entry, string Hlsl, string Profile);

    private static readonly ScalarType F = ScalarType.Float;
    private static readonly VectorType F2 = HlslType.Vector(F, 2);
    private static readonly VectorType F3 = HlslType.Vector(F, 3);
    private static readonly VectorType F4 = HlslType.Vector(F, 4);
    private static readonly VectorType U3 = HlslType.Vector(ScalarType.UInt, 3);

    private static GlobalVariable Stage(IrModule m, EntryPoint ep, string name, bool input, HlslType type, int? location, BuiltIn builtIn = BuiltIn.None)
    {
        var g = new GlobalVariable(name, input ? GlobalKind.Input : GlobalKind.Output, input ? AddressSpace.Input : AddressSpace.Output, type)
        {
            Location = location,
            BuiltIn = builtIn,
        };
        m.Globals.Add(g);
        (input ? ep.Inputs : ep.Outputs).Add(g);
        return g;
    }

    private static GlobalVariable Buffer(IrModule m, string name, ResourceType type, AddressSpace space, int set, int binding, StructType? element, BufferLayoutRules rules)
    {
        var g = new GlobalVariable(name, GlobalKind.Resource, space, type) { Set = set, Binding = binding };
        if (element is not null)
        {
            Assert.True(BufferLayout.TryCompute(element, rules, m.LayoutOptions, out var layout, out var error), error);
            g.Layout = layout;
        }

        m.Globals.Add(g);
        return g;
    }

    private static GlobalVariable Image(IrModule m, string name, HlslType type, int set, int binding)
    {
        var g = new GlobalVariable(name, GlobalKind.Resource, AddressSpace.UniformConstant, type) { Set = set, Binding = binding };
        m.Globals.Add(g);
        return g;
    }

    private static (EntryPoint Ep, IrBuilder B, IrFunction F) Entry(IrModule m, string name, ShaderStage stage)
    {
        var f = new IrFunction(name, HlslType.Void);
        m.Functions.Add(f);
        var ep = new EntryPoint(f, stage);
        m.EntryPoints.Add(ep);
        var b = new IrBuilder(f) { Block = f.AddBlock("entry") };
        return (ep, b, f);
    }

    private static Constant Vec(VectorType t, params float[] v)
    {
        var e = new Constant[v.Length];
        for (int i = 0; i < v.Length; i++)
        {
            e[i] = Constant.Float(v[i]);
        }

        return Constant.Composite(t, e);
    }

    /// <summary>A sprite vertex shader: cbuffer float4x4, mul, three vertex inputs, position plus two varyings.</summary>
    public static Case SpriteVertex()
    {
        var m = new IrModule { Name = "sprite.hlsl" };
        var cbStruct = new StructType("", [new StructField("viewProj", HlslType.Matrix(F, 4, 4))]);
        var cb = Buffer(m, "Constants", new ResourceType(ResourceTypeKind.ConstantBuffer, cbStruct), AddressSpace.Uniform, 0, 0, cbStruct, BufferLayoutRules.ConstantBuffer);
        var (ep, b, _) = Entry(m, "VSMain", ShaderStage.Vertex);
        var pos = Stage(m, ep, "in.var.POSITION", true, F2, 0);
        var uv = Stage(m, ep, "in.var.TEXCOORD0", true, F2, 1);
        var col = Stage(m, ep, "in.var.COLOR0", true, F4, 2);
        var outPos = Stage(m, ep, "gl_Position", false, F4, null, BuiltIn.Position);
        var outUv = Stage(m, ep, "out.var.TEXCOORD0", false, F2, 0);
        var outCol = Stage(m, ep, "out.var.COLOR0", false, F4, 1);

        var p = b.Load(pos);
        var u = b.Load(uv);
        var c = b.Load(col);
        var mp = b.AccessChain(cb, HlslType.Matrix(F, 4, 4), Constant.Int(0));
        var mat = b.Load(mp);
        var x = b.Extract(p, F, 0);
        var y = b.Extract(p, F, 1);
        var v = b.Construct(F4, x, y, Constant.Float(0), Constant.Float(1));
        var clip = b.Emit(Opcode.MatMul, F4, mat, v);
        b.Store(outPos, clip);
        b.Store(outUv, u);
        b.Store(outCol, c);
        b.Return();

        const string hlsl = """
            cbuffer Constants : register(b0, space0) { float4x4 viewProj; };
            struct VSIn { float2 pos : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR0; };
            struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; float4 color : COLOR0; };
            VSOut VSMain(VSIn i) { VSOut o; o.pos = mul(viewProj, float4(i.pos, 0, 1)); o.uv = i.uv; o.color = i.color; return o; }
            """;
        return new Case("sprite-vs", m, "VSMain", hlsl, "vs_6_0");
    }

    /// <summary>A sprite pixel shader: separate texture and sampler, Sample, multiply, SV_Target.</summary>
    public static Case SpritePixel()
    {
        var m = new IrModule { Name = "sprite.hlsl" };
        var tex = Image(m, "tex", new ResourceType(ResourceTypeKind.Texture2D, F4), 0, 0);
        var samp = Image(m, "samp", SamplerType.State, 0, 1);
        var (ep, b, _) = Entry(m, "PSMain", ShaderStage.Pixel);
        var uv = Stage(m, ep, "in.var.TEXCOORD0", true, F2, 0);
        var col = Stage(m, ep, "in.var.COLOR0", true, F4, 1);
        var target = Stage(m, ep, "out.var.SV_Target", false, F4, 0);
        var u = b.Load(uv);
        var c = b.Load(col);
        var s = b.Emit(Opcode.TextureSample, F4, tex, samp, u);
        b.Store(target, b.Binary(Opcode.Mul, s, c));
        b.Return();

        const string hlsl = """
            [[vk::binding(0, 0)]] Texture2D tex;
            [[vk::binding(1, 0)]] SamplerState samp;
            float4 PSMain(float2 uv : TEXCOORD0, float4 color : COLOR0) : SV_Target { return tex.Sample(samp, uv) * color; }
            """;
        return new Case("sprite-ps", m, "PSMain", hlsl, "ps_6_0");
    }

    /// <summary>A fullscreen triangle from SV_VertexID using integer shifts and masks.</summary>
    public static Case FullscreenTriangle()
    {
        var m = new IrModule { Name = "fullscreen.hlsl" };
        var (ep, b, _) = Entry(m, "VSMain", ShaderStage.Vertex);
        var vid = Stage(m, ep, "gl_VertexIndex", true, ScalarType.UInt, null, BuiltIn.VertexIndex);
        var outPos = Stage(m, ep, "gl_Position", false, F4, null, BuiltIn.Position);
        var outUv = Stage(m, ep, "out.var.TEXCOORD0", false, F2, 0);
        var id = b.Load(vid);
        var shifted = b.Binary(Opcode.Shl, id, Constant.UInt(1));
        var a = b.Binary(Opcode.BitAnd, shifted, Constant.UInt(2));
        var c = b.Binary(Opcode.BitAnd, id, Constant.UInt(2));
        var fa = b.Convert(a, F);
        var fc = b.Convert(c, F);
        var uv = b.Construct(F2, fa, fc);
        var scaled = b.Binary(Opcode.Mul, uv, Vec(F2, 2, 2));
        var ndc = b.Binary(Opcode.Sub, scaled, Vec(F2, 1, 1));
        b.Store(outPos, b.Construct(F4, ndc, Constant.Float(0), Constant.Float(1)));
        b.Store(outUv, uv);
        b.Return();

        const string hlsl = """
            struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
            VSOut VSMain(uint id : SV_VertexID) {
                VSOut o; float2 uv = float2((id << 1) & 2, id & 2);
                o.pos = float4(uv * 2 - 1, 0, 1); o.uv = uv; return o; }
            """;
        return new Case("fullscreen-vs", m, "VSMain", hlsl, "vs_6_0");
    }

    /// <summary>A pixel shader with an SSA loop, an if/else joined by a phi, and discard. Blocks are created out of order on purpose.</summary>
    public static Case LoopPixel()
    {
        var m = new IrModule { Name = "loop.hlsl" };
        var (ep, b, f) = Entry(m, "PSMain", ShaderStage.Pixel);
        var uvIn = Stage(m, ep, "in.var.TEXCOORD0", true, F2, 0);
        var target = Stage(m, ep, "out.var.SV_Target", false, F4, 0);
        var entry = f.Entry!;
        var header = f.AddBlock("header");
        var after = f.AddBlock("after");
        var killB = f.AddBlock("kill");
        var exit = f.AddBlock("exit");
        var join = f.AddBlock("join");
        var step = f.AddBlock("step");
        var elseB = f.AddBlock("else");
        var thenB = f.AddBlock("then");
        var body = f.AddBlock("body");
        var cond = f.AddBlock("cond");

        var uv = b.Load(uvIn);
        var ux = b.Extract(uv, F, 0);
        b.Branch(header);

        b.Block = header;
        var iPhi = b.Phi(header, ScalarType.Int, [new(entry, Constant.Int(0)), new(step, Constant.Int(0))]);
        var accPhi = b.Phi(header, F, [new(entry, Constant.Float(0)), new(step, Constant.Float(0))]);
        b.Branch(cond);
        IrBuilder.MarkLoop(header, exit, step);

        b.Block = cond;
        b.CondBranch(b.Compare(Opcode.Lt, iPhi, Constant.Int(4)), body, exit);

        b.Block = body;
        var fi = b.Convert(iPhi, F);
        b.CondBranch(b.Compare(Opcode.Gt, ux, fi), thenB, elseB);
        IrBuilder.MarkSelection(body, join);

        b.Block = thenB;
        var up = b.Binary(Opcode.Add, accPhi, Constant.Float(0.25f));
        b.Branch(join);

        b.Block = elseB;
        var down = b.Binary(Opcode.Sub, accPhi, Constant.Float(0.125f));
        b.Branch(join);

        b.Block = join;
        var merged = b.Phi(join, F, [new(thenB, up), new(elseB, down)]);
        b.Branch(step);

        b.Block = step;
        var next = b.Binary(Opcode.Add, iPhi, Constant.Int(1));
        b.Branch(header);
        iPhi.Operands[1] = next;
        accPhi.Operands[1] = merged;

        b.Block = exit;
        b.CondBranch(b.Compare(Opcode.Lt, accPhi, Constant.Float(0)), killB, after);
        IrBuilder.MarkSelection(exit, after);

        b.Block = killB;
        b.Kill();

        b.Block = after;
        b.Store(target, b.Construct(F4, accPhi, accPhi, accPhi, Constant.Float(1)));
        b.Return();

        const string hlsl = """
            float4 PSMain(float2 uv : TEXCOORD0) : SV_Target {
                float acc = 0;
                for (int i = 0; i < 4; i++) { if (uv.x > i) acc += 0.25; else acc -= 0.125; }
                if (acc < 0) discard;
                return float4(acc, acc, acc, 1); }
            """;
        return new Case("loop-ps", m, "PSMain", hlsl, "ps_6_0");
    }

    /// <summary>A compute shader over a RWStructuredBuffer of {float3, float}: whole-struct load and store plus a member store.</summary>
    public static Case ComputeStructured()
    {
        var m = new IrModule { Name = "particles.hlsl" };
        var particle = new StructType("Particle", [new StructField("pos", F3), new StructField("mass", F)]);
        var buf = Buffer(m, "particles", new ResourceType(ResourceTypeKind.RWStructuredBuffer, particle), AddressSpace.StorageBuffer, 0, 0, particle, BufferLayoutRules.StorageBuffer);
        var (ep, b, _) = Entry(m, "CSMain", ShaderStage.Compute);
        ep.WorkgroupSize = (64, 1, 1);
        var gid = Stage(m, ep, "gl_GlobalInvocationID", true, U3, null, BuiltIn.GlobalInvocationId);
        var id = b.Load(gid);
        var x = b.Extract(id, ScalarType.UInt, 0);
        var ptr = b.AccessChain(buf, particle, x);
        var pv = b.Load(ptr);
        var pos = b.Extract(pv, F3, 0);
        var mass = b.Extract(pv, F, 1);
        var scaled = b.Binary(Opcode.Mul, pos, b.Construct(F3, mass, mass, mass));
        var ins = b.Emit(Opcode.CompositeInsert, particle, pv, scaled);
        ins.Immediates.Add(0);
        b.Store(ptr, ins);
        var massPtr = b.AccessChain(buf, F, x, Constant.Int(1));
        b.Store(massPtr, b.Binary(Opcode.Add, mass, Constant.Float(1)));
        b.Return();

        const string hlsl = """
            struct Particle { float3 pos; float mass; };
            RWStructuredBuffer<Particle> particles : register(u0);
            [numthreads(64, 1, 1)] void CSMain(uint3 id : SV_DispatchThreadID) {
                Particle p = particles[id.x]; p.pos *= p.mass; particles[id.x] = p; particles[id.x].mass = p.mass + 1; }
            """;
        return new Case("compute-structured", m, "CSMain", hlsl, "cs_6_0");
    }

    /// <summary>A cbuffer with a row_major and a column_major float4x3, both read in a vertex shader.</summary>
    public static Case MatrixPackingCase()
    {
        var m = new IrModule { Name = "matrices.hlsl" };
        var mt = HlslType.Matrix(F, 4, 3);
        var cbStruct = new StructType("", [
            new StructField("r", mt) { MatrixPacking = Types.MatrixPacking.RowMajor },
            new StructField("c", mt) { MatrixPacking = Types.MatrixPacking.ColumnMajor },
        ]);
        var cb = Buffer(m, "Mats", new ResourceType(ResourceTypeKind.ConstantBuffer, cbStruct), AddressSpace.Uniform, 0, 0, cbStruct, BufferLayoutRules.ConstantBuffer);
        var (ep, b, _) = Entry(m, "VSMain", ShaderStage.Vertex);
        var pos = Stage(m, ep, "in.var.POSITION", true, F3, 0);
        var outPos = Stage(m, ep, "gl_Position", false, F4, null, BuiltIn.Position);
        var p = b.Load(pos);
        var rm = b.Load(b.AccessChain(cb, mt, Constant.Int(0)));
        var cm = b.Load(b.AccessChain(cb, mt, Constant.Int(1)));
        // float4x3 * float3 -> float4
        var a = b.Emit(Opcode.MatMul, F4, rm, p);
        var c = b.Emit(Opcode.MatMul, F4, cm, p);
        b.Store(outPos, b.Binary(Opcode.Add, a, c));
        b.Return();

        const string hlsl = """
            cbuffer Mats : register(b0) { row_major float4x3 r; column_major float4x3 c; };
            float4 VSMain(float3 p : POSITION) : SV_Position { return mul(r, p) + mul(c, p); }
            """;
        return new Case("matrix-packing", m, "VSMain", hlsl, "vs_6_0");
    }

    /// <summary>Groupshared memory, barriers, atomics on a structured buffer and byte-address loads and stores.</summary>
    public static Case ComputeShared()
    {
        var m = new IrModule { Name = "shared.hlsl" };
        var uintT = ScalarType.UInt;
        var counters = Buffer(m, "counters", new ResourceType(ResourceTypeKind.RWStructuredBuffer, uintT), AddressSpace.StorageBuffer, 0, 0, null, BufferLayoutRules.StorageBuffer);
        BufferLayout.TryCompute(uintT, BufferLayoutRules.StorageBuffer, m.LayoutOptions, out var ul, out _);
        counters.Layout = ul;
        var raw = Buffer(m, "raw", new ResourceType(ResourceTypeKind.RWByteAddressBuffer, null), AddressSpace.StorageBuffer, 0, 1, null, BufferLayoutRules.StorageBuffer);
        var tile = new GlobalVariable("tile", GlobalKind.Workgroup, AddressSpace.Workgroup, HlslType.Array(uintT, 64));
        m.Globals.Add(tile);
        var (ep, b, f) = Entry(m, "CSMain", ShaderStage.Compute);
        ep.WorkgroupSize = (64, 1, 1);
        var gtid = Stage(m, ep, "gl_LocalInvocationIndex", true, uintT, null, BuiltIn.LocalInvocationIndex);
        var idx = b.Load(gtid);
        var slot = b.AccessChain(tile, uintT, idx);
        var word = b.Emit(Opcode.ByteAddressLoad, uintT, raw, b.Binary(Opcode.Mul, idx, Constant.UInt(4)));
        b.Store(slot, word);
        b.Barrier(BarrierFlags.GroupSync | BarrierFlags.GroupMemory);
        var neighbour = b.Binary(Opcode.BitAnd, b.Binary(Opcode.Add, idx, Constant.UInt(1)), Constant.UInt(63));
        var other = b.Load(b.AccessChain(tile, uintT, neighbour));
        var counter = b.AccessChain(counters, uintT, Constant.UInt(0));
        var old = b.Atomic(AtomicOp.Add, counter, other);
        var pair = b.Emit(Opcode.CompositeConstruct, HlslType.Vector(uintT, 2), old, other);
        b.Emit(Opcode.ByteAddressStore, HlslType.Void, raw, b.Binary(Opcode.Mul, idx, Constant.UInt(8)), pair);
        _ = f;
        b.Return();

        const string hlsl = """
            RWStructuredBuffer<uint> counters : register(u0);
            RWByteAddressBuffer raw : register(u1);
            groupshared uint tile[64];
            [numthreads(64, 1, 1)] void CSMain(uint idx : SV_GroupIndex) {
                tile[idx] = raw.Load(idx * 4);
                GroupMemoryBarrierWithGroupSync();
                uint other = tile[(idx + 1) & 63];
                uint old; InterlockedAdd(counters[0], other, old);
                raw.Store2(idx * 8, uint2(old, other)); }
            """;
        return new Case("compute-shared", m, "CSMain", hlsl, "cs_6_0");
    }

    /// <summary>One pixel shader using the texture operations: bias, level, grad, compare, gather, load, size, storage images, math.</summary>
    public static Case TextureOps()
    {
        var m = new IrModule { Name = "textures.hlsl" };
        var tex = Image(m, "tex", new ResourceType(ResourceTypeKind.Texture2D, F4), 0, 0);
        var texArr = Image(m, "texArr", new ResourceType(ResourceTypeKind.Texture2DArray, F4), 0, 1);
        var depth = Image(m, "shadow", new ResourceType(ResourceTypeKind.Texture2D, F), 0, 2);
        var cube = Image(m, "env", new ResourceType(ResourceTypeKind.TextureCube, F4), 0, 3);
        var samp = Image(m, "samp", SamplerType.State, 1, 0);
        var cmp = Image(m, "cmp", SamplerType.Comparison, 1, 1);
        var rw = Image(m, "rwTex", new ResourceType(ResourceTypeKind.RWTexture2D, F4), 2, 0);
        var (ep, b, _) = Entry(m, "PSMain", ShaderStage.Pixel);
        var uvIn = Stage(m, ep, "in.var.TEXCOORD0", true, F2, 0);
        var idIn = Stage(m, ep, "in.var.ID", true, ScalarType.UInt, 1);
        var target = Stage(m, ep, "out.var.SV_Target", false, F4, 0);
        var depthOut = Stage(m, ep, "gl_FragDepth", false, F, null, BuiltIn.FragDepth);
        var uv = b.Load(uvIn);
        var id = b.Load(idIn);
        var sum = b.Emit(Opcode.TextureSample, F4, tex, samp, uv);
        sum.Immediates.AddRange([1, -1]);
        var bias = b.Emit(Opcode.TextureSampleBias, F4, tex, samp, uv, Constant.Float(0.5f));
        var lvl = b.Emit(Opcode.TextureSampleLevel, F4, tex, samp, uv, Constant.Float(2));
        var dx = b.CallIntrinsic(Intrinsic.Ddx, F2, uv);
        var dy = b.CallIntrinsic(Intrinsic.Ddy, F2, uv);
        var grad = b.Emit(Opcode.TextureSampleGrad, F4, tex, samp, uv, dx, dy);
        var uv3 = b.Construct(HlslType.Vector(F, 3), uv, Constant.Float(1));
        var arr = b.Emit(Opcode.TextureSample, F4, texArr, samp, uv3);
        var sh = b.Emit(Opcode.TextureSampleCompareLevelZero, F, depth, cmp, uv, Constant.Float(0.5f));
        var shc = b.Emit(Opcode.TextureSampleCompare, F, depth, cmp, uv, Constant.Float(0.25f));
        var gather = b.Emit(Opcode.TextureGather, F4, tex, samp, uv);
        gather.Immediates.Add(1);
        var dir = b.Construct(HlslType.Vector(F, 3), uv, Constant.Float(1));
        var env = b.Emit(Opcode.TextureSample, F4, cube, samp, dir);
        var coord = b.Construct(HlslType.Vector(ScalarType.Int, 2), b.Convert(id, ScalarType.Int), Constant.Int(3));
        var fetch = b.Emit(Opcode.TextureLoad, F4, tex, coord, Constant.Int(0));
        var size = b.Emit(Opcode.TextureQuerySize, HlslType.Vector(ScalarType.UInt, 2), tex, Constant.UInt(0));
        var rd = b.Emit(Opcode.TextureLoad, F4, rw, coord);
        b.Emit(Opcode.TextureStore, HlslType.Void, rw, coord, b.Binary(Opcode.Add, rd, fetch));
        var sf = b.Convert(size, HlslType.Vector(F, 2));
        var all = b.Binary(Opcode.Add, sum, bias);
        all = b.Binary(Opcode.Add, all, lvl);
        all = b.Binary(Opcode.Add, all, grad);
        all = b.Binary(Opcode.Add, all, arr);
        all = b.Binary(Opcode.Add, all, gather);
        all = b.Binary(Opcode.Add, all, env);
        var scalarMix = b.Binary(Opcode.Add, sh, shc);
        var sat = b.CallIntrinsic(Intrinsic.Saturate, F4, all);
        var lerped = b.CallIntrinsic(Intrinsic.Lerp, F4, sat, all, Vec(F4, 0.5f, 0.5f, 0.5f, 0.5f));
        var fw = b.CallIntrinsic(Intrinsic.Fwidth, F2, sf);
        b.Store(target, b.Binary(Opcode.Add, lerped, b.Construct(F4, fw, scalarMix, scalarMix)));
        b.Store(depthOut, scalarMix);
        b.Return();
        return new Case("texture-ops", m, "PSMain", "", "ps_6_0");
    }

    /// <summary>Allocas, a helper with an out parameter, a switch, single-component vectors and single-row/column matrices.</summary>
    public static Case ControlFlowCalls()
    {
        var m = new IrModule { Name = "flow.hlsl" };
        var helper = new IrFunction("scale", F);
        m.Functions.Add(helper);
        var hx = helper.AddParameter(F, "x");
        var hy = helper.AddParameter(new PointerType(AddressSpace.Function, F), "y");
        var hb = new IrBuilder(helper) { Block = helper.AddBlock("entry") };
        hb.Store(hy, hb.Binary(Opcode.Mul, hx, Constant.Float(2)));
        hb.Return(hb.Binary(Opcode.Add, hx, Constant.Float(1)));

        var (ep, b, f) = Entry(m, "PSMain", ShaderStage.Pixel);
        var uvIn = Stage(m, ep, "in.var.TEXCOORD0", true, F2, 0);
        var target = Stage(m, ep, "out.var.SV_Target", false, F4, 0);
        var entry = f.Entry!;
        var sw = f.AddBlock("switch");
        var c0 = f.AddBlock("case0");
        var c1 = f.AddBlock("case1");
        var def = f.AddBlock("default");
        var merge = f.AddBlock("merge");

        var acc = b.Alloca(F, "acc");
        var tmp = b.Alloca(F, "tmp");
        var uv = b.Load(uvIn);
        var ux = b.Extract(uv, F, 0);
        var r = b.Call(helper, ux, tmp);
        b.Store(acc, b.Binary(Opcode.Add, r, b.Load(tmp)));
        var sel = b.Convert(b.Binary(Opcode.Mul, ux, Constant.Float(3)), ScalarType.Int);
        b.Branch(sw);
        b.Block = sw;
        b.Switch(sel, def, [new(0, c0), new(1, c1)]);
        IrBuilder.MarkSelection(sw, merge);
        b.Block = c0;
        b.Store(acc, b.Binary(Opcode.Add, b.Load(acc), Constant.Float(1)));
        b.Branch(merge);
        b.Block = c1;
        b.Store(acc, b.Binary(Opcode.Sub, b.Load(acc), Constant.Float(1)));
        b.Branch(merge);
        b.Block = def;
        b.Store(acc, b.Binary(Opcode.Mul, b.Load(acc), Constant.Float(0.5f)));
        b.Branch(merge);
        b.Block = merge;
        _ = entry;

        var f1 = HlslType.Vector(F, 1);
        var one = b.Construct(f1, b.Load(acc));
        var splat = b.Shuffle(one, one, F3, 0, 0, 0);
        var back = b.Extract(one, F, 0);
        var m13 = HlslType.Matrix(F, 1, 3);
        var m31 = HlslType.Matrix(F, 3, 1);
        var row = b.Construct(m13, splat);
        var rowOut = b.Extract(row, F3, 0);
        var col = b.Construct(m31, back, back, back);
        var colElem = b.Extract(col, F, 1);
        b.Store(target, b.Construct(F4, b.Binary(Opcode.Add, rowOut, splat), b.Binary(Opcode.Add, colElem, back)));
        b.Return();
        return new Case("control-flow-calls", m, "PSMain", "", "ps_6_0");
    }

    /// <summary>A compute shader that runs most arithmetic, conversion and math intrinsics and stores the results so nothing is dead.</summary>
    public static Case IntrinsicSweep()
    {
        var m = new IrModule { Name = "sweep.hlsl" };
        var inT = new ResourceType(ResourceTypeKind.StructuredBuffer, F4);
        var outT = new ResourceType(ResourceTypeKind.RWStructuredBuffer, F4);
        var inBuf = Buffer(m, "input", inT, AddressSpace.StorageBuffer, 0, 0, null, BufferLayoutRules.StorageBuffer);
        var outBuf = Buffer(m, "output", outT, AddressSpace.StorageBuffer, 0, 1, null, BufferLayoutRules.StorageBuffer);
        BufferLayout.TryCompute(F4, BufferLayoutRules.StorageBuffer, m.LayoutOptions, out var l4, out _);
        inBuf.Layout = l4;
        outBuf.Layout = l4;
        var (ep, b, _) = Entry(m, "CSMain", ShaderStage.Compute);
        ep.WorkgroupSize = (8, 8, 1);
        var gid = Stage(m, ep, "gl_GlobalInvocationID", true, U3, null, BuiltIn.GlobalInvocationId);
        var x = b.Extract(b.Load(gid), ScalarType.UInt, 0);
        var v4 = b.Load(b.AccessChain(inBuf, F4, x));
        var v3 = b.Shuffle(v4, v4, F3, 0, 1, 2);
        var s = b.Extract(v4, F, 0);
        var iv = b.Convert(v3, HlslType.Vector(ScalarType.Int, 3));
        var uvv = b.Convert(v3, U3);
        var si = b.Convert(s, ScalarType.Int);
        var su = b.Convert(s, ScalarType.UInt);
        var floats = new List<IrValue>();
        IrValue Call(Intrinsic i, HlslType t, params IrValue[] a) => b.CallIntrinsic(i, t, a);
        foreach (var i in new[] { Intrinsic.Sin, Intrinsic.Cos, Intrinsic.Tan, Intrinsic.Asin, Intrinsic.Acos, Intrinsic.Atan, Intrinsic.Sinh, Intrinsic.Cosh, Intrinsic.Tanh,
            Intrinsic.Exp, Intrinsic.Exp2, Intrinsic.Log, Intrinsic.Log2, Intrinsic.Sqrt, Intrinsic.Rsqrt, Intrinsic.Abs, Intrinsic.Floor, Intrinsic.Ceil, Intrinsic.Frac,
            Intrinsic.Round, Intrinsic.Trunc, Intrinsic.Saturate, Intrinsic.Rcp, Intrinsic.Radians, Intrinsic.Degrees, Intrinsic.Normalize, Intrinsic.Ddx, Intrinsic.Fwidth })
        {
            if (i is Intrinsic.Ddx or Intrinsic.Fwidth)
            {
                continue; // derivatives are not valid in compute
            }

            floats.Add(Call(i, F3, v3));
        }

        floats.Add(Call(Intrinsic.Pow, F3, v3, v3));
        floats.Add(Call(Intrinsic.Atan2, F3, v3, v3));
        floats.Add(Call(Intrinsic.Min, F3, v3, v3));
        floats.Add(Call(Intrinsic.Max, F3, v3, Vec(F3, 1, 1, 1)));
        floats.Add(Call(Intrinsic.Clamp, F3, v3, Vec(F3, 0, 0, 0), Vec(F3, 1, 1, 1)));
        floats.Add(Call(Intrinsic.Lerp, F3, v3, v3, b.Construct(F3, s)));
        floats.Add(Call(Intrinsic.SmoothStep, F3, Vec(F3, 0, 0, 0), Vec(F3, 1, 1, 1), v3));
        floats.Add(Call(Intrinsic.Step, F3, v3, v3));
        floats.Add(Call(Intrinsic.Fma, F3, v3, v3, v3));
        floats.Add(Call(Intrinsic.Cross, F3, v3, v3));
        floats.Add(Call(Intrinsic.Reflect, F3, v3, v3));
        floats.Add(Call(Intrinsic.Refract, F3, v3, v3, Constant.Float(0.5f)));
        floats.Add(Call(Intrinsic.Ldexp, F3, v3, v3));
        floats.Add(b.Construct(F3, Call(Intrinsic.Length, F, v3), Call(Intrinsic.Distance, F, v3, v3), Call(Intrinsic.Dot, F, v3, v3)));
        floats.Add(b.Construct(F3, Call(Intrinsic.Dot, F, b.Construct(F2, s, s), b.Construct(F2, s, s)), Call(Intrinsic.Dot, F, s, s), Call(Intrinsic.Determinant, F, b.Construct(HlslType.Matrix(F, 3, 3), v3, v3, v3))));
        floats.Add(Call(Intrinsic.FaceForward, F3, v3, v3, v3));

        // modf / frexp / sincos through pointers
        var ip = b.Alloca(F3, "ip");
        var ex = b.Alloca(F3, "ex");
        var sn = b.Alloca(F3, "sn");
        var cs = b.Alloca(F3, "cs");
        floats.Add(Call(Intrinsic.Modf, F3, v3, ip));
        floats.Add(b.Load(ip));
        floats.Add(Call(Intrinsic.Frexp, F3, v3, ex));
        floats.Add(b.Load(ex));
        Call(Intrinsic.SinCos, HlslType.Void, v3, sn, cs);
        floats.Add(b.Load(sn));
        floats.Add(b.Load(cs));

        // integer and bit operations
        var ints = new List<IrValue>
        {
            Call(Intrinsic.CountBits, U3, uvv),
            Call(Intrinsic.ReverseBits, U3, uvv),
            Call(Intrinsic.FirstBitHigh, U3, uvv),
            Call(Intrinsic.FirstBitLow, U3, uvv),
            Call(Intrinsic.FirstBitHigh, HlslType.Vector(ScalarType.Int, 3), iv),
            Call(Intrinsic.Abs, HlslType.Vector(ScalarType.Int, 3), iv),
            Call(Intrinsic.Min, U3, uvv, uvv),
            Call(Intrinsic.Clamp, HlslType.Vector(ScalarType.Int, 3), iv, iv, iv),
            b.Binary(Opcode.Rem, iv, iv),
            b.Binary(Opcode.Div, uvv, uvv),
            b.Binary(Opcode.Shl, uvv, uvv),
            b.Binary(Opcode.Shr, iv, iv),
            b.Binary(Opcode.Shr, uvv, b.Construct(U3, su)),
            b.Binary(Opcode.BitXor, uvv, uvv),
            b.Unary(Opcode.BitNot, uvv),
            b.Unary(Opcode.Neg, iv),
            Call(Intrinsic.Dot, ScalarType.Int, iv, iv),
            Call(Intrinsic.Sign, HlslType.Vector(ScalarType.Int, 3), v3),
            Call(Intrinsic.Sign, HlslType.Vector(ScalarType.Int, 3), iv),
            b.Emit(Opcode.Select, F3, b.Compare(Opcode.Lt, v3, Vec(F3, 0, 0, 0)), v3, Vec(F3, 1, 1, 1)),
            b.Convert(b.Compare(Opcode.Ne, v3, v3), F3),
            b.Convert(Call(Intrinsic.IsNan, HlslType.Vector(ScalarType.Bool, 3), v3), U3),
            b.Convert(Call(Intrinsic.IsInf, HlslType.Vector(ScalarType.Bool, 3), v3), U3),
            b.Convert(Call(Intrinsic.IsFinite, HlslType.Vector(ScalarType.Bool, 3), v3), U3),
            b.Convert(b.Construct(HlslType.Vector(ScalarType.Bool, 2), Call(Intrinsic.All, ScalarType.Bool, v3), Call(Intrinsic.Any, ScalarType.Bool, iv)), HlslType.Vector(ScalarType.UInt, 2)),
            b.Convert(b.Compare(Opcode.Ge, uvv, uvv), U3),
            b.Convert(b.Compare(Opcode.Le, iv, iv), U3),
            b.Bitcast(v3, U3),
            b.Convert(b.Bitcast(su, ScalarType.Float), ScalarType.UInt),
            Call(Intrinsic.PackHalf, U3, v3),
            Call(Intrinsic.UnpackHalf, F3, uvv),
            b.Convert(si, ScalarType.UInt),
        };

        // matrices
        var m33 = HlslType.Matrix(F, 3, 3);
        var m34 = HlslType.Matrix(F, 3, 4);
        var mA = b.Construct(m33, v3, v3, v3);
        var mB = b.Construct(m34, v4, v4, v4);
        var prod = b.Emit(Opcode.MatMul, m34, mA, mB);
        var vm = b.Emit(Opcode.MatMul, F4, v3, prod);
        var mv = b.Emit(Opcode.MatMul, F3, mA, v3);
        var tr = b.Emit(Opcode.Transpose, HlslType.Matrix(F, 4, 3), prod);
        var outer = b.Emit(Opcode.OuterProduct, m34, v3, v4);
        var comp = b.Binary(Opcode.Mul, b.Binary(Opcode.Add, prod, outer), b.Unary(Opcode.Neg, prod));
        floats.Add(b.Extract(comp, F4, 2));
        floats.Add(b.Extract(tr, F3, 1));
        floats.Add(mv);
        floats.Add(b.Extract(b.Convert(b.Convert(mA, HlslType.Matrix(ScalarType.Double, 3, 3)), m33), F3, 1));

        IrValue total = vm;
        foreach (var fv in floats)
        {
            var v = fv.Type.Equals(F3) ? b.Construct(F4, fv, Constant.Float(0)) : fv.Type.Equals(F4) ? fv : null;
            if (v is not null)
            {
                total = b.Binary(Opcode.Add, total, v);
            }
        }

        foreach (var iv2 in ints)
        {
            IrValue? f3 = iv2.Type.ScalarElement == F ? iv2 : iv2.Type is VectorType { Count: 3 } ? b.Convert(iv2, F3) : null;
            if (f3 is not null)
            {
                total = b.Binary(Opcode.Add, total, b.Construct(F4, f3, Constant.Float(0)));
            }
        }

        b.Store(b.AccessChain(outBuf, F4, x), total);
        b.Return();
        return new Case("intrinsic-sweep", m, "CSMain", "", "cs_6_0");
    }

    /// <summary>Pixel built-ins and interpolation modes, a resource array indexed dynamically, and the sample mask.</summary>
    public static Case PixelInterface()
    {
        var m = new IrModule { Name = "interface.hlsl" };
        var texT = new ResourceType(ResourceTypeKind.Texture2D, F4);
        var texs = new GlobalVariable("texs", GlobalKind.Resource, AddressSpace.UniformConstant, texT) { Set = 0, Binding = 0, ResourceCount = 4 };
        m.Globals.Add(texs);
        var samp = Image(m, "samp", SamplerType.State, 0, 4);
        var (ep, b, _) = Entry(m, "PSMain", ShaderStage.Pixel);
        var frag = Stage(m, ep, "gl_FragCoord", true, F4, null, BuiltIn.FragCoord);
        var front = Stage(m, ep, "gl_FrontFacing", true, ScalarType.Bool, null, BuiltIn.FrontFacing);
        var sampleIdx = Stage(m, ep, "gl_SampleID", true, ScalarType.UInt, null, BuiltIn.SampleIndex);
        var flat = Stage(m, ep, "in.var.INDEX", true, ScalarType.UInt, 0);
        var npersp = Stage(m, ep, "in.var.NP", true, F2, 1);
        npersp.Interpolation = Interpolation.NoPerspective;
        var cent = Stage(m, ep, "in.var.CENT", true, F2, 2);
        cent.Interpolation = Interpolation.Centroid;
        var smp = Stage(m, ep, "in.var.SMP", true, F2, 3);
        smp.Interpolation = Interpolation.Sample;
        var target = Stage(m, ep, "out.var.SV_Target", false, F4, 0);
        var mask = Stage(m, ep, "gl_SampleMask", false, ScalarType.UInt, null, BuiltIn.SampleMask);
        var index = b.Load(flat);
        var tp = b.AccessChain(texs, texT, b.Binary(Opcode.BitAnd, index, Constant.UInt(3)));
        var uv = b.Binary(Opcode.Add, b.Binary(Opcode.Add, b.Load(npersp), b.Load(cent)), b.Load(smp));
        var color = b.Emit(Opcode.TextureSample, F4, tp, samp, uv);
        var fc = b.Load(frag);
        var facing = b.Emit(Opcode.Select, F4, b.Load(front), color, fc);
        b.Store(target, facing);
        b.Store(mask, b.Binary(Opcode.Shl, Constant.UInt(1), b.Load(sampleIdx)));
        b.Return();
        return new Case("pixel-interface", m, "PSMain", "", "ps_6_0");
    }

    public static IEnumerable<Case> All()
    {
        yield return SpriteVertex();
        yield return SpritePixel();
        yield return FullscreenTriangle();
        yield return LoopPixel();
        yield return ComputeStructured();
        yield return MatrixPackingCase();
        yield return ComputeShared();
        yield return TextureOps();
        yield return ControlFlowCalls();
        yield return IntrinsicSweep();
        yield return PixelInterface();
    }
}
