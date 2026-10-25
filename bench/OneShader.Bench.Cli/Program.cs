using System;
using System.Collections.Generic;
using System.IO;
using OneShader;

// oshc <file.hlsl> -E <entry> -T vs|ps|cs [-t spirv|msl|wgsl] [--no-opt] [--no-names] [-D NAME[=VALUE]] [-I dir] -o <out>
//
// Compiles one entry point of one file, like `dxc -T -E -Fo`, and writes the blob. Exit code 0 on success, 1 on a
// compile error (diagnostics on stderr), 2 on a bad command line.

string? file = null;
string? entry = null;
string? output = null;
var stage = ShaderStage.Vertex;
bool haveStage = false;
var target = CompileTarget.SpirV;
bool optimize = true;
bool keepNames = true;
var defines = new List<KeyValuePair<string, string?>>();
var includes = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("missing value after " + a);
    try
    {
        switch (a)
        {
            case "-E": entry = Next(); break;
            case "-o": output = Next(); break;
            case "-I": includes.Add(Next()); break;
            case "-T":
                stage = Next() switch { "vs" => ShaderStage.Vertex, "ps" => ShaderStage.Pixel, "cs" => ShaderStage.Compute, _ => throw new ArgumentException("-T takes vs, ps or cs") };
                haveStage = true;
                break;
            case "-t":
                target = Next() switch { "spirv" => CompileTarget.SpirV, "msl" => CompileTarget.Msl, "wgsl" => CompileTarget.Wgsl, _ => throw new ArgumentException("-t takes spirv, msl or wgsl") };
                break;
            case "-D":
                {
                    string d = Next();
                    int eq = d.IndexOf('=', StringComparison.Ordinal);
                    defines.Add(eq < 0 ? new(d, null) : new(d[..eq], d[(eq + 1)..]));
                    break;
                }
            case "--no-opt": optimize = false; break;
            case "--no-names": keepNames = false; break;
            default:
                if (a.StartsWith('-'))
                {
                    throw new ArgumentException("unknown option " + a);
                }
                file = a;
                break;
        }
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine("oshc: " + ex.Message);
        return 2;
    }
}

if (file is null || entry is null || output is null || !haveStage)
{
    Console.Error.WriteLine("usage: oshc <file.hlsl> -E <entry> -T vs|ps|cs [-t spirv|msl|wgsl] [--no-opt] [--no-names] [-D N[=V]] [-I dir] -o <out>");
    return 2;
}

var options = new CompileOptions
{
    Targets = [target],
    Defines = defines,
    IncludeDirectories = includes,
    EntryPoints = [new EntryPointRequest(entry, stage)],
    Optimize = optimize,
    KeepNames = keepNames,
};
var result = ShaderCompiler.CompileFile(file, options);
var blob = result.Success ? result.Find(entry, target) : null;
if (blob is null)
{
    foreach (var d in result.Diagnostics)
    {
        Console.Error.WriteLine(d.ToString());
    }
    return 1;
}

File.WriteAllBytes(output, blob.ToArray());
return 0;
