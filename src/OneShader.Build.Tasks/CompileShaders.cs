using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using OneShader.Diagnostics;
using OneShader.Reflection;

namespace OneShader.Build.Tasks;

/// <summary>
/// Compiles the project's HLSL files. For each <c>OneShader</c> item it writes one
/// <c>.oneshader</c> file (reflection, compiled blobs, and the bookkeeping that lets the next build
/// skip unchanged shaders) and returns the files so the build can hand them to the source
/// generator as additional files. Diagnostics are logged with file, line, column and code, so
/// MSBuild prints <c>path(line,col): error OSHxxxx: message</c> and the build fails on errors.
/// </summary>
/// <remarks>Codes OSH0900 to OSH0949 belong to this task.</remarks>
public sealed partial class CompileShaders : Task, ICancelableTask, IDisposable
{
    private const string InternalErrorCode = "OSH0900";
    private const string BadMetadataCode = "OSH0901";
    private const string UnreadableCode = "OSH0902";
    private const string BadTargetCode = "OSH0903";

    private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
    private int _compiled;
    private int _upToDate;

    /// <summary>The shaders to compile. Metadata: <c>EntryPoints</c> (<c>Name:Stage;...</c>), <c>Defines</c> (<c>A;B=2</c>), <c>IncludeDirectories</c>, <c>Namespace</c>, <c>ClassName</c>, <c>Targets</c> (<c>SpirV;Msl;Wgsl</c>, overrides <see cref="DefaultTargets"/>).</summary>
    [Required]
    public ITaskItem[] Shaders { get; set; } = [];

    /// <summary>Directory for the output files; created when missing.</summary>
    [Required]
    public string OutputDirectory { get; set; } = "";

    /// <summary>The project directory; relative item paths and include directories are resolved against it.</summary>
    [Required]
    public string ProjectDirectory { get; set; } = "";

    /// <summary>Namespace for shaders whose item has no <c>Namespace</c> metadata.</summary>
    public string DefaultNamespace { get; set; } = "";

    /// <summary>Semicolon-separated targets to compile for (<c>SpirV</c>, <c>Msl</c>, <c>Wgsl</c>); empty means SPIR-V only.</summary>
    public string DefaultTargets { get; set; } = "";

    /// <summary>"true" makes <c>half</c>, <c>min16*</c> and the <c>int16_t</c> family native 16-bit types in every shader (DXC's <c>-enable-16bit-types</c>); the item's <c>Enable16BitTypes</c> metadata overrides it.</summary>
    public string DefaultEnable16BitTypes { get; set; } = "";

    /// <summary>Semicolon-separated definitions applied to every shader (<c>NAME</c> or <c>NAME=value</c>).</summary>
    public string DefaultDefines { get; set; } = "";

    /// <summary>Semicolon-separated include directories searched for every shader.</summary>
    public string DefaultIncludeDirectories { get; set; } = "";

    /// <summary>The output files, one per shader, in item order.</summary>
    [Output]
    public ITaskItem[] GeneratedFiles { get; set; } = [];

    /// <summary>How many files (shaders or variants) the last <see cref="Execute"/> compiled; the others were up to date.</summary>
    public int CompiledCount => _compiled;

    /// <summary>How many files the last <see cref="Execute"/> found up to date and left alone.</summary>
    public int UpToDateCount => _upToDate;

    /// <inheritdoc />
    public void Cancel() => _cancel.Cancel();

    /// <inheritdoc />
    public void Dispose() => _cancel.Dispose();

