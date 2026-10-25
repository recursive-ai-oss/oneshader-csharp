using System;
using System.Collections.Generic;
using OneShader.Diagnostics;
using OneShader.Types;

namespace OneShader.IR;

/// <summary>Instruction opcodes. Operand conventions are listed in docs/ir.md.</summary>
public enum Opcode
{
    // Memory
    /// <summary>Allocates a function-local variable; result is a Function pointer. Only in the entry block.</summary>
    Alloca,

    /// <summary>Loads the value a pointer addresses.</summary>
    Load,

    /// <summary>Stores a value through a pointer; no result.</summary>
    Store,

    /// <summary>Computes the address of a member or element: operands are the base pointer, then indices.</summary>
    AccessChain,

    /// <summary>Element count of the runtime array at the end of a storage buffer.</summary>
    ArrayLength,

    /// <summary>Atomic read-modify-write; <see cref="Instruction.Immediates"/>[0] is an <see cref="AtomicOp"/>; returns the old value.</summary>
    AtomicRmw,

    /// <summary>Atomic compare-exchange: pointer, compare, value; returns the old value.</summary>
    AtomicCompareExchange,

    /// <summary>Execution and/or memory barrier; <see cref="Instruction.Immediates"/>[0] is a <see cref="BarrierFlags"/> value.</summary>
    Barrier,

    // Arithmetic (component-wise on vectors and matrices; signedness comes from the operand type)
    /// <summary>Addition.</summary>
    Add,

    /// <summary>Subtraction.</summary>
    Sub,

    /// <summary>Multiplication (component-wise; matrix products use <see cref="MatMul"/>).</summary>
    Mul,

    /// <summary>Division.</summary>
    Div,

    /// <summary>Remainder (<c>fmod</c> semantics for floats; sign of the dividend).</summary>
    Rem,

    /// <summary>Negation.</summary>
    Neg,

    /// <summary>Bitwise and.</summary>
    BitAnd,

    /// <summary>Bitwise or.</summary>
    BitOr,

    /// <summary>Bitwise xor.</summary>
    BitXor,

    /// <summary>Bitwise not.</summary>
    BitNot,

    /// <summary>Shift left; the shift count is masked to the operand width as HLSL does.</summary>
    Shl,

    /// <summary>Shift right (arithmetic for signed types, logical for unsigned).</summary>
    Shr,

    /// <summary>Logical and on bools (both operands already evaluated; short-circuiting is control flow).</summary>
    LogicalAnd,

    /// <summary>Logical or on bools.</summary>
    LogicalOr,

    /// <summary>Logical not on bools.</summary>
    LogicalNot,

    /// <summary>Equal; result is bool or a bool vector.</summary>
    Eq,

    /// <summary>Not equal.</summary>
    Ne,

    /// <summary>Less than.</summary>
    Lt,

    /// <summary>Less than or equal.</summary>
    Le,

    /// <summary>Greater than.</summary>
    Gt,

    /// <summary>Greater than or equal.</summary>
    Ge,

    /// <summary>Select: condition, value if true, value if false (condition may be a bool vector).</summary>
    Select,

    // Conversion
    /// <summary>Numeric conversion to the result type (value-preserving where possible, as HLSL casts).</summary>
    Convert,

    /// <summary>Reinterprets the bits as another type of the same size.</summary>
    Bitcast,

    // Composite
    /// <summary>Builds a vector, matrix (from rows), array or struct from operands; one scalar operand splats to a vector.</summary>
    CompositeConstruct,

    /// <summary>Extracts by literal indices in <see cref="Instruction.Immediates"/>.</summary>
    CompositeExtract,

    /// <summary>Replaces the element at the literal indices: composite, new value.</summary>
    CompositeInsert,

    /// <summary>Shuffles two vectors: operands a, b; <see cref="Instruction.Immediates"/> are component indices into the concatenation (swizzles use b = a).</summary>
    VectorShuffle,

    /// <summary>Extracts a vector component at a dynamic index: vector, index.</summary>
    VectorExtractDynamic,

