using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;

namespace OneShader.ParityTests;

/// <summary>This repository's own compiler, the candidate in every parity comparison.</summary>
public static class OneShaderCompiler
{
    /// <summary>Creates the compiler; results are cached per entry point so a case does not compile twice.</summary>
    public static ParityCompiler Create(string name)
    {
        var cache = new ConcurrentDictionary<string, byte[]>();
        return new ParityCompiler(name, request =>
        {
            string key = request.SourcePath + "|" + request.Entry + "|" + request.Stage + "|"
                + string.Join(';', request.Defines.Select(d => d.Key + "=" + d.Value));
            return cache.GetOrAdd(key, _ => Compile(request));
        });
    }

    private static byte[] Compile(ShaderRequest request)
    {
        var stage = request.Stage switch
        {
            ShaderStage.Vertex => OneShader.ShaderStage.Vertex,
            ShaderStage.Pixel => OneShader.ShaderStage.Pixel,
            _ => OneShader.ShaderStage.Compute,
        };
        var options = new CompileOptions
        {
            Defines = request.Defines.Select(d => new System.Collections.Generic.KeyValuePair<string, string?>(d.Key, d.Value)).ToList(),
            IncludeDirectories = [Path.GetDirectoryName(request.SourcePath)!],
            EntryPoints = [new EntryPointRequest(request.Entry, stage)],
        };
        var result = ShaderCompiler.Compile(File.ReadAllText(request.SourcePath), request.SourcePath, options);
        var blob = result.Find(request.Entry, CompileTarget.SpirV);
        if (!result.Success || blob is null)
        {
            throw new InvalidOperationException("OneShader failed for " + request.Entry + " (" + request.Stage + ") in " + request.SourcePath + ":\n"
                + string.Join('\n', result.Diagnostics.Select(d => d.ToString())));
        }

        return blob.ToArray();
    }
}
