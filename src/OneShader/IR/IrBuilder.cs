using System;
using System.Collections.Generic;
using OneShader.Diagnostics;
using OneShader.Types;

namespace OneShader.IR;

/// <summary>
/// Appends instructions to a block with result types worked out for the common cases. All methods
/// attach the instruction to <see cref="Block"/> and return it. Building into a block that already
/// has a terminator throws <see cref="InvalidOperationException"/>: that is a lowering bug, not a user error.
/// </summary>
public sealed class IrBuilder
{
    private readonly IrFunction _function;

    /// <summary>Creates a builder for a function; set <see cref="Block"/> before emitting.</summary>
    public IrBuilder(IrFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        _function = function;
    }

    /// <summary>The block that receives new instructions.</summary>
    public BasicBlock? Block { get; set; }

    /// <summary>Source position stamped on new instructions.</summary>
    public SourceSpan Span { get; set; }

    /// <summary>
    /// When true, floating-point arithmetic emitted from now on is marked <see cref="Instruction.Precise"/> (the front end sets it while it
    /// lowers an expression whose result goes into a <c>precise</c> variable or return value).
    /// </summary>
    public bool Precise { get; set; }

    /// <summary>Appends an instruction with a explicit result type.</summary>
    public Instruction Emit(Opcode op, HlslType type, params IrValue[] operands)
    {
        if (Block is null)
        {
            throw new InvalidOperationException("The builder has no current block.");
        }

        if (Block.Terminator is not null)
        {
            throw new InvalidOperationException("Block already ends in a terminator.");
        }

        var i = new Instruction(op, type, new List<IrValue>(operands)) { Span = Span, Parent = Block };
        if (Precise && op is Opcode.Add or Opcode.Sub or Opcode.Mul or Opcode.Div or Opcode.Rem or Opcode.Neg or Opcode.MatMul && type.ScalarElement is { IsFloatingPoint: true })
        {
            i.Precise = true;
        }

        Block.Instructions.Add(i);
        return i;
    }

    /// <summary>Allocates a function-local variable; the instruction goes at the end of the entry block's non-terminator prefix.</summary>
    public Instruction Alloca(HlslType valueType, string? name = null)
    {
        var entry = _function.Entry ?? throw new InvalidOperationException("The function has no entry block.");
        var i = new Instruction(Opcode.Alloca, new PointerType(AddressSpace.Function, valueType), new List<IrValue>())
        {
            Span = Span,
            Parent = entry,
            Name = name,
        };
        int at = 0;
        while (at < entry.Instructions.Count && entry.Instructions[at].Op == Opcode.Alloca)
        {
            at++;
        }

        entry.Instructions.Insert(at, i);
        return i;
    }

    /// <summary>Loads through a pointer.</summary>
    public Instruction Load(IrValue pointer) =>
        Emit(Opcode.Load, ((PointerType)pointer.Type).Pointee, pointer);

    /// <summary>Stores a value through a pointer.</summary>
    public Instruction Store(IrValue pointer, IrValue value) => Emit(Opcode.Store, HlslType.Void, pointer, value);

    /// <summary>Address of a member or element; <paramref name="resultPointee"/> is the type reached.</summary>
    public Instruction AccessChain(IrValue basePointer, HlslType resultPointee, params IrValue[] indices)
    {
        var space = ((PointerType)basePointer.Type).Space;
        var ops = new IrValue[indices.Length + 1];
        ops[0] = basePointer;
        System.Array.Copy(indices, 0, ops, 1, indices.Length);
        return Emit(Opcode.AccessChain, new PointerType(space, resultPointee), ops);
    }

    /// <summary>Arithmetic or bitwise binary operation; the result has the left operand's type.</summary>
    public Instruction Binary(Opcode op, IrValue left, IrValue right) => Emit(op, left.Type, left, right);

    /// <summary>Comparison; the result is bool, or a bool vector of the operands' width.</summary>
    public Instruction Compare(Opcode op, IrValue left, IrValue right) =>
        Emit(op, left.Type is VectorType v ? HlslType.Vector(ScalarType.Bool, v.Count) : ScalarType.Bool, left, right);

    /// <summary>Unary operation (neg, bit-not, logical-not); the result has the operand's type.</summary>
    public Instruction Unary(Opcode op, IrValue operand) => Emit(op, operand.Type, operand);

    /// <summary>Select between two values.</summary>
    public Instruction Select(IrValue condition, IrValue ifTrue, IrValue ifFalse) =>
        Emit(Opcode.Select, ifTrue.Type, condition, ifTrue, ifFalse);

    /// <summary>Numeric conversion.</summary>
    public Instruction Convert(IrValue value, HlslType to) => Emit(Opcode.Convert, to, value);

    /// <summary>Bit reinterpretation.</summary>
    public Instruction Bitcast(IrValue value, HlslType to) => Emit(Opcode.Bitcast, to, value);