    /// <summary>Inserts a component at a dynamic index: vector, value, index.</summary>
    VectorInsertDynamic,

    // Matrix
    /// <summary>HLSL <c>mul</c> for matrix*matrix, matrix*vector and vector*matrix (not scalar products).</summary>
    MatMul,

    /// <summary>Matrix transpose.</summary>
    Transpose,

    /// <summary>Outer product of two vectors.</summary>
    OuterProduct,

    // Calls
    /// <summary>Calls a user function (<see cref="Instruction.Callee"/>).</summary>
    Call,

    /// <summary>Calls a built-in function (<see cref="Instruction.Intrinsic"/>).</summary>
    CallIntrinsic,

    // Textures and buffers (operand conventions in docs/ir.md)
    /// <summary>Implicit-LOD sample: texture, sampler, coordinate.</summary>
    TextureSample,

    /// <summary>Sample with LOD bias: texture, sampler, coordinate, bias.</summary>
    TextureSampleBias,

    /// <summary>Sample at an explicit level: texture, sampler, coordinate, level.</summary>
    TextureSampleLevel,

    /// <summary>Sample with gradients: texture, sampler, coordinate, ddx, ddy.</summary>
    TextureSampleGrad,

    /// <summary>Comparison sample: texture, comparison sampler, coordinate, reference.</summary>
    TextureSampleCompare,

    /// <summary>Comparison sample at level zero: same operands as <see cref="TextureSampleCompare"/>.</summary>
    TextureSampleCompareLevelZero,

    /// <summary>Gather of one component from four texels: texture, sampler, coordinate; Immediates[0] is the component.</summary>
    TextureGather,

    /// <summary>Comparison gather: four texels' depth comparisons as a float4: texture, comparison sampler, coordinate, reference; Immediates are the constant texel offset.</summary>
    TextureGatherCompare,

    /// <summary>Fetch of one texel: texture, integer coordinate, optional level or sample index.</summary>
    TextureLoad,

    /// <summary>Write of one texel: texture, integer coordinate, value.</summary>
    TextureStore,

    /// <summary>Dimensions: texture, optional level; result is a uint vector (or uint for 1D).</summary>
    TextureQuerySize,

    /// <summary>Mip level count: texture.</summary>
    TextureQueryLevels,

    /// <summary>Sample count of a multisampled texture: texture.</summary>
    TextureQuerySamples,

    /// <summary>Level of detail: texture, sampler, coordinate; result is float2 (clamped, unclamped).</summary>
    TextureQueryLod,

    /// <summary>Loads 1 to 4 32-bit words: byte-address buffer, byte offset (uint).</summary>
    ByteAddressLoad,

    /// <summary>Stores 1 to 4 32-bit words: byte-address buffer, byte offset, value.</summary>
    ByteAddressStore,

    // SSA and control flow
    /// <summary>Phi: operands are the incoming values, <see cref="Instruction.Targets"/> the matching predecessors. First in a block.</summary>
    Phi,

    /// <summary>Unconditional branch to Targets[0].</summary>
    Branch,

    /// <summary>Conditional branch on a bool: Targets[0] if true, Targets[1] otherwise.</summary>
    CondBranch,

    /// <summary>Multi-way branch: selector (int/uint), Targets[0] default then one target per case; case values in Immediates.</summary>
    Switch,

    /// <summary>Returns, with a value when the function is not void.</summary>
    Return,

    /// <summary>Pixel discard (<c>discard</c>/<c>clip</c>); ends the invocation.</summary>
    Kill,

    /// <summary>Marks code that cannot be reached.</summary>
    Unreachable,
}

/// <summary>Operations for <see cref="Opcode.AtomicRmw"/>.</summary>
public enum AtomicOp
{
    /// <summary>Add.</summary>
    Add,

    /// <summary>Minimum (signedness from the operand type).</summary>
    Min,

    /// <summary>Maximum.</summary>
    Max,

