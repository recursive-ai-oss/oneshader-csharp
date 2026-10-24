using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using OneShader.Diagnostics;
using OneShader.Reflection;
using OneShader.Types;

namespace OneShader;

/// <summary>The backends a compile can target.</summary>
public enum CompileTarget
{
    /// <summary>SPIR-V for Vulkan 1.2 or newer.</summary>
    SpirV = 0,

    /// <summary>Metal Shading Language source.</summary>
    Msl = 1,

    /// <summary>WGSL source for WebGPU.</summary>
    Wgsl = 2,
}

/// <summary>An include file found by an <see cref="IncludeResolver"/>.</summary>
/// <param name="Path">The path to report in diagnostics and to resolve nested includes against.</param>
/// <param name="Text">The file contents.</param>
public sealed record ResolvedInclude(string Path, string Text);

/// <summary>
/// Finds the file for an <c>#include</c>. Called with the name as written, the path of the including
/// file, and whether the angle-bracket form was used. Return null when the file does not exist; the
/// compiler then reports the error on the <c>#include</c> line. When no resolver is supplied,
/// includes fail with a diagnostic. Must not throw for a missing file.
/// </summary>
public delegate ResolvedInclude? IncludeResolver(string requested, string includingFile, bool isSystemInclude);

/// <summary>An entry point to compile, when not discovered from <c>[shader("...")]</c> attributes.</summary>
/// <param name="Name">The function name.</param>
/// <param name="Stage">The stage to compile it for.</param>
public readonly record struct EntryPointRequest(string Name, ShaderStage Stage);

/// <summary>Settings for <see cref="ShaderCompiler.Compile"/>. All properties have defaults.</summary>
public sealed class CompileOptions
{
    /// <summary>Targets to produce. Default: SPIR-V only. An empty list is treated as the default.</summary>
    public IReadOnlyList<CompileTarget> Targets { get; init; } = [CompileTarget.SpirV];

    /// <summary>Preprocessor definitions (<c>-D name=value</c>); a null value defines the name as 1.</summary>
    public IReadOnlyList<KeyValuePair<string, string?>> Defines { get; init; } = [];

    /// <summary>Directories searched for <c>#include</c> after the including file's own directory, when no <see cref="IncludeResolver"/> is set and the host reads files.</summary>
    public IReadOnlyList<string> IncludeDirectories { get; init; } = [];

    /// <summary>The include hook. Default null: <c>#include</c> is an error.</summary>
    public IncludeResolver? IncludeResolver { get; init; }

    /// <summary>
    /// Entry points to compile. Default: every function marked <c>[shader("vertex"|"pixel"|"compute")]</c>.
    /// When set, only these are compiled and the attribute is not required.
    /// </summary>
    public IReadOnlyList<EntryPointRequest> EntryPoints { get; init; } = [];

    /// <summary>Packing for matrices with no explicit <c>row_major</c>/<c>column_major</c> (HLSL's default is column-major).</summary>
    public MatrixPacking DefaultMatrixPacking { get; init; } = MatrixPacking.ColumnMajor;

    /// <summary>True: <c>half</c> and <c>min16*</c> are 16-bit, as with DXC's <c>-enable-16bit-types</c>. Default false: they are stored and computed as 32-bit.</summary>
    public bool Enable16BitTypes { get; init; }

    /// <summary>Run the optimizer. Default true.</summary>
    public bool Optimize { get; init; } = true;

    /// <summary>Keep source-level names in the output where the target allows (SPIR-V debug names, MSL/WGSL identifiers). Default true.</summary>
    public bool KeepNames { get; init; } = true;
}

/// <summary>The compiled code of one entry point for one target.</summary>
public sealed class ShaderBlob
{
    private readonly byte[] _data;

    /// <summary>Creates a blob; the array is kept, not copied.</summary>
    public ShaderBlob(string entryPoint, ShaderStage stage, CompileTarget target, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(entryPoint);
        ArgumentNullException.ThrowIfNull(data);
        EntryPoint = entryPoint;
        Stage = stage;
        Target = target;
        _data = data;
    }

    /// <summary>The entry point name.</summary>
    public string EntryPoint { get; }

    /// <summary>The stage.</summary>
    public ShaderStage Stage { get; }

    /// <summary>The target.</summary>
    public CompileTarget Target { get; }

    /// <summary>The bytes: SPIR-V words in little-endian order, or UTF-8 text for MSL and WGSL.</summary>
    public ReadOnlySpan<byte> Data => _data;

    /// <summary>A copy of the bytes.</summary>
    public byte[] ToArray() => (byte[])_data.Clone();

    /// <summary>The text of an MSL or WGSL blob. Throws <see cref="InvalidOperationException"/> for SPIR-V.</summary>
    public string AsText() =>
        Target == CompileTarget.SpirV ? throw new InvalidOperationException("SPIR-V is binary; use Data.") : Encoding.UTF8.GetString(_data);
}

/// <summary>The outcome of a compile. Compile problems are diagnostics here, never exceptions.</summary>
public sealed class CompileResult
{
    internal CompileResult(IReadOnlyList<Diagnostic> diagnostics, IReadOnlyList<ShaderBlob> blobs, ShaderReflection? reflection)
    {
        Diagnostics = diagnostics;
        Blobs = blobs;
        Reflection = reflection;
    }

    /// <summary>All diagnostics in report order.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>True when no diagnostic is an error. Blobs and reflection exist only then.</summary>
    public bool Success
    {
        get
        {
            foreach (var d in Diagnostics)
            {
                if (d.Severity == DiagnosticSeverity.Error)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>The compiled code, entry points in source order and targets in <see cref="CompileTarget"/> order within each.</summary>
    public IReadOnlyList<ShaderBlob> Blobs { get; }

    /// <summary>Reflection for the file; null when compilation failed.</summary>
    public ShaderReflection? Reflection { get; }

    /// <summary>Finds the blob for an entry point and target, or null.</summary>
    public ShaderBlob? Find(string entryPoint, CompileTarget target)
    {
        foreach (var b in Blobs)
        {
            if (b.Target == target && b.EntryPoint == entryPoint)
            {
                return b;
            }
        }

        return null;
    }
}

/// <summary>Diagnostic codes of the public API layer.</summary>
public static class ApiDiagnosticCodes
{
    /// <summary>The compiler pipeline is not available yet.</summary>
    public const string NotImplemented = "OSH0001";
}

/// <summary>The compiler's entry point.</summary>
public static class ShaderCompiler
{
    /// <summary>
    /// Compiles one HLSL source text. Never throws for problems in the shader or its includes: they
    /// are returned as diagnostics with file, line and column. Throws <see cref="ArgumentNullException"/>
    /// for null arguments and <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    /// <param name="source">The HLSL text.</param>
    /// <param name="path">The file path as the user knows it; used in diagnostics and to resolve includes.</param>
    /// <param name="options">Settings; defaults apply when null.</param>
    /// <param name="cancellationToken">Cancels a long compile.</param>
    /// <returns>The result: diagnostics, and reflection when there were no errors. Code generation for the targets is not wired in yet, so <see cref="CompileResult.Blobs"/> is empty.</returns>
    public static CompileResult Compile(string source, string path, CompileOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();
        var result = Semantics.Frontend.Run(source, path, options ?? new CompileOptions(), cancellationToken);
        return new CompileResult(result.Diagnostics.Items, [], result.Reflection);
    }
}