    /// <summary>Builds a composite.</summary>
    public Instruction Construct(HlslType type, params IrValue[] parts) => Emit(Opcode.CompositeConstruct, type, parts);

    /// <summary>Extracts by literal indices.</summary>
    public Instruction Extract(IrValue composite, HlslType resultType, params int[] indices)
    {
        var i = Emit(Opcode.CompositeExtract, resultType, composite);
        i.Immediates.AddRange(indices);
        return i;
    }

    /// <summary>Swizzle or shuffle.</summary>
    public Instruction Shuffle(IrValue a, IrValue b, HlslType resultType, params int[] components)
    {
        var i = Emit(Opcode.VectorShuffle, resultType, a, b);
        i.Immediates.AddRange(components);
        return i;
    }

    /// <summary>Calls a user function.</summary>
    public Instruction Call(IrFunction callee, params IrValue[] args)
    {
        var i = Emit(Opcode.Call, callee.ReturnType, args);
        i.Callee = callee;
        return i;
    }

    /// <summary>Calls a built-in function.</summary>
    public Instruction CallIntrinsic(Intrinsic intrinsic, HlslType resultType, params IrValue[] args)
    {
        var i = Emit(Opcode.CallIntrinsic, resultType, args);
        i.Intrinsic = intrinsic;
        return i;
    }

    /// <summary>Atomic read-modify-write.</summary>
    public Instruction Atomic(AtomicOp op, IrValue pointer, IrValue value)
    {
        var i = Emit(Opcode.AtomicRmw, value.Type, pointer, value);
        i.Immediates.Add((int)op);
        return i;
    }

    /// <summary>Barrier.</summary>
    public Instruction Barrier(BarrierFlags flags)
    {
        var i = Emit(Opcode.Barrier, HlslType.Void);
        i.Immediates.Add((int)flags);
        return i;
    }

    /// <summary>Unconditional branch.</summary>
    public Instruction Branch(BasicBlock target)
    {
        var i = Emit(Opcode.Branch, HlslType.Void);
        i.Targets.Add(target);
        return i;
    }

    /// <summary>Conditional branch.</summary>
    public Instruction CondBranch(IrValue condition, BasicBlock ifTrue, BasicBlock ifFalse)
    {
        var i = Emit(Opcode.CondBranch, HlslType.Void, condition);
        i.Targets.Add(ifTrue);
        i.Targets.Add(ifFalse);
        return i;
    }

    /// <summary>Multi-way branch; <paramref name="cases"/> pairs a case value with its target.</summary>
    public Instruction Switch(IrValue selector, BasicBlock defaultTarget, IReadOnlyList<KeyValuePair<int, BasicBlock>> cases)
    {
        var i = Emit(Opcode.Switch, HlslType.Void, selector);
        i.Targets.Add(defaultTarget);
        foreach (var c in cases)
        {
            i.Immediates.Add(c.Key);
            i.Targets.Add(c.Value);
        }

        return i;
    }

    /// <summary>Return, with a value unless the function is void.</summary>
    public Instruction Return(IrValue? value = null) =>
        value is null ? Emit(Opcode.Return, HlslType.Void) : Emit(Opcode.Return, HlslType.Void, value);

    /// <summary>Discard.</summary>
    public Instruction Kill() => Emit(Opcode.Kill, HlslType.Void);

    /// <summary>Marks the end of unreachable code.</summary>
    public Instruction Unreachable() => Emit(Opcode.Unreachable, HlslType.Void);

    /// <summary>Phi node; inserted at the start of the block, after earlier phis.</summary>
    public Instruction Phi(BasicBlock block, HlslType type, IReadOnlyList<KeyValuePair<BasicBlock, IrValue>> incoming)
    {
        var i = new Instruction(Opcode.Phi, type, new List<IrValue>()) { Span = Span, Parent = block };
        foreach (var kv in incoming)
        {
            i.Operands.Add(kv.Value);
            i.Targets.Add(kv.Key);
        }

        int at = 0;
        while (at < block.Instructions.Count && block.Instructions[at].Op == Opcode.Phi)
        {
            at++;
        }

        block.Instructions.Insert(at, i);
        return i;
    }

    /// <summary>
    /// Marks <paramref name="header"/> as a selection header. Call after its terminator exists.
    /// </summary>
    public static void MarkSelection(BasicBlock header, BasicBlock merge, ControlHint hint = ControlHint.None)
    {
        header.Structure = StructureKind.Selection;
        header.MergeBlock = merge;
        header.Hint = hint;
    }

    /// <summary>Marks <paramref name="header"/> as a loop header with its exit and continue blocks.</summary>
    public static void MarkLoop(BasicBlock header, BasicBlock merge, BasicBlock continueBlock, ControlHint hint = ControlHint.None)
    {
        header.Structure = StructureKind.Loop;
        header.MergeBlock = merge;
        header.ContinueBlock = continueBlock;
        header.Hint = hint;
    }
}