    /// <inheritdoc />
    public override bool Execute()
    {
        string projectDir = Path.GetFullPath(ProjectDirectory);
        string outDir = Path.GetFullPath(OutputDirectory, projectDir);
        Directory.CreateDirectory(outDir);

        // Phase 1 (sequential): read each item's metadata and expand its variants into jobs.
        var groups = new List<JobGroup>();
        foreach (var item in Shaders)
        {
            if (_cancel.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                Prepare(item, projectDir, outDir, groups);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                string file = Path.GetFullPath(item.GetMetadata("FullPath").Length > 0 ? item.GetMetadata("FullPath") : item.ItemSpec, projectDir);
                Log.LogError(null, InternalErrorCode, null, file, 1, 1, 0, 0, "Internal error while reading this shader's settings (" + ex.GetType().Name + ": " + OneLine(ex.Message) + "). This is a bug in OneShader; please report it with this shader.");
            }
        }

        if (!CheckVariantNames())
        {
            return false;
        }

        // Phase 2 (parallel when there is more than one job): compile. Jobs do not log; they collect messages.
        var jobs = new List<Job>();
        foreach (var g in groups)
        {
            jobs.AddRange(g.Jobs);
        }

        try
        {
            if (jobs.Count == 1)
            {
                RunJob(jobs[0], projectDir);
            }
            else if (jobs.Count > 1)
            {
                var po = new System.Threading.Tasks.ParallelOptions { CancellationToken = _cancel.Token, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8) };
                System.Threading.Tasks.Parallel.ForEach(jobs, po, job => RunJob(job, projectDir));
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        // Phase 3 (sequential, in item order): report, then list the outputs.
        var outputs = new List<ITaskItem>();
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in groups)
        {
            Report(g);
            bool ok = true;
            foreach (var job in g.Jobs)
            {
                ok &= job.Output is not null;
            }

            if (g.ManifestPath is not null)
            {
                if (ok)
                {
                    WriteIfChanged(g.ManifestPath, g.ManifestText);
                    outputs.Add(new TaskItem(g.ManifestPath));
                    expected.Add(g.ManifestPath);
                }
                else
                {
                    TryDelete(g.ManifestPath);
                }
            }

            foreach (var job in g.Jobs)
            {
                if (job.Output is not null)
                {
                    outputs.Add(new TaskItem(job.Output));
                    expected.Add(job.Output);
                }
            }
        }

        RemoveStale(outDir, expected);
        GeneratedFiles = [.. outputs];
        Log.LogMessage(MessageImportance.Low, "OneShader: {0} compiled, {1} up to date.", _compiled, _upToDate);
        return !Log.HasLoggedErrors;
    }

    private static string OneLine(string s) => s.Replace('\r', ' ').Replace('\n', ' ');

    private static void RemoveStale(string outDir, HashSet<string> keep)
    {
        // A shader that was deleted or renamed must not keep feeding the generator.
        foreach (string f in Directory.EnumerateFiles(outDir, "*.oneshader", SearchOption.AllDirectories))
        {
            if (!keep.Contains(f))
            {
                try
                {
                    File.Delete(f);
                }
                catch (IOException)
                {
                    // Still in use; the next build removes it.
                }
                catch (UnauthorizedAccessException)
                {
                    // Read-only or locked; the next build removes it.
                }
            }
        }
    }

    /// <summary>One message a job produced, kept as data so jobs can run on several threads and be reported in a fixed order.</summary>
    private readonly record struct Msg(int Severity, string Code, string File, int Line, int Column, string Text);

    private const int SevError = 0;
    private const int SevWarning = 1;
    private const int SevInfo = 2;

    /// <summary>One compile: a whole shader file, or one variant of it.</summary>
    private sealed class Job
    {
        public string Full = "";
        public string Relative = "";
        public string OutputPath = "";
        public string Namespace = "";
        public string ClassName = "";
        public List<KeyValuePair<string, string?>> Defines = [];
        public List<string> IncludeDirs = [];
        public List<EntryPointRequest> Entries = [];
        public List<CompileTarget> Targets = [];
        public bool Enable16BitTypes;

        /// <summary>Empty for a plain shader; otherwise the variant's define set, for messages.</summary>
        public string Label = "";

        /// <summary>Empty for a plain shader; otherwise the id that ties the variant's file to its manifest.</summary>
        public string VariantKey = "";

