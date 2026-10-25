using System;
using System.Collections.Generic;
using OneShader.Diagnostics;
using OneShader.IR;
using OneShader.Reflection;
using OneShader.Types;

namespace OneShader.SpirV;

/// <summary>Settings for <see cref="SpirvBackend.Emit"/>.</summary>
public sealed class SpirvOptions
{
    /// <summary>Emit <c>OpName</c>/<c>OpMemberName</c> debug names from source names. Default true.</summary>
    public bool KeepNames { get; init; } = true;
}

/// <summary>Stable diagnostic codes of the SPIR-V backend (OSH5000-OSH5099). Codes are never reused.</summary>
internal static class SpirvCodes
{
    public const string EntryPointNotFound = "OSH5001";
    public const string Unsupported = "OSH5002";
    public const string MissingBinding = "OSH5003";
    public const string MalformedIr = "OSH5004";
}

/// <summary>Turns IR into a SPIR-V binary for the Vulkan 1.2 environment.</summary>
public static class SpirvBackend
{
    /// <summary>
    /// Emits SPIR-V 1.5 (Shader capability, logical addressing, GLSL450 memory model) for one entry point
    /// and everything it reaches. The output is deterministic: the same module and options give the same bytes.
    /// </summary>
    /// <param name="module">The IR module. Functions that take resource pointers must already be inlined.</param>
    /// <param name="entryPointName">The name of the entry point's function.</param>
    /// <param name="options">Settings; defaults when null.</param>
    /// <param name="diagnostics">Receives errors for unsupported constructs.</param>
    /// <returns>The module as little-endian bytes, or null when an error was reported (never throws for unsupported input).</returns>
    public static byte[]? Emit(IrModule module, string entryPointName, SpirvOptions? options, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(entryPointName);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var emitter = new SpirvEmitter(module, options ?? new SpirvOptions(), diagnostics);
        return emitter.Run(entryPointName);
    }
}

internal sealed partial class SpirvEmitter
{
    private const int MaxDepth = 64;

    private readonly IrModule _m;
    private readonly SpirvOptions _o;
    private readonly DiagnosticBag _d;
    private readonly SpirvModule _s = new();
    private EntryPoint _ep = null!;
    private bool _failed;

    private readonly Dictionary<GlobalVariable, uint> _globalIds = [];
    private readonly Dictionary<IrValue, uint> _ptrPointee = [];
    private readonly Dictionary<IrFunction, uint> _fnIds = [];

    public SpirvEmitter(IrModule m, SpirvOptions o, DiagnosticBag d)
    {
        _m = m;
        _o = o;
        _d = d;
    }

    private void Fail(string code, string message, SourceSpan span = default)
    {
        _failed = true;
        _d.Error(code, message, span);
    }

    private void Unsupported(string what, SourceSpan span = default) =>
        Fail(SpirvCodes.Unsupported, what, span);

    public byte[]? Run(string entryName)
    {
        EntryPoint? ep = null;
        foreach (var e in _m.EntryPoints)
        {
            if (e.Function.Name == entryName)
            {
                ep = e;
                break;
            }
        }

        if (ep is null)
        {
            Fail(SpirvCodes.EntryPointNotFound, "The module has no entry point named '" + entryName + "'; check the name passed to the compiler.");
            return null;
        }

        _ep = ep;
        _s.Capabilities.Add(Cap.Shader);
        if (_o.KeepNames)
        {
            SpirvModule.Put(_s.Debug, Op.Source, 5, 600);
        }

        // Which functions and globals does this entry point reach? Walk with an explicit stack.
        var functions = new HashSet<IrFunction> { ep.Function };
        var globals = new HashSet<GlobalVariable>();
        var work = new Stack<IrFunction>();
        work.Push(ep.Function);
        while (work.Count > 0)
        {
            var f = work.Pop();
            foreach (var b in f.Blocks)
            {
                foreach (var i in b.Instructions)
                {
                    foreach (var op in i.Operands)
                    {
                        if (op is GlobalVariable g)
                        {
                            globals.Add(g);
                        }
                    }

                    if (i.Callee is { } callee && functions.Add(callee))
                    {
                        work.Push(callee);
                    }
                }
            }
        }

        var order = new List<IrFunction>();
        foreach (var f in _m.Functions)
        {
            if (functions.Contains(f))
            {
                order.Add(f);
            }
        }

        if (!order.Contains(ep.Function))
        {
            order.Insert(0, ep.Function);
        }

        // Interface variables first (inputs, then outputs), then everything else in module order.
        var declared = new List<GlobalVariable>();
        var seen = new HashSet<GlobalVariable>();
        foreach (var g in ep.Inputs)
        {
            if (seen.Add(g))
            {
                declared.Add(g);
            }
        }

        foreach (var g in ep.Outputs)
        {
            if (seen.Add(g))
            {
                declared.Add(g);
            }
        }

        foreach (var g in _m.Globals)
        {
            if (globals.Contains(g) && seen.Add(g))
            {
                declared.Add(g);
            }
        }

        foreach (var g in declared)
        {
            DeclareGlobal(g);
        }

        foreach (var f in order)
        {
            _fnIds[f] = _s.NewId();
        }

        foreach (var f in order)
        {
            EmitFunction(f);
        }

        EmitEntryPoint(declared);
        if (_failed)
        {
            return null;
        }

        return _s.ToBytes();
    }

    private void EmitEntryPoint(List<GlobalVariable> declared)
    {
        uint model = _ep.Stage switch
        {
            ShaderStage.Vertex => 0,
            ShaderStage.Pixel => 4,
            _ => 5,
        };
        var after = new List<uint>();
        foreach (var g in declared)
        {
            if (_globalIds.TryGetValue(g, out uint gid))
            {
                after.Add(gid);
            }
        }

        SpirvModule.PutWithString(_s.EntryPoints, Op.EntryPoint, [model, _fnIds[_ep.Function]], _ep.Function.Name, after.ToArray());

        uint fn = _fnIds[_ep.Function];
        switch (_ep.Stage)
        {
            case ShaderStage.Pixel:
                SpirvModule.Put(_s.ExecutionModes, Op.ExecutionMode, fn, 7); // OriginUpperLeft
                if (_ep.WritesDepth || HasBuiltInOutput(BuiltIn.FragDepth))
                {
                    SpirvModule.Put(_s.ExecutionModes, Op.ExecutionMode, fn, 12); // DepthReplacing
                }

                break;
            case ShaderStage.Compute:
                var (x, y, z) = _ep.WorkgroupSize;
                if (x == 0 || y == 0 || z == 0)
                {
                    (x, y, z) = (1, 1, 1);
                }

                SpirvModule.Put(_s.ExecutionModes, Op.ExecutionMode, fn, 17, x, y, z); // LocalSize
                break;
            default:
                break;
        }
    }

    private bool HasBuiltInOutput(BuiltIn b)
    {
        foreach (var g in _ep.Outputs)
        {
            if (g.BuiltIn == b)
            {
                return true;
            }
        }

        return false;
    }
}
