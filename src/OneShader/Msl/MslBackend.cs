using System.Collections.Generic;
using System;
using OneShader.Diagnostics;
using OneShader.IR;

namespace OneShader.Msl;

/// <summary>Settings for <see cref="MslBackend.Emit"/>.</summary>
public sealed class MslOptions
{
    /// <summary>Keep source-level names (variables, struct members) in the output where they are legal MSL. Default true; false gives short numbered names.</summary>
    public bool KeepNames { get; init; } = true;
}

/// <summary>Stable diagnostic codes of the MSL backend (OSH5100-OSH5149). Codes are never reused.</summary>
internal static class MslCodes
{
    public const string EntryPointNotFound = "OSH5101";
    public const string Unsupported = "OSH5102";
    public const string MalformedIr = "OSH5104";
    public const string TooManyResources = "OSH5105";
}

/// <summary>Turns IR into Metal Shading Language source.</summary>
public static class MslBackend
{
    /// <summary>
    /// Emits MSL (language version 2.4 or newer) for one entry point and everything it reaches, as one self-contained source
    /// file. The output is deterministic: the same module and options give the same text.
    /// </summary>
    /// <param name="module">The IR module. Functions that take resource pointers must already be inlined.</param>
    /// <param name="entryPointName">The name of the entry point's function.</param>
    /// <param name="options">Settings; defaults when null.</param>
    /// <param name="diagnostics">Receives errors for constructs MSL cannot express.</param>
    /// <returns>The source text, or null when an error was reported (never throws for unsupported input).</returns>
    public static string? Emit(IrModule module, string entryPointName, MslOptions? options, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(entryPointName);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var emitter = new MslEmitter(module, options ?? new MslOptions(), diagnostics);
        return emitter.Run(entryPointName);
    }

    /// <summary>
    /// The Metal buffer index a vertex shader's <c>[[stage_in]]</c> attributes are read from. The compiler never gives a resource this index in a
    /// vertex function that has vertex inputs (it reports an error instead of using it), so a host can set one interleaved vertex buffer here
    /// with the offsets and stride from reflection.
    /// </summary>
    public const int VertexBufferIndex = 30;

    /// <summary>
    /// The name of the entry point's function in the MSL source, which a host passes to <c>newFunction(name:)</c>.
    /// It is the HLSL name, except that names MSL reserves get a suffix (<c>main</c> becomes <c>main0</c>) and characters that are not letters or digits become underscores.
    /// </summary>
    public static string EntryFunctionName(string hlslName)
    {
        ArgumentNullException.ThrowIfNull(hlslName);
        return MslNames.EntryName(hlslName);
    }

    /// <summary>The name of a specialization constant's <c>[[function_constant]]</c> variable in the MSL source.</summary>
    internal static string SpecConstantName(IrModule module, SpecConstant constant)
    {
        var raw = new Dictionary<SpecConstant, string>(ReferenceEqualityComparer.Instance);
        MslEmitter.ClaimSpecNames(module, new MslNames(), raw, new Dictionary<SpecConstant, string>(ReferenceEqualityComparer.Instance));
        return raw.TryGetValue(constant, out var n) ? n : constant.Name ?? "spec";
    }
}
