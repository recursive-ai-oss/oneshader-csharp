using System;
using System.Collections.Generic;
using System.IO;
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

    /// <summary>
    /// Run the optimizer: inlining, promotion of locals to SSA, constant folding, dead-code removal and control-flow simplification.
    /// Default true. It never changes what the shader computes; if an internal check fails, the shader is compiled without
    /// optimization and warning OSH6001 is reported. Set to false to compile the lowered code as written (for debugging).
    /// </summary>
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

    /// <summary>
    /// The files this compile read, so a file watcher knows what to observe: the main file first (the path passed to
    /// <see cref="ShaderCompiler.Compile"/>), then every file an <c>#include</c> found, each once, in ordinal order.
    /// Included files are listed by the path their resolver reported (the full path for file-system includes). The list is
    /// filled on failure too, with whatever was read before the error, so fixing an included file can trigger a retry.
    /// </summary>
    public IReadOnlyList<string> Files { get; internal set; } = [];

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
    /// <summary>The compiler pipeline is not available.</summary>
    public const string NotImplemented = "OSH0001";

    /// <summary>A requested output target is not one of the known <see cref="CompileTarget"/> values.</summary>
    public const string TargetNotAvailable = "OSH0002";

    /// <summary>The optimizer hit an internal problem; the shader was compiled without optimization (a warning).</summary>
    public const string OptimizerFallback = "OSH6001";

    /// <summary>The shader file passed to <see cref="ShaderCompiler.CompileFile"/> could not be read.</summary>
    public const string FileNotReadable = "OSH0003";

    /// <summary>An unexpected exception escaped a compiler stage; reported as an error on line 1 of the file (a compiler bug).</summary>
    public const string InternalError = "OSH7000";
}

/// <summary>The compiler's entry point.</summary>
public static class ShaderCompiler
{
    /// <summary>
    /// Compiles one HLSL source text. Never throws for problems in the shader or its includes: they
    /// are returned as diagnostics with file, line and column; an unexpected internal exception is returned as error OSH7000. Throws <see cref="ArgumentNullException"/>
    /// for null arguments and <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    /// <param name="source">The HLSL text.</param>
    /// <param name="path">The file path as the user knows it; used in diagnostics and to resolve includes.</param>
    /// <param name="options">Settings; defaults apply when null.</param>
    /// <param name="cancellationToken">Cancels a long compile.</param>
    /// <returns>
    /// The result: all diagnostics, and when there were no errors the reflection and one blob per entry point
    /// and target. SPIR-V, MSL and WGSL are available.
    /// </returns>
    public static CompileResult Compile(string source, string path, CompileOptions? options = null, CancellationToken cancellationToken = default)
    {
        return CompileGuarded(source, path, options, null, cancellationToken);
    }

    /// <summary>
    /// Reads a shader file and compiles it, for tools and hot reload. Behaves like <see cref="Compile"/> with the file's text and
    /// <paramref name="path"/> as the name in diagnostics, and includes are found on disk (relative to the including file, then
    /// <see cref="CompileOptions.IncludeDirectories"/>) unless <see cref="CompileOptions.IncludeResolver"/> is set.
    /// A file that is missing or cannot be read is error <see cref="ApiDiagnosticCodes.FileNotReadable"/> in the result, not an exception.
    /// <see cref="CompileResult.Files"/> lists full paths, the shader first, so it can be watched even when the compile fails.
    /// Throws <see cref="ArgumentNullException"/> for a null path and <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    /// <param name="path">The file to compile; relative paths are relative to the current directory.</param>
    /// <param name="options">Settings; defaults apply when null.</param>
    /// <param name="cancellationToken">Cancels a long compile.</param>
    public static CompileResult CompileFile(string path, CompileOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();
        string full;
        string text;
        try
        {
            full = Path.GetFullPath(path);
            text = File.ReadAllText(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            var failure = new DiagnosticBag();
            failure.Error(ApiDiagnosticCodes.FileNotReadable, "Cannot read the shader file: " + ex.Message + " Check the path and permissions.", new SourceSpan(path, 1, 1));
            return new CompileResult(failure.Items, [], null) { Files = [path] };
        }

        var result = Compile(text, path, options, cancellationToken);
        var files = new List<string>(result.Files) { [0] = full };
        result.Files = files;
        return result;
    }

    /// <summary>
    /// Runs the pipeline and turns any unexpected exception into error OSH7000 on line 1 of the file, so a compiler
    /// bug never takes down the host (the build node or a tool). <paramref name="probe"/> runs after lowering; tests use it to force a failure.
    /// </summary>
    internal static CompileResult CompileGuarded(string source, string path, CompileOptions? options, Action<IR.IrModule>? probe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();
        var includes = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var result = CompileCore(source, path, options ?? new CompileOptions(), probe, includes, cancellationToken);
            result.Files = ListFiles(path, includes);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failure = new DiagnosticBag();
            failure.Error(ApiDiagnosticCodes.InternalError, "Internal compiler error (" + ex.GetType().Name + ": " + ex.Message + "). This is a bug in the compiler, not in the shader; please report it with the shader. Setting Optimize to false may avoid it.", new SourceSpan(path, 1, 1));
            return new CompileResult(failure.Items, [], null) { Files = ListFiles(path, includes) };
        }
    }

