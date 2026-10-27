using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OneShader.Layouts;
using OneShader.Reflection;
using OneShader.Types;

namespace OneShader.ParityTests;

/// <summary>
/// Turns a case's <c>spec NAME : VALUE</c> lines into the values each entry point's pipeline gets, through the library's own
/// <see cref="SpecializationValues"/>, so the bytes the harness passes are the bytes a host built from reflection would pass.
/// </summary>
public static class SpecOverrides
{
    private static readonly ConcurrentDictionary<string, ShaderReflection> Reflections = new();

    /// <summary>The values for one entry point, or null when the case sets none that the entry point reads.</summary>
    public static SpecializationValues? For(CaseModel model, ShaderReflection reflection, string entry)
    {
        if (model.Specs.Count == 0 || !EntryLayout.TryCreate(reflection, entry, out var layout, out _) || layout is null || layout.SpecConstants.Count == 0)
        {
            return null;
        }

        var values = SpecializationValues.For(layout);
        foreach (var (name, text) in model.Specs)
        {
            var c = layout.SpecConstants.FirstOrDefault(x => x.Name == name);
            if (c.Name is null)
            {
                continue;
            }

            switch (c.Type)
            {
                case ScalarKind.Bool: values.Set(name, text is "true" or "1"); break;
                case ScalarKind.Int: values.Set(name, int.Parse(text, CultureInfo.InvariantCulture)); break;
                case ScalarKind.UInt: values.Set(name, uint.Parse(text, CultureInfo.InvariantCulture)); break;
                default: values.Set(name, float.Parse(text, CultureInfo.InvariantCulture)); break;
            }
        }

        return values;
    }

    /// <summary>The reflection of the case's shader as this compiler reports it (SPIR-V only), cached.</summary>
    public static ShaderReflection ReflectionOf(CaseModel model, string source)
    {
        string key = source + "|" + string.Join(';', model.Defines.Select(d => d.Key + "=" + d.Value));
        return Reflections.GetOrAdd(key, _ =>
        {
            var result = ShaderCompiler.Compile(File.ReadAllText(source), source, new CompileOptions
            {
                Targets = [CompileTarget.SpirV],
                Defines = model.Defines.Select(d => new KeyValuePair<string, string?>(d.Key, d.Value)).ToList(),
                IncludeDirectories = [Path.GetDirectoryName(source)!],
            });
            return result.Reflection ?? throw new InvalidOperationException("no reflection for " + source + ":\n" + string.Join('\n', result.Diagnostics.Select(d => d.ToString())));
        });
    }

    /// <summary>A lookup for the Vulkan runner; null when the case has no overrides.</summary>
    public static Func<string, OneShader.Layouts.Vulkan.SpecializationInfo?>? VulkanFor(CaseModel model, string source)
    {
        if (model.Specs.Count == 0)
        {
            return null;
        }

        var reflection = ReflectionOf(model, source);
        return entry => For(model, reflection, entry)?.ToVulkan();
    }
}
