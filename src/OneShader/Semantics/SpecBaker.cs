using System;
using System.Collections.Generic;
using System.Globalization;
using OneShader.Diagnostics;
using OneShader.IR;
using OneShader.Reflection;
using OneShader.Types;

namespace OneShader.Semantics;

/// <summary>Specialization constants after lowering: fixing chosen ones at compile time, and keeping reflection in step with the optimized module.</summary>
internal static class SpecBaker
{
    /// <summary>
    /// Replaces the named constants by plain constants everywhere they are used and drops them from the module, so reflection and the
    /// backends no longer see them. Reports a value that does not fit the type as an error and an unknown name as a warning.
    /// </summary>
    public static void Bake(IrModule module, IReadOnlyList<KeyValuePair<string, double>> overrides, DiagnosticBag bag, string path)
    {
        var at = new SourceSpan(path, 1, 1);
        foreach (var (name, value) in overrides)
        {
            SpecConstant? found = null;
            foreach (var sc in module.SpecConstants)
            {
                if (sc.Name == name)
                {
                    found = sc;
                    break;
                }
            }

            if (found is null)
            {
                bag.Warning(SemanticCodes.BadSpecConstant, "The shader declares no specialization constant named '" + name + "', so the override is ignored. Check the spelling against the [[vk::constant_id]] declarations.", at);
                continue;
            }

            var kind = ((ScalarType)found.Type).ScalarKind;
            Constant? c = null;
            string v = value.ToString("R", CultureInfo.InvariantCulture);
            switch (kind)
            {
                case ScalarKind.Bool:
                    c = value is 0 or 1 ? Constant.Bool(value == 1) : null;
                    break;
                case ScalarKind.Int:
                    c = value == Math.Floor(value) && value >= int.MinValue && value <= int.MaxValue ? Constant.Int((int)value) : null;
                    break;
                case ScalarKind.UInt:
                    c = value == Math.Floor(value) && value >= 0 && value <= uint.MaxValue ? Constant.UInt((uint)value) : null;
                    break;
                default:
                    c = double.IsNaN(value) || Math.Abs(value) <= float.MaxValue || double.IsInfinity(value) ? Constant.Float((float)value) : null;
                    break;
            }

            if (c is null)
            {
                bag.Error(SemanticCodes.BadSpecConstant, "The value " + v + " does not fit the " + (kind == ScalarKind.Bool ? "bool" : kind == ScalarKind.Int ? "int" : kind == ScalarKind.UInt ? "uint" : "float")
                    + " specialization constant '" + name + "': " + (kind == ScalarKind.Bool ? "use 0 or 1." : "use a whole number in its range."), at);
                continue;
            }

            foreach (var f in module.Functions)
            {
                f.ReplaceUses(found, c);
            }

            module.SpecConstants.Remove(found);
        }
    }

    /// <summary>
    /// Returns reflection whose per-entry-point spec-constant lists match the optimized module: a constant used only by code the
    /// optimizer removed is not an input of the compiled shader (WebGPU rejects a key the shader does not declare).
    /// </summary>
    public static ShaderReflection? RefreshSpecUse(ShaderReflection? reflection, IrModule module)
    {
        if (reflection is null || module.SpecConstants.Count == 0 || reflection.EntryPoints.Count != module.EntryPoints.Count)
        {
            return reflection;
        }

        var entries = new List<EntryPointInfo>(reflection.EntryPoints.Count);
        for (int i = 0; i < reflection.EntryPoints.Count; i++)
        {
            var e = reflection.EntryPoints[i];
            var names = new List<string>();
            foreach (var sc in SpecUse.Scan(module, module.EntryPoints[i].Function))
            {
                names.Add(sc.Name!);
            }

            entries.Add(new EntryPointInfo
            {
                Name = e.Name,
                Stage = e.Stage,
                Inputs = e.Inputs,
                Outputs = e.Outputs,
                WorkgroupSize = e.WorkgroupSize,
                VertexStride = e.VertexStride,
                UsedResources = e.UsedResources,
                PushConstant = e.PushConstant,
                UsedSpecConstants = names,
                SubgroupOperations = e.SubgroupOperations,
            });
        }

        return new ShaderReflection
        {
            SourceName = reflection.SourceName,
            Targets = reflection.Targets,
            Resources = reflection.Resources,
            PushConstants = reflection.PushConstants,
            SpecConstants = reflection.SpecConstants,
            EntryPoints = entries,
        };
    }
}