    /// <summary>Bitwise and.</summary>
    And,

    /// <summary>Bitwise or.</summary>
    Or,

    /// <summary>Bitwise xor.</summary>
    Xor,

    /// <summary>Exchange.</summary>
    Exchange,
}

/// <summary>What a <see cref="Opcode.Barrier"/> synchronizes.</summary>
[Flags]
public enum BarrierFlags
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>All threads of the group wait for each other (<c>...WithGroupSync</c>).</summary>
    GroupSync = 1,

    /// <summary>Orders groupshared memory accesses.</summary>
    GroupMemory = 2,

    /// <summary>Orders device (buffer and image) memory accesses.</summary>
    DeviceMemory = 4,
}

/// <summary>Built-in functions with an <see cref="Opcode.CallIntrinsic"/>.</summary>
public enum Intrinsic
{
    /// <summary>abs.</summary>
    Abs, /// <summary>acos.</summary>
    Acos, /// <summary>all.</summary>
    All, /// <summary>any.</summary>
    Any, /// <summary>asin.</summary>
    Asin, /// <summary>atan.</summary>
    Atan, /// <summary>atan2(y, x).</summary>
    Atan2, /// <summary>ceil.</summary>
    Ceil, /// <summary>clamp(x, lo, hi).</summary>
    Clamp, /// <summary>cos.</summary>
    Cos, /// <summary>cosh.</summary>
    Cosh, /// <summary>countbits.</summary>
    CountBits, /// <summary>cross.</summary>
    Cross, /// <summary>degrees.</summary>
    Degrees, /// <summary>determinant.</summary>
    Determinant, /// <summary>distance.</summary>
    Distance, /// <summary>dot.</summary>
    Dot, /// <summary>exp.</summary>
    Exp, /// <summary>exp2.</summary>
    Exp2, /// <summary>faceforward(n, i, ng).</summary>
    FaceForward, /// <summary>firstbithigh.</summary>
    FirstBitHigh, /// <summary>firstbitlow.</summary>
    FirstBitLow, /// <summary>floor.</summary>
    Floor, /// <summary>fma.</summary>
    Fma, /// <summary>frac.</summary>
    Frac, /// <summary>frexp(x, out exp): operands x, pointer to exp.</summary>
    Frexp, /// <summary>isfinite.</summary>
    IsFinite, /// <summary>isinf.</summary>
    IsInf, /// <summary>isnan.</summary>
    IsNan, /// <summary>ldexp.</summary>
    Ldexp, /// <summary>length.</summary>
    Length, /// <summary>lerp (mix).</summary>
    Lerp, /// <summary>log.</summary>
    Log, /// <summary>log2.</summary>
    Log2, /// <summary>log10.</summary>
    Log10, /// <summary>max.</summary>
    Max, /// <summary>min.</summary>
    Min, /// <summary>modf(x, out ip): operands x, pointer to ip.</summary>
    Modf, /// <summary>normalize.</summary>
    Normalize, /// <summary>pow.</summary>
    Pow, /// <summary>radians.</summary>
    Radians, /// <summary>rcp (reciprocal).</summary>
    Rcp, /// <summary>reflect.</summary>
    Reflect, /// <summary>refract(i, n, eta).</summary>
    Refract, /// <summary>reversebits.</summary>
    ReverseBits, /// <summary>round (half to even).</summary>
    Round, /// <summary>rsqrt.</summary>
    Rsqrt, /// <summary>saturate.</summary>
    Saturate, /// <summary>sign.</summary>
    Sign, /// <summary>sin.</summary>
    Sin, /// <summary>sincos(x, out s, out c): operands x, pointers.</summary>
    SinCos, /// <summary>sinh.</summary>
    Sinh, /// <summary>smoothstep(lo, hi, x).</summary>
    SmoothStep, /// <summary>sqrt.</summary>
    Sqrt, /// <summary>step(edge, x).</summary>
    Step, /// <summary>tan.</summary>
    Tan, /// <summary>tanh.</summary>
    Tanh, /// <summary>trunc.</summary>
    Trunc, /// <summary>f32tof16.</summary>
    PackHalf, /// <summary>f16tof32.</summary>
    UnpackHalf, /// <summary>ddx (coarse or fine, driver's choice).</summary>
    Ddx, /// <summary>ddx_coarse.</summary>
    DdxCoarse, /// <summary>ddx_fine.</summary>
    DdxFine, /// <summary>ddy.</summary>
    Ddy, /// <summary>ddy_coarse.</summary>
    DdyCoarse, /// <summary>ddy_fine.</summary>
    DdyFine, /// <summary>fwidth.</summary>
    Fwidth, /// <summary>WaveGetLaneCount.</summary>
    WaveGetLaneCount, /// <summary>WaveGetLaneIndex.</summary>
    WaveGetLaneIndex, /// <summary>WaveIsFirstLane.</summary>
    WaveIsFirstLane, /// <summary>WaveActiveAnyTrue.</summary>
    WaveActiveAnyTrue, /// <summary>WaveActiveAllTrue.</summary>
    WaveActiveAllTrue, /// <summary>WaveActiveBallot.</summary>
    WaveActiveBallot, /// <summary>WaveReadLaneAt.</summary>
    WaveReadLaneAt, /// <summary>WaveReadLaneFirst.</summary>
    WaveReadLaneFirst, /// <summary>WaveActiveSum.</summary>
    WaveActiveSum, /// <summary>WaveActiveProduct.</summary>
    WaveActiveProduct, /// <summary>WaveActiveMin.</summary>
    WaveActiveMin, /// <summary>WaveActiveMax.</summary>
    WaveActiveMax, /// <summary>WaveActiveBitAnd.</summary>
    WaveActiveBitAnd, /// <summary>WaveActiveBitOr.</summary>
    WaveActiveBitOr, /// <summary>WaveActiveBitXor.</summary>
    WaveActiveBitXor, /// <summary>WaveActiveCountBits.</summary>
    WaveActiveCountBits, /// <summary>WavePrefixSum.</summary>
    WavePrefixSum, /// <summary>WavePrefixProduct.</summary>
    WavePrefixProduct, /// <summary>WavePrefixCountBits.</summary>
    WavePrefixCountBits, /// <summary>QuadReadAcrossX.</summary>
    QuadReadAcrossX, /// <summary>QuadReadAcrossY.</summary>
    QuadReadAcrossY, /// <summary>QuadReadAcrossDiagonal.</summary>
    QuadReadAcrossDiagonal, /// <summary>QuadReadLaneAt.</summary>
    QuadReadLaneAt,
}