    private static string[] ListFiles(string path, HashSet<string> includes)
    {
        var sorted = new List<string>(includes.Count);
        foreach (string f in includes)
        {
            if (!string.Equals(f, path, StringComparison.Ordinal))
            {
                sorted.Add(f);
            }
        }

        sorted.Sort(StringComparer.Ordinal);
        sorted.Insert(0, path);
        return [.. sorted];
    }

    private static CompileResult CompileCore(string source, string path, CompileOptions options, Action<IR.IrModule>? probe, ICollection<string> includesRead, CancellationToken cancellationToken)
    {
        var result = Semantics.Frontend.Run(source, path, options, includesRead, cancellationToken);
        var diagnostics = result.Diagnostics;
        var blobs = new List<ShaderBlob>();
        if (diagnostics.HasErrors || result.Module is not { } module)
        {
            return new CompileResult(diagnostics.Items, blobs, null);
        }

        probe?.Invoke(module);
        if (options.Optimize)
        {
            var failure = Optimization.Optimizer.Run(module, cancellationToken);
            if (failure is not null)
            {
                // Lowering is deterministic, so running the front end again gives a pristine module to emit instead.
                diagnostics.Warning(ApiDiagnosticCodes.OptimizerFallback, "The optimizer failed (" + failure + "); the shader was compiled without optimization. Please report this with the shader; setting Optimize to false avoids the problem.", new SourceSpan(path, 1, 1));
                var fresh = Semantics.Frontend.Run(source, path, options, cancellationToken);
                if (fresh.Module is null)
                {
                    diagnostics.Error(ApiDiagnosticCodes.OptimizerFallback, "Recompiling without the optimizer failed; this is a compiler bug.", new SourceSpan(path, 1, 1));
                    return new CompileResult(diagnostics.Items, blobs, null);
                }

                module = fresh.Module;
            }
        }

        var targets = options.Targets.Count == 0 ? [CompileTarget.SpirV] : options.Targets;
        foreach (var requested in targets)
        {
            if (!Enum.IsDefined(requested))
            {
                diagnostics.Error(ApiDiagnosticCodes.TargetNotAvailable, "The output target " + (int)requested + " is not known; use SpirV, Msl or Wgsl.", new SourceSpan(path, 1, 1));
            }
        }

        var spirvOptions = new SpirV.SpirvOptions { KeepNames = options.KeepNames };
        var mslOptions = new Msl.MslOptions { KeepNames = options.KeepNames };
        var wgslOptions = new Wgsl.WgslOptions { KeepNames = options.KeepNames };
        foreach (var entry in module.EntryPoints)
        {
            foreach (var target in (CompileTarget[])[CompileTarget.SpirV, CompileTarget.Msl, CompileTarget.Wgsl])
            {
                if (!Wants(targets, target))
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (target == CompileTarget.Wgsl)
                {
                    string? text = Wgsl.WgslBackend.Emit(module, entry.Function.Name, wgslOptions, diagnostics);
                    if (text is not null)
                    {
                        blobs.Add(new ShaderBlob(entry.Function.Name, entry.Stage, target, Encoding.UTF8.GetBytes(text)));
                    }

                    continue;
                }

                if (target == CompileTarget.Msl)
                {
                    string? text = Msl.MslBackend.Emit(module, entry.Function.Name, mslOptions, diagnostics);
                    if (text is not null)
                    {
                        blobs.Add(new ShaderBlob(entry.Function.Name, entry.Stage, target, Encoding.UTF8.GetBytes(text)));
                    }

                    continue;
                }

                var bytes = SpirV.SpirvBackend.Emit(module, entry.Function.Name, spirvOptions, diagnostics);
                if (bytes is not null)
                {
                    blobs.Add(new ShaderBlob(entry.Function.Name, entry.Stage, target, bytes));
                }
            }
        }

        if (diagnostics.HasErrors)
        {
            return new CompileResult(diagnostics.Items, [], null);
        }

        return new CompileResult(diagnostics.Items, blobs, result.Reflection);
    }

    private static bool Wants(IReadOnlyList<CompileTarget> targets, CompileTarget target)
    {
        foreach (var t in targets)
        {
            if (t == target)
            {
                return true;
            }
        }

        return false;
    }
}