        public List<Msg> Messages = [];

        /// <summary>The output file when the job succeeded.</summary>
        public string? Output;
    }

    /// <summary>The jobs of one <c>OneShader</c> item, with the manifest that lists them when the item has variants.</summary>
    private sealed class JobGroup
    {
        public List<Job> Jobs = [];
        public string? ManifestPath;
        public string ManifestText = "";
        public string Full = "";
        public int VariantCount;
    }

    private void Prepare(ITaskItem item, string projectDir, string outDir, List<JobGroup> groups)
    {
        string full = Path.GetFullPath(item.GetMetadata("FullPath").Length > 0 ? item.GetMetadata("FullPath") : item.ItemSpec, projectDir);
        string relative = Relative(projectDir, full);
        string outputPath = Path.Combine(outDir, relative.Replace("../", "__/", StringComparison.Ordinal).Replace('/', Path.DirectorySeparatorChar) + ".oneshader");
        string ns = Meta(item, "Namespace", DefaultNamespace);
        string className = Meta(item, "ClassName", "");

        if (!TryOptions(item, full, projectDir, out var defines, out var includeDirs, out var entries, out var targets))
        {
            return;
        }

        if (!TryEnable16(item, full, out bool enable16))
        {
            return;
        }

        var group = new JobGroup { Full = full };
        var plan = PlanVariants(item, full, relative, projectDir);
        if (plan is null)
        {
            return;
        }

        if (!plan.HasVariants)
        {
            group.Jobs.Add(new Job
            {
                Full = full, Relative = relative, OutputPath = outputPath, Namespace = ns, ClassName = className,
                Defines = defines, IncludeDirs = includeDirs, Entries = entries, Targets = targets, Enable16BitTypes = enable16,
            });
            groups.Add(group);
            return;
        }

        // Variants: one output file per variant under <file>.variants/, named by a hash of the define set so that adding a value
        // to an axis leaves the other variants' files (and their cached compiles) alone, plus one manifest at the usual path.
        string variantDir = outputPath[..^".oneshader".Length] + ".variants";
        var manifest = new StringBuilder("oneshader-reflection 1\n");
        manifest.Append("source name=").Append(Quote(relative)).Append('\n');
        manifest.Append("generate namespace=").Append(Quote(ns)).Append(" class=").Append(Quote(className)).Append('\n');
        manifest.Append("variantset mode=").Append(plan.Named ? "names" : "axes").Append(" count=").Append(plan.Variants.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var axis in plan.Axes)
        {
            manifest.Append("axis name=").Append(Quote(axis.Name)).Append(" kind=").Append(axis.IsFlag ? "flag" : "values");
            if (!axis.IsFlag)
            {
                manifest.Append(" values=").Append(Quote(string.Join(",", axis.Values)));
            }

            manifest.Append('\n');
        }

        for (int i = 0; i < plan.Variants.Count; i++)
        {
            var v = plan.Variants[i];
            var jobDefines = new List<KeyValuePair<string, string?>>(defines);
            jobDefines.AddRange(v.Defines);
            group.Jobs.Add(new Job
            {
                Full = full, Relative = relative, Namespace = ns, ClassName = className,
                OutputPath = Path.Combine(variantDir, v.Key + ".oneshader"),
                Defines = jobDefines, IncludeDirs = includeDirs, Entries = entries, Targets = targets, Enable16BitTypes = enable16,
                Label = v.Label, VariantKey = v.Key,
            });
            manifest.Append("variant index=").Append(i.ToString(CultureInfo.InvariantCulture)).Append(" key=").Append(v.Key);
            if (plan.Named)
            {
                manifest.Append(" name=").Append(Quote(v.Name));
            }
            else
            {
                manifest.Append(" ordinals=").Append(Quote(string.Join(",", v.Ordinals)));
            }

            manifest.Append(" defines=").Append(Quote(v.DefinesText)).Append('\n');
        }

        group.ManifestPath = outputPath;
        group.ManifestText = manifest.ToString();
        group.VariantCount = plan.Variants.Count;
        groups.Add(group);
    }

