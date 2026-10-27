using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using OneShader.Diagnostics;
using OneShader.Types;

namespace OneShader.Tool;

/// <summary>
/// The command line, separated from <c>Main</c> so tests can drive it in-process. Exit codes: 0 success,
/// 1 the shader did not compile (or an output could not be written), 2 the command line is wrong.
/// </summary>
internal static class Cli
{
    public const int Ok = 0;
    public const int CompileFailed = 1;
    public const int UsageError = 2;

    private sealed class UsageException(string message) : Exception(message);

    private sealed class Settings
    {
        public string? Input;
        public string? Profile;
        public ShaderStage? Stage;
        public string? Entry;
        public bool AllEntries;
        public string? Output;
        public string? ReflectPath;
        public string? DepFile;
        public bool SpirvFlag;
        public readonly List<CompileTarget> Targets = [];
        public readonly List<KeyValuePair<string, string?>> Defines = [];
        public readonly List<string> Includes = [];
        public bool Optimize = true;
        public bool KeepNames = true;
        public bool Enable16Bit;
        public bool WarningsAsErrors;
        public MatrixPacking Packing = MatrixPacking.ColumnMajor;
    }

    public static string Version
    {
        get
        {
            string? v = typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(v))
            {
                return "0.0.0";
            }

            // Source Link appends "+<commit>"; the commit is not part of the version people compare.
            int plus = v.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? v : v[..plus];
        }
    }

    public static int Run(string[] args, Stream stdout, TextWriter stderr)
    {
        Settings s;
        try
        {
            var parsed = Parse(args, stdout);
            if (parsed is null)
            {
                return Ok; // --help or --version was printed
            }

            s = parsed;
        }
        catch (UsageException ex)
        {
            stderr.WriteLine("oneshader: error: " + ex.Message + " Run 'oneshader --help' for the options.");
            return UsageError;
        }

        return Compile(s, stdout, stderr);
    }

    private static Settings? Parse(string[] args, Stream stdout)
    {
        var s = new Settings();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.Length < 2 || a[0] != '-')
            {
                if (s.Input is not null)
                {
                    throw new UsageException("more than one input file ('" + s.Input + "' and '" + a + "'); compile one HLSL file per run.");
                }

                s.Input = a;
                continue;
            }

            // Value options, DXC style: "-T ps_6_0" or "-Tps_6_0". Longer prefixes first so -Fo is not read as -F.
            if (TryValue(args, ref i, "-Fo", out string? v))
            {
                s.Output = v;
            }
            else if (TryValue(args, ref i, "-HV", out v))
            {
                if (v is not ("2021" or "202x"))
                {
                    throw new UsageException("-HV " + v + " is not supported; oneshader implements HLSL 2021 (use -HV 2021).");
                }
            }
            else if (TryValue(args, ref i, "-T", out v))
            {
                s.Profile = v;
                s.Stage = ParseProfile(v);
            }
            else if (TryValue(args, ref i, "-E", out v))
            {
                s.Entry = v;
            }
            else if (TryValue(args, ref i, "-D", out v))
            {
                int eq = v.IndexOf('=', StringComparison.Ordinal);
                string name = eq < 0 ? v : v[..eq];
                if (name.Length == 0)
                {
                    throw new UsageException("-D needs a macro name (-D NAME or -D NAME=VALUE).");
                }

                s.Defines.Add(new KeyValuePair<string, string?>(name, eq < 0 ? null : v[(eq + 1)..]));
            }
            else if (TryValue(args, ref i, "-I", out v))
            {
                s.Includes.Add(v);
            }
            else if (a == "--target" || a.StartsWith("--target=", StringComparison.Ordinal))
            {
                string t = a == "--target" ? Next(args, ref i, a) : a["--target=".Length..];
                var target = t switch
                {
                    "spirv" => CompileTarget.SpirV,
                    "msl" => CompileTarget.Msl,
                    "wgsl" => CompileTarget.Wgsl,
                    _ => throw new UsageException("--target takes spirv, msl or wgsl, not '" + t + "'."),
                };
                if (!s.Targets.Contains(target))
                {
                    s.Targets.Add(target);
                }
            }
            else if (a == "--reflect")
            {
                s.ReflectPath = Next(args, ref i, a);
            }
            else if (a == "--depfile")
            {
                s.DepFile = Next(args, ref i, a);
            }
            else
            {
                switch (a)
                {
                    case "--help" or "-h" or "-help" or "-?":
                        WriteText(stdout, HelpText);
                        return null;
                    case "--version" or "-version":
                        WriteText(stdout, "oneshader " + Version + "\n");
                        return null;
                    case "-spirv":
                        s.SpirvFlag = true;
                        break;
                    case "--all-entries":
                        s.AllEntries = true;
                        break;
                    case "--no-names":
                        s.KeepNames = false;
                        break;
                    case "-O0" or "-Od":
                        s.Optimize = false;
                        break;
                    case "-O1" or "-O2" or "-O3":
                        s.Optimize = true;
                        break;
                    case "-Zpr":
                        s.Packing = MatrixPacking.RowMajor;
                        break;
                    case "-Zpc":
                        s.Packing = MatrixPacking.ColumnMajor;
                        break;
                    case "-WX":
                        s.WarningsAsErrors = true;
                        break;
                    case "-enable-16bit-types":
                        s.Enable16Bit = true;
                        break;
                    case "-nologo":
                        break;
                    default:
                        RejectOption(a);
                        break;
                }
            }
        }

        if (s.Input is null)
        {
            throw new UsageException("no input file. Give the HLSL file to compile.");
        }

        if (s.AllEntries && (s.Entry is not null || s.Profile is not null))
        {
            throw new UsageException("--all-entries compiles every [shader(...)] entry point and cannot be combined with -E or -T.");
        }

        if (s.Entry is not null && s.Stage is null)
        {
            throw new UsageException("-E needs -T <profile> (for example -T ps_6_0) so the stage is known.");
        }

        if (s.SpirvFlag && !s.Targets.Contains(CompileTarget.SpirV))
        {
            s.Targets.Insert(0, CompileTarget.SpirV);
        }

        if (s.Targets.Count == 0)
        {
            // Unlike DXC, whose default output is DXIL, there is only one sensible default here.
            s.Targets.Add(CompileTarget.SpirV);
        }

        return s;
    }

    private static void RejectOption(string a)
    {
        if (a.StartsWith("-fspv-target-env=", StringComparison.Ordinal))
        {
            string env = a["-fspv-target-env=".Length..];
            if (env is "vulkan1.2" or "vulkan1.3")
            {
                return; // the SPIR-V is valid for Vulkan 1.2 and everything newer
            }

            throw new UsageException("option '" + a + "' is not supported: oneshader emits SPIR-V for Vulkan 1.2 or newer (use -fspv-target-env=vulkan1.2).");
        }

        if (a.StartsWith("-fvk-", StringComparison.Ordinal) || a.StartsWith("-fspv-", StringComparison.Ordinal))
        {
            throw new UsageException("option '" + a + "' is not supported. oneshader always uses DXC's default Vulkan layout and binding rules; use [[vk::binding]], [[vk::location]] and [[vk::offset]] in the shader instead.");
        }

        if (a is "-Zi" or "-Qembed_debug" or "-Fd")
        {
            throw new UsageException("option '" + a + "' is not supported: debug info is not implemented. Remove it, or use --no-names to drop names from the output.");
        }

        if (a is "-Fh" or "-Fe" or "-Fc" or "-Fre" or "-Fsh" or "-Vn")
        {
            throw new UsageException("option '" + a + "' is not supported. Use -Fo for the output file and --reflect <file.json> for reflection.");
        }

        if (a.StartsWith("--", StringComparison.Ordinal) || a.StartsWith("-O", StringComparison.Ordinal))
        {
            throw new UsageException("unknown option '" + a + "'.");
        }

        throw new UsageException("unknown or unsupported option '" + a + "'.");
    }

    private static bool TryValue(string[] args, ref int i, string flag, out string value)
    {
        string a = args[i];
        if (a == flag)
        {
            value = Next(args, ref i, flag);
            return true;
        }

        if (a.StartsWith(flag, StringComparison.Ordinal) && !a.StartsWith("--", StringComparison.Ordinal) && a.Length > flag.Length)
        {
            value = a[flag.Length..];
            return true;
        }

        value = "";
        return false;
    }

    private static string Next(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length)
        {
            throw new UsageException("option '" + flag + "' needs a value.");
        }

        return args[++i];
    }

    private static ShaderStage? ParseProfileOrNull(string profile)
    {
        // vs_6_0 .. ps_6_9, cs_5_0: shader model 5 and 6 profiles for the three stages.
        if (profile.Length == 6 && profile[2] == '_' && profile[4] == '_' && profile[3] is '5' or '6' && char.IsAsciiDigit(profile[5]))
        {
            return profile[..2] switch
            {
                "vs" => ShaderStage.Vertex,
                "ps" => ShaderStage.Pixel,
                "cs" => ShaderStage.Compute,
                _ => null,
            };
        }

        return null;
    }

    private static ShaderStage ParseProfile(string profile)
    {
        var stage = ParseProfileOrNull(profile);
        if (stage is not null)
        {
            return stage.Value;
        }

        throw new UsageException("-T " + profile + " is not a supported profile. Use vs_6_x, ps_6_x or cs_6_x; geometry, tessellation, mesh and library profiles are not supported.");
    }

    private static int Compile(Settings s, Stream stdout, TextWriter stderr)
    {
        bool discover = s.Profile is null;
        var options = new CompileOptions
        {
            Targets = s.Targets,
            Defines = s.Defines,
            IncludeDirectories = s.Includes,
            EntryPoints = discover ? [] : [new EntryPointRequest(s.Entry ?? "main", s.Stage!.Value)],
            DefaultMatrixPacking = s.Packing,
            Enable16BitTypes = s.Enable16Bit,
            Optimize = s.Optimize,
            KeepNames = s.KeepNames,
        };

        var result = ShaderCompiler.CompileFile(s.Input!, options);
        bool warned = false;
        foreach (var d in result.Diagnostics)
        {
            stderr.WriteLine(d.ToString());
            warned |= d.Severity == DiagnosticSeverity.Warning;
        }

        if (!result.Success)
        {
            return CompileFailed;
        }

        if (warned && s.WarningsAsErrors)
        {
            stderr.WriteLine("oneshader: error: warnings were reported and -WX is set; fix them or remove -WX.");
            return CompileFailed;
        }

        if (result.Blobs.Count == 0)
        {
            stderr.WriteLine(s.Input + ": error: no entry point was compiled. Mark entry functions with [shader(\"vertex\"|\"pixel\"|\"compute\")], or pass -T <profile> and -E <name>.");
            return CompileFailed;
        }

        try
        {
            var outputs = new List<string>();
            WriteBlobs(s, result, stdout, outputs);
            if (s.ReflectPath is not null && result.Reflection is not null)
            {
                WriteFile(s.ReflectPath, Encoding.UTF8.GetBytes(ReflectionJson.Write(result.Reflection)));
                outputs.Add(s.ReflectPath);
            }

            if (s.DepFile is not null)
            {
                WriteFile(s.DepFile, Encoding.UTF8.GetBytes(DepFile.Format(outputs, result.Files)));
            }
        }
        catch (UsageException ex)
        {
            stderr.WriteLine("oneshader: error: " + ex.Message);
            return UsageError;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            stderr.WriteLine("oneshader: error: cannot write an output file: " + ex.Message + " Check the path and permissions.");
            return CompileFailed;
        }

        return Ok;
    }

    private static void WriteBlobs(Settings s, CompileResult result, Stream stdout, List<string> outputs)
    {
        if (s.Output is null)
        {
            if (s.ReflectPath is not null && s.DepFile is null)
            {
                return; // reflection only
            }

            if (result.Blobs.Count != 1)
            {
                throw new UsageException(result.Blobs.Count + " outputs were produced (entry points x targets); give -Fo <file> and they are written beside it, named by entry point or target.");
            }

            stdout.Write(result.Blobs[0].Data);
            stdout.Flush();
            return;
        }

        var entries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in result.Blobs)
        {
            entries.Add(b.EntryPoint);
        }

        string dir = Path.GetDirectoryName(s.Output) ?? "";
        string stem = Path.GetFileNameWithoutExtension(s.Output);
        string ext = Path.GetExtension(s.Output);
        foreach (var b in result.Blobs)
        {
            string path;
            if (result.Blobs.Count == 1)
            {
                path = s.Output;
            }
            else
            {
                string e = s.Targets.Count > 1 ? TargetExtension(b.Target) : ext;
                path = Path.Combine(dir, stem + (entries.Count > 1 ? "." + b.EntryPoint : "") + e);
            }

            WriteFile(path, b.ToArray());
            outputs.Add(path);
        }
    }

    private static string TargetExtension(CompileTarget t) => t switch
    {
        CompileTarget.Msl => ".metal",
        CompileTarget.Wgsl => ".wgsl",
        _ => ".spv",
    };

    private static void WriteFile(string path, byte[] data)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(path, data);
    }

    private static void WriteText(Stream stream, string text)
    {
        stream.Write(new UTF8Encoding(false).GetBytes(text));
        stream.Flush();
    }

    private const string HelpText = """
        oneshader - compile HLSL to SPIR-V, MSL and WGSL

        usage: oneshader [options] <file.hlsl>

        DXC-compatible options:
          -T <profile>       vs_6_x, ps_6_x or cs_6_x; the stage comes from the profile
          -E <name>          entry point (default with -T: main)
          -D NAME[=VALUE]    define a macro (without a value: 1)
          -I <dir>           add an #include search directory
          -Fo <file>         output file; with several outputs, names are derived from it
          -spirv             emit SPIR-V (the default target)
          -O0 | -Od          no optimization;  -O1 -O2 -O3: optimize (the default)
          -Zpr | -Zpc        row-major | column-major matrix packing (default column-major)
          -HV 2021           HLSL version (2021 is the only one implemented)
          -WX                treat warnings as errors
          -enable-16bit-types  half and min16* are 16-bit
          -fspv-target-env=vulkan1.2   accepted (output is valid for Vulkan 1.2 and newer)
          Other -fvk-* and -fspv-* options are rejected with a message.

        oneshader options:
          --target spirv|msl|wgsl   target to emit; repeat for several (several outputs)
          --all-entries      compile every [shader("...")] entry point (also the default without -T)
          --reflect <file>   write reflection as JSON
          --depfile <file>   write a Make-style dependency file (outputs: inputs read)
          --no-names         leave source names out of the output
          --version, --help

        Without -Fo a single output goes to standard output. Exit codes: 0 success,
        1 compile error, 2 command-line error. Diagnostics: file(line,col): error OSHnnnn: message.

        """;
}
