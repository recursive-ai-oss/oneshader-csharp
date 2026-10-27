using System;
using System.Collections.Generic;
using OneShader.Diagnostics;
using OneShader.Types;

namespace OneShader.IR;

/// <summary>Hints about inlining a function.</summary>
public enum InlineHint
{
    /// <summary>The optimizer decides.</summary>
    Auto,

    /// <summary>Always inline (<c>[forceinline]</c>, or required because a parameter is a resource pointer).</summary>
    Always,

    /// <summary>Never inline (<c>[noinline]</c>).</summary>
    Never,
}

/// <summary>A function: parameters, a list of blocks (the first is the entry) and its return type.</summary>
public sealed class IrFunction
{
    private readonly List<Argument> _parameters = [];

    /// <summary>Creates an empty function; add parameters with <see cref="AddParameter"/> and blocks with <see cref="AddBlock"/>.</summary>
    public IrFunction(string name, HlslType returnType)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(returnType);
        Name = name;
        ReturnType = returnType;
    }

    /// <summary>The function name; unique in the module.</summary>
    public string Name { get; set; }

    /// <summary>The return type (<see cref="HlslType.Void"/> for none).</summary>
    public HlslType ReturnType { get; }

    /// <summary>Parameters in order.</summary>
    public IReadOnlyList<Argument> Parameters => _parameters;

    /// <summary>Blocks in layout order, which is also the dump order. The first block is the entry.</summary>
    public List<BasicBlock> Blocks { get; } = [];

    /// <summary>Inlining hint.</summary>
    public InlineHint Inline { get; set; }

    /// <summary>Where the function was declared.</summary>
    public SourceSpan Span { get; set; }

    /// <summary>The entry block, or null for a function without blocks.</summary>
    public BasicBlock? Entry => Blocks.Count > 0 ? Blocks[0] : null;

    /// <summary>Adds a parameter.</summary>
    public Argument AddParameter(HlslType type, string? name = null)
    {
        var a = new Argument(type, name) { Index = _parameters.Count };
        _parameters.Add(a);
        return a;
    }

    /// <summary>Appends an empty block.</summary>
    public BasicBlock AddBlock(string? name = null)
    {
        var b = new BasicBlock(this, name);
        Blocks.Add(b);
        return b;
    }

    /// <summary>Rewrites every use of <paramref name="from"/> in this function (operands only; block targets are untouched) to <paramref name="to"/>. Linear in the function size.</summary>
    public void ReplaceUses(IrValue from, IrValue to)
    {
        foreach (var b in Blocks)
        {
            foreach (var i in b.Instructions)
            {
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    if (ReferenceEquals(i.Operands[k], from))
                    {
                        i.Operands[k] = to;
                    }
                }
            }
        }
    }
}

/// <summary>One shader entry point: a function plus the stage interface it uses.</summary>
public sealed class EntryPoint
{
    /// <summary>Creates an entry point. The function takes no parameters and returns void; inputs and outputs are module globals.</summary>
    public EntryPoint(IrFunction function, ShaderStage stage)
    {
        ArgumentNullException.ThrowIfNull(function);
        Function = function;
        Stage = stage;
    }

    /// <summary>The entry function.</summary>
    public IrFunction Function { get; }

    /// <summary>The stage.</summary>
    public ShaderStage Stage { get; }

    /// <summary>Compute thread-group size from <c>[numthreads]</c>.</summary>
    public (uint X, uint Y, uint Z) WorkgroupSize { get; set; }

    /// <summary>Input globals this entry point reads, in declaration order.</summary>
    public List<GlobalVariable> Inputs { get; } = [];

    /// <summary>Output globals this entry point writes, in declaration order.</summary>
    public List<GlobalVariable> Outputs { get; } = [];

    /// <summary>Pixel stage: the shader writes depth, so early depth testing must stay off.</summary>
    public bool WritesDepth { get; set; }
}

/// <summary>A compiled translation unit: globals, functions and entry points in source order.</summary>
public sealed class IrModule
{
    /// <summary>Source file name, for dumps (no directory).</summary>
    public string Name { get; set; } = "";

    /// <summary>Module-scope variables in declaration order.</summary>
    public List<GlobalVariable> Globals { get; } = [];

    /// <summary>Specialization constants in declaration order.</summary>
    public List<SpecConstant> SpecConstants { get; } = [];

    /// <summary>Functions in declaration order; callees need not precede callers.</summary>
    public List<IrFunction> Functions { get; } = [];

    /// <summary>Entry points in declaration order.</summary>
    public List<EntryPoint> EntryPoints { get; } = [];

    /// <summary>Layout settings the buffer layouts in this module were computed with.</summary>
    public LayoutOptions LayoutOptions { get; set; } = LayoutOptions.Default;
}