    private void RunJob(Job job, string projectDir)
    {
        try
        {
            job.Output = Compile(job, projectDir);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Anything unexpected outside the compiler call (rendering the output, hashing, file access) still must not
            // surface as an MSBuild task crash with a stack trace.
            job.Messages.Add(new Msg(SevError, InternalErrorCode, job.Full, 1, 1, "Internal error while building this shader (" + ex.GetType().Name + ": " + OneLine(ex.Message) + "). This is a bug in OneShader; please report it with this shader."));
            job.Output = null;
        }
    }

    /// <summary>Logs a group's messages. The variants of one shader often fail the same way; the same message is reported once, naming the variants it occurred in.</summary>
    private void Report(JobGroup group)
    {
        if (group.Jobs.Count == 1 && group.Jobs[0].Label.Length == 0)
        {
            foreach (var m in group.Jobs[0].Messages)
            {
                Emit(m);
            }

            return;
        }

        var order = new List<KeyValuePair<Msg, List<string>>>();
        var index = new Dictionary<Msg, int>();
        foreach (var job in group.Jobs)
        {
            foreach (var m in job.Messages)
            {
                if (index.TryGetValue(m, out int at))
                {
                    order[at].Value.Add(job.Label);
                }
                else
                {
                    index[m] = order.Count;
                    order.Add(new KeyValuePair<Msg, List<string>>(m, [job.Label]));
                }
            }
        }

        foreach (var pair in order)
        {
            var labels = pair.Value;
            string suffix;
            if (group.Jobs.Count > 1 && labels.Count == group.Jobs.Count)
            {
                suffix = " (in all " + labels.Count.ToString(CultureInfo.InvariantCulture) + " variants)";
            }
            else if (labels.Count == 1)
            {
                suffix = " (variant " + labels[0] + ")";
            }
            else
            {
                const int Shown = 3;
                string more = labels.Count > Shown ? "; and " + (labels.Count - Shown).ToString(CultureInfo.InvariantCulture) + " more" : "";
                suffix = " (variants " + string.Join("; ", labels.GetRange(0, Math.Min(Shown, labels.Count))) + more + ")";
            }

            Emit(pair.Key with { Text = pair.Key.Text + suffix });
        }
    }

    private void Emit(Msg m)
    {
        switch (m.Severity)
        {
            case SevError:
                Log.LogError(null, m.Code, null, m.File, m.Line, m.Column, 0, 0, m.Text);
                break;
            case SevWarning:
                Log.LogWarning(null, m.Code, null, m.File, m.Line, m.Column, 0, 0, m.Text);
                break;
            default:
                Log.LogMessage(MessageImportance.Normal, "{0}({1},{2}): info {3}: {4}", m.File, m.Line, m.Column, m.Code, m.Text);
                break;
        }
    }