/// <summary>The role of a block in structured control flow.</summary>
public enum StructureKind
{
    /// <summary>Not a header.</summary>
    None,

    /// <summary>Header of an if/else or switch: ends in <see cref="Opcode.CondBranch"/> or <see cref="Opcode.Switch"/>; <see cref="BasicBlock.MergeBlock"/> is where the arms rejoin.</summary>
    Selection,

    /// <summary>Header of a loop: ends in <see cref="Opcode.Branch"/> into the loop; has a merge (exit) and a continue block.</summary>
    Loop,
}

/// <summary>Source-level hints on a structured construct (<c>[unroll]</c>, <c>[loop]</c>, <c>[flatten]</c>, <c>[branch]</c>).</summary>
public enum ControlHint
{
    /// <summary>No hint.</summary>
    None,

    /// <summary><c>[unroll]</c>.</summary>
    Unroll,

    /// <summary><c>[loop]</c>.</summary>
    DontUnroll,

    /// <summary><c>[flatten]</c>.</summary>
    Flatten,

    /// <summary><c>[branch]</c>.</summary>
    Branch,
}

/// <summary>
/// One instruction. One class serves every opcode so passes can rewrite operands and opcodes in
/// place; the typed helpers on <see cref="IrBuilder"/> build well-formed instances.
/// </summary>
public sealed class Instruction : IrValue
{
    internal Instruction(Opcode op, HlslType type, IReadOnlyList<IrValue> operands)
        : base(type)
    {
        Op = op;
        Operands = operands is List<IrValue> l ? l : new List<IrValue>(operands);
    }