    private string? Compile(Job job, string projectDir)
    {
        string full = job.Full;
        string relative = job.Relative;
        string outputPath = job.OutputPath;
        string optionsHash = HashOptions(relative, job.Namespace, job.ClassName, job.Defines, job.IncludeDirs, job.Entries, job.Targets, job.Enable16BitTypes);
        if (TryReuse(outputPath, optionsHash, job))
        {
            Interlocked.Increment(ref _upToDate);
            return outputPath;
        }

        Interlocked.Increment(ref _compiled);

        string text;
        try
        {
            text = File.ReadAllText(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            job.Messages.Add(new Msg(SevError, UnreadableCode, full, 0, 0, "Cannot read the shader: " + ex.Message));
            return null;
        }

        var includeDirs = job.IncludeDirs;
        var options = new CompileOptions
        {
            Defines = job.Defines,
            EntryPoints = job.Entries,
            Targets = job.Targets,
            Enable16BitTypes = job.Enable16BitTypes,
            IncludeResolver = (requested, including, isSystem) => ResolveInclude(requested, including, isSystem, projectDir, includeDirs),
        };

        CompileResult result;
        try
        {
            result = ShaderCompiler.Compile(text, relative, options, _cancel.Token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A compiler bug must not take the build down with a stack trace nobody can read.
            job.Messages.Add(new Msg(SevError, InternalErrorCode, full, 1, 1, "Internal compiler error (" + ex.GetType().Name + ": " + ex.Message + "). This is a bug in OneShader; please report it with this shader."));
            return null;
        }

        // The compile reports every file it read; the next build recompiles when any of them changed.
        var deps = new SortedSet<string>(StringComparer.Ordinal);
        for (int i = 1; i < result.Files.Count; i++)
        {
            deps.Add(Path.GetFullPath(result.Files[i], projectDir));
        }

        var warnings = new List<Diagnostic>();
        foreach (var d in result.Diagnostics)
        {
            job.Messages.Add(ToMsg(d, projectDir));
            if (d.Severity != DiagnosticSeverity.Error)
            {
                warnings.Add(d);
            }
        }

        if (!result.Success || result.Reflection is null)
        {
            // No output: the previous one (if any) describes an older version of the shader.
            TryDelete(outputPath);
            if (result.Success)
            {
                job.Messages.Add(new Msg(SevError, InternalErrorCode, full, 1, 1, "Internal compiler error: the compile succeeded but produced no reflection. This is a bug in OneShader; please report it with this shader."));
            }

            return null;
        }

        string content = Render(result.Reflection, result.Blobs, full, relative, job.Namespace, job.ClassName, optionsHash, deps, warnings, projectDir, job.VariantKey);
        WriteIfChanged(outputPath, content);
        return outputPath;
    }

    internal static string Render(
        ShaderReflection reflection,
        IReadOnlyList<ShaderBlob> blobs,
        string full,
        string relative,
        string ns,
        string className,
        string optionsHash,
        SortedSet<string> deps,
        List<Diagnostic> warnings,
        string projectDir,
        string variantKey = "")
    {
        var sb = new StringBuilder(ReflectionFormat.Write(reflection));
        sb.Append("generate namespace=").Append(Quote(ns)).Append(" class=").Append(Quote(className)).Append('\n');
        if (variantKey.Length > 0)
        {
            sb.Append("variantof key=").Append(variantKey).Append('\n');
        }

        foreach (var b in blobs)
        {
            sb.Append("blob entry=").Append(Quote(b.EntryPoint)).Append(" target=").Append(b.Target);
            if (b.Target == CompileTarget.Msl)
            {
                // What a host passes to newFunction(name:), and where a vertex function reads its attributes from.
                sb.Append(" function=").Append(Quote(Msl.MslBackend.EntryFunctionName(b.EntryPoint)));
                if (b.Stage == ShaderStage.Vertex && HasVertexInputs(reflection, b.EntryPoint))
                {
                    sb.Append(" vertexBuffer=").Append(Msl.MslBackend.VertexBufferIndex.ToString(CultureInfo.InvariantCulture));
                }
            }
            else if (b.Target == CompileTarget.Wgsl)
            {
                sb.Append(" function=").Append(Quote(Wgsl.WgslBackend.EntryFunctionName(b.EntryPoint)));
            }

            sb.Append(" data=").Append(Quote(Convert.ToBase64String(b.Data))).Append('\n');
        }

        // Bookkeeping for the next build. Paths are relative to the project, so the file is the
        // same on every machine.
        sb.Append("options hash=").Append(optionsHash).Append('\n');
        AppendDep(sb, relative, full);
        foreach (string d in deps)
        {
            if (!string.Equals(d, full, StringComparison.Ordinal))
            {
                AppendDep(sb, Relative(projectDir, d), d);
            }
        }

        foreach (var w in warnings)
        {
            sb.Append("diagnostic severity=").Append(w.Severity).Append(" code=").Append(w.Code)
              .Append(" file=").Append(Quote(w.Span.IsKnown ? w.Span.Path : "")).Append(" line=").Append(w.Span.Line.ToString(CultureInfo.InvariantCulture))
              .Append(" column=").Append(w.Span.Column.ToString(CultureInfo.InvariantCulture)).Append(" message=").Append(Quote(w.Message)).Append('\n');
        }

        return sb.ToString();
    }

    private static bool HasVertexInputs(ShaderReflection reflection, string entry)
    {
        foreach (var e in reflection.EntryPoints)
        {
            if (e.Name == entry)
            {
                return e.Inputs.Count > 0;
            }
        }

        return false;
    }

    private static void AppendDep(StringBuilder sb, string relative, string full)
    {
        sb.Append("dep file=").Append(Quote(relative)).Append(" sha=").Append(ContentHash(full)).Append('\n');
    }

    private static string ContentHash(string path)
    {
        try
        {
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "missing";
        }
    }

    /// <summary>
    /// True when the existing output was produced from the same options by this compiler and every
    /// input file still has the content it had then (compared by hash, not by file time); its
    /// stored warnings are then reported again.
    /// </summary>
    private bool TryReuse(string outputPath, string optionsHash, Job job)
    {
        if (!File.Exists(outputPath))
        {
            return false;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(outputPath);
        }
        catch (IOException)
        {
            return false;
        }

        string projectDir = Path.GetFullPath(ProjectDirectory);
        bool hashOk = false;
        var replay = new List<string>();
        foreach (string line in lines)
        {
            if (line.StartsWith("options hash=", StringComparison.Ordinal))
            {
                hashOk = string.Equals(line.Substring("options hash=".Length), optionsHash, StringComparison.Ordinal);
            }
            else if (line.StartsWith("dep ", StringComparison.Ordinal))
            {
                var a = ParseAttrs(line);
                if (!a.TryGetValue("file", out string? depFile) || !a.TryGetValue("sha", out string? sha)
                    || !string.Equals(ContentHash(Path.GetFullPath(depFile, projectDir)), sha, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            else if (line.StartsWith("diagnostic ", StringComparison.Ordinal))
            {
                replay.Add(line);
            }
        }

        if (!hashOk)
        {
            return false;
        }

        foreach (string line in replay)
        {
            job.Messages.Add(ReplayedMessage(line, projectDir));
        }

        return true;
    }

    private static Msg ReplayedMessage(string line, string projectDir)
    {
        var attrs = ParseAttrs(line);
        attrs.TryGetValue("code", out string? code);
        attrs.TryGetValue("message", out string? message);
        attrs.TryGetValue("file", out string? file);
        int.TryParse(attrs.TryGetValue("line", out string? l) ? l : "0", NumberStyles.None, CultureInfo.InvariantCulture, out int ln);
        int.TryParse(attrs.TryGetValue("column", out string? c) ? c : "0", NumberStyles.None, CultureInfo.InvariantCulture, out int col);
        string path = string.IsNullOrEmpty(file) ? "" : Path.GetFullPath(file!, projectDir);
        bool info = attrs.TryGetValue("severity", out string? sev) && sev == "Info";
        return new Msg(info ? SevInfo : SevWarning, code ?? "", path, ln, col, message ?? "");
    }

    private static Msg ToMsg(Diagnostic d, string projectDir)
    {
        string path = d.Span.IsKnown ? Path.GetFullPath(d.Span.Path, projectDir) : "";
        int line = d.Span.IsKnown ? d.Span.Line : 0;
        int col = d.Span.IsKnown ? Math.Max(d.Span.Column, 1) : 0;
        int severity = d.Severity switch { DiagnosticSeverity.Error => SevError, DiagnosticSeverity.Warning => SevWarning, _ => SevInfo };
        return new Msg(severity, d.Code, path, line, col, OneLine(d.Message));
    }

    private bool TryOptions(
        ITaskItem item,
        string full,
        string projectDir,
        out List<KeyValuePair<string, string?>> defines,
        out List<string> includeDirs,
        out List<EntryPointRequest> entries,
        out List<CompileTarget> targets)
    {
        defines = [];
        includeDirs = [];
        entries = [];
        targets = [];
        bool ok = true;
        string requested = item.GetMetadata("Targets").Length > 0 ? item.GetMetadata("Targets") : DefaultTargets;
        foreach (string t in Split(requested))
        {
            if (!Enum.TryParse<CompileTarget>(t, ignoreCase: true, out var target) || !Enum.IsDefined(target) || char.IsDigit(t[0]))
            {
                Log.LogError(null, BadTargetCode, null, full, 0, 0, 0, 0, "Unknown target '" + t + "' in OneShaderTargets (or the item's Targets). Use SpirV, Msl or Wgsl separated by ';', for example OneShaderTargets=\"SpirV;Msl\".");
                ok = false;
            }
            else if (!targets.Contains(target))
            {
                targets.Add(target);
            }
        }

        if (targets.Count == 0)
        {
            targets.Add(CompileTarget.SpirV);
        }

        targets.Sort();
        foreach (string d in Split(DefaultDefines + ";" + item.GetMetadata("Defines")))
        {
            int eq = d.IndexOf('=', StringComparison.Ordinal);
            defines.Add(eq < 0 ? new KeyValuePair<string, string?>(d, null) : new KeyValuePair<string, string?>(d.Substring(0, eq), d.Substring(eq + 1)));
        }

        foreach (string dir in Split(item.GetMetadata("IncludeDirectories") + ";" + DefaultIncludeDirectories))
        {
            includeDirs.Add(Path.GetFullPath(dir, projectDir));
        }

        foreach (string e in Split(item.GetMetadata("EntryPoints")))
        {
            int colon = e.LastIndexOf(':');
            if (colon <= 0 || !Enum.TryParse<ShaderStage>(e.Substring(colon + 1).Trim(), ignoreCase: true, out var stage) || !Enum.IsDefined(stage))
            {
                Log.LogError(null, BadMetadataCode, null, full, 0, 0, 0, 0, "Invalid EntryPoints entry '" + e + "'. Write Name:Stage with Stage one of Vertex, Pixel or Compute, for example EntryPoints=\"VSMain:Vertex;PSMain:Pixel\".");
                ok = false;
                continue;
            }

            entries.Add(new EntryPointRequest(e.Substring(0, colon).Trim(), stage));
        }

        return ok;
    }

    private static ResolvedInclude? ResolveInclude(
        string requested,
        string includingFile,
        bool isSystem,
        string projectDir,
        List<string> includeDirs)
    {
        var candidates = new List<string>();
        if (Path.IsPathRooted(requested))
        {
            candidates.Add(requested);
        }
        else
        {
            if (!isSystem)
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(includingFile, projectDir));
                if (dir is not null)
                {
                    candidates.Add(Path.Combine(dir, requested));
                }
            }

            foreach (string d in includeDirs)
            {
                candidates.Add(Path.Combine(d, requested));
            }
        }

        foreach (string c in candidates)
        {
            string path = Path.GetFullPath(c, projectDir);
            try
            {
                if (File.Exists(path))
                {
                    string text = File.ReadAllText(path);
                    return new ResolvedInclude(Relative(projectDir, path), text);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable counts as missing; the compiler reports the #include line.
            }
        }

        return null;
    }

    internal static string HashOptions(
        string relative,
        string ns,
        string className,
        List<KeyValuePair<string, string?>> defines,
        List<string> includeDirs,
        List<EntryPointRequest> entries,
        IReadOnlyList<CompileTarget>? targets = null,
        bool enable16BitTypes = false)
    {
        var sb = new StringBuilder();

        // Any change to the compiler build changes its module id, so a new compiler recompiles everything.
        sb.Append(typeof(ShaderCompiler).Assembly.ManifestModule.ModuleVersionId).Append('\n').Append(relative).Append('\n').Append(ns).Append('\n').Append(className).Append('\n');
        foreach (var d in defines)
        {
            sb.Append("D ").Append(d.Key).Append('=').Append(d.Value ?? "\u0001").Append('\n');
        }

        foreach (string i in includeDirs)
        {
            sb.Append("I ").Append(i).Append('\n');
        }

        foreach (var e in entries)
        {
            sb.Append("E ").Append(e.Name).Append(':').Append(e.Stage).Append('\n');
        }

        foreach (var t in targets ?? [CompileTarget.SpirV])
        {
            sb.Append("T ").Append(t).Append('\n');
        }

        if (enable16BitTypes)
        {
            sb.Append("16\n");
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private bool TryEnable16(ITaskItem item, string full, out bool value)
    {
        string text = Meta(item, "Enable16BitTypes", DefaultEnable16BitTypes).Trim();
        value = false;
        if (text.Length == 0 || text.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (text.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        Log.LogError(null, BadMetadataCode, null, full, 0, 0, 0, 0, "OneShaderEnable16BitTypes / Enable16BitTypes is '" + text + "'. Use true or false.");
        return false;
    }

    private static string Meta(ITaskItem item, string name, string fallback)
    {
        string v = item.GetMetadata(name);
        return v.Length > 0 ? v : fallback;
    }

    private static IEnumerable<string> Split(string value)
    {
        foreach (string part in value.Split(';'))
        {
            string t = part.Trim();
            if (t.Length > 0)
            {
                yield return t;
            }
        }
    }

    /// <summary>A path relative to the project with '/' separators, or the full path when it lies outside the project.</summary>
    private static string Relative(string projectDir, string full)
    {
        string rel = Path.GetRelativePath(projectDir, full).Replace('\\', '/');
        return Path.IsPathRooted(rel) ? full.Replace('\\', '/') : rel;
    }

    private static void WriteIfChanged(string path, string content)
    {
        byte[] bytes = new UTF8Encoding(false).GetBytes(content);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
        {
            // Same bytes: leave the file alone so the C# compile that follows can be skipped.
            return;
        }

        File.WriteAllBytes(path, bytes);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Left in place; the generator reports nothing for a file the build does not pass on.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }

    private static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ')
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.Append('"').ToString();
    }

    private static string Unquote(string s)
    {
        if (s.Length < 2 || s[0] != '"')
        {
            return s;
        }

        var sb = new StringBuilder();
        for (int i = 1; i < s.Length - 1; i++)
        {
            char c = s[i];
            if (c != '\\' || i + 1 >= s.Length - 1)
            {
                sb.Append(c);
                continue;
            }

            char e = s[++i];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (i + 4 < s.Length && int.TryParse(s.AsSpan(i + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int cp))
                    {
                        sb.Append((char)cp);
                        i += 4;
                    }

                    break;
                default: sb.Append(e); break;
            }
        }

        return sb.ToString();
    }

    private static Dictionary<string, string> ParseAttrs(string line)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        int i = line.IndexOf(' ', StringComparison.Ordinal);
        while (i >= 0 && i < line.Length)
        {
            while (i < line.Length && line[i] == ' ')
            {
                i++;
            }

            int eq = line.IndexOf('=', i);
            if (eq < 0)
            {
                break;
            }

            string key = line.Substring(i, eq - i);
            int vs = eq + 1;
            int ve = vs;
            if (vs < line.Length && line[vs] == '"')
            {
                ve = vs + 1;
                while (ve < line.Length && line[ve] != '"')
                {
                    ve += line[ve] == '\\' ? 2 : 1;
                }

                ve = Math.Min(ve + 1, line.Length);
            }
            else
            {
                while (ve < line.Length && line[ve] != ' ')
                {
                    ve++;
                }
            }

            result[key] = Unquote(line.Substring(vs, ve - vs));
            i = ve;
        }

        return result;
    }
}