    /// <summary>The operation. Mutable so passes can rewrite in place.</summary>
    public Opcode Op { get; set; }

    /// <summary>The value operands.</summary>
    public List<IrValue> Operands { get; }

    /// <summary>Literal operands: extract indices, shuffle components, atomic op, barrier flags, switch case values, gather component, texel offsets.</summary>
    public List<int> Immediates { get; } = [];

    /// <summary>Block operands: branch targets, or phi predecessors.</summary>
    public List<BasicBlock> Targets { get; } = [];

    /// <summary>The called function for <see cref="Opcode.Call"/>.</summary>
    public IrFunction? Callee { get; set; }

    /// <summary>The built-in for <see cref="Opcode.CallIntrinsic"/>.</summary>
    public Intrinsic Intrinsic { get; set; }

    /// <summary>True when the result must be computed exactly as written (<c>precise</c>): no reassociation, fusing or algebraic rewriting.</summary>
    public bool Precise { get; set; }

    /// <summary>Source position for diagnostics and debug names.</summary>
    public SourceSpan Span { get; set; }

    /// <summary>The block containing this instruction, or null while detached.</summary>
    public BasicBlock? Parent { get; internal set; }

    /// <summary>True when the instruction ends a block.</summary>
    public bool IsTerminator => Op is Opcode.Branch or Opcode.CondBranch or Opcode.Switch or Opcode.Return or Opcode.Kill or Opcode.Unreachable;

    /// <summary>True when the instruction yields no value.</summary>
    public bool IsVoid => Type.IsVoid;

    /// <summary>True when removing the instruction could change behaviour even if its result is unused.</summary>
    public bool HasSideEffects => Op switch
    {
        Opcode.Store or Opcode.AtomicRmw or Opcode.AtomicCompareExchange or Opcode.Barrier or Opcode.Call
            or Opcode.TextureStore or Opcode.ByteAddressStore => true,
        Opcode.CallIntrinsic => Intrinsic is Intrinsic.Frexp or Intrinsic.Modf or Intrinsic.SinCos
            or >= Intrinsic.WaveGetLaneCount and <= Intrinsic.QuadReadLaneAt,
        _ => IsTerminator,
    };
}

/// <summary>A basic block: instructions ending in exactly one terminator, plus structured-control-flow markers.</summary>
public sealed class BasicBlock
{
    internal BasicBlock(IrFunction parent, string? name)
    {
        Parent = parent;
        Name = name;
    }

    /// <summary>The owning function.</summary>
    public IrFunction Parent { get; }

    /// <summary>A hint such as <c>for.body</c>; shown in dumps, never significant.</summary>
    public string? Name { get; set; }

    /// <summary>The instructions in order; the last one must be the terminator.</summary>
    public List<Instruction> Instructions { get; } = [];

    /// <summary>The terminator, or null while the block is still being built.</summary>
    public Instruction? Terminator => Instructions.Count > 0 && Instructions[^1].IsTerminator ? Instructions[^1] : null;

    /// <summary>The structured role of this block as a header.</summary>
    public StructureKind Structure { get; set; }

    /// <summary>For headers: the block control reaches after the construct (loop exit / selection join).</summary>
    public BasicBlock? MergeBlock { get; set; }

    /// <summary>For loop headers: the block that holds the increment and branches back to the header; <c>continue</c> jumps here.</summary>
    public BasicBlock? ContinueBlock { get; set; }

    /// <summary>Hint from a source attribute.</summary>
    public ControlHint Hint { get; set; }

    /// <summary>Successor blocks in terminator order.</summary>
    public IReadOnlyList<BasicBlock> Successors => Terminator?.Targets ?? (IReadOnlyList<BasicBlock>)System.Array.Empty<BasicBlock>();
}
