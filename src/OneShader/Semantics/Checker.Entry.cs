using System;
using System.Collections.Generic;
using System.Globalization;
using OneShader.Diagnostics;
using OneShader.IR;
using OneShader.Reflection;
using OneShader.Syntax;
using OneShader.Types;

namespace OneShader.Semantics;

/// <summary>One stage input or output variable, flattened out of the entry function's parameters and return value.</summary>
internal sealed class InterfaceVar
{
    public required string Name { get; init; }

    public required string Semantic { get; init; }

    public string SemanticName { get; init; } = "";

    public uint SemanticIndex { get; init; }

    public required HlslType Type { get; init; }

    public BuiltIn BuiltIn { get; set; }

    public int? Location { get; set; }

    public Interpolation Interpolation { get; init; }

    public string? FormatName { get; init; }

    public VertexFormat? Format { get; set; }

    public uint Offset { get; set; }

    /// <summary>-1 for the return value, otherwise the parameter index.</summary>
    public int ParamIndex { get; init; }

    public List<int> Path { get; init; } = [];

    public SourceSpan Span { get; init; }

    public int? ExplicitLocation { get; init; }

    public GlobalVariable? Global { get; set; }
}

internal sealed class EntryInfo
{
    public required FunctionSymbol Function { get; init; }

    public required ShaderStage Stage { get; init; }

    public List<InterfaceVar> Inputs { get; } = [];

    public List<InterfaceVar> Outputs { get; } = [];

    public (uint X, uint Y, uint Z) NumThreads { get; set; }

    public bool WritesDepth { get; set; }

    public uint VertexStride { get; set; }
}

internal sealed partial class Checker
{
    private static readonly Dictionary<string, VertexFormat> VertexFormats = BuildFormats();

    private static Dictionary<string, VertexFormat> BuildFormats()
    {
        var d = new Dictionary<string, VertexFormat>(StringComparer.Ordinal);
        foreach (var f in Enum.GetValues<VertexFormat>())
        {
            d[f.ToString().ToLowerInvariant()] = f;
        }

        return d;
    }

    private static int FormatBytes(VertexFormat f) => f switch
    {
        VertexFormat.Float32 or VertexFormat.Sint32 or VertexFormat.Uint32 or VertexFormat.Float16x2 or VertexFormat.Unorm8x4
            or VertexFormat.Snorm8x4 or VertexFormat.Uint8x4 or VertexFormat.Sint8x4 or VertexFormat.Unorm16x2 or VertexFormat.Snorm16x2
            or VertexFormat.Uint16x2 or VertexFormat.Sint16x2 => 4,
        VertexFormat.Float32x2 or VertexFormat.Sint32x2 or VertexFormat.Uint32x2 or VertexFormat.Float16x4 or VertexFormat.Unorm16x4
            or VertexFormat.Snorm16x4 or VertexFormat.Uint16x4 or VertexFormat.Sint16x4 => 8,
        VertexFormat.Float32x3 or VertexFormat.Sint32x3 or VertexFormat.Uint32x3 => 12,
        _ => 16,
    };

    private static int FormatComponents(VertexFormat f) => f switch
    {
        VertexFormat.Float32 or VertexFormat.Sint32 or VertexFormat.Uint32 => 1,
        VertexFormat.Float32x2 or VertexFormat.Sint32x2 or VertexFormat.Uint32x2 or VertexFormat.Float16x2 or VertexFormat.Unorm16x2
            or VertexFormat.Snorm16x2 or VertexFormat.Uint16x2 or VertexFormat.Sint16x2 => 2,
        VertexFormat.Float32x3 or VertexFormat.Sint32x3 or VertexFormat.Uint32x3 => 3,
        _ => 4,
    };

    private static bool FormatIsInteger(VertexFormat f) => f.ToString().StartsWith("Sint", StringComparison.Ordinal)
        || f.ToString().StartsWith("Uint", StringComparison.Ordinal);

    private static VertexFormat? DefaultFormat(ScalarType s, int n)
    {
        int i = n - 1;
        if (s.IsFloatingPoint && TypeOps.BitWidth(s) == 32)
        {
            return (VertexFormat)((int)VertexFormat.Float32 + i);
        }

        if (s.ScalarKind == ScalarKind.Int)
        {
            return (VertexFormat)((int)VertexFormat.Sint32 + i);
        }

        if (s.ScalarKind == ScalarKind.UInt)
        {
            return (VertexFormat)((int)VertexFormat.Uint32 + i);
        }

        return null;
    }

    // ------------------------------------------------------------ discovery

    private void FindEntryPoints()
    {
        if (_opt.EntryPoints.Count > 0)
        {
            foreach (var req in _opt.EntryPoints)
            {
                FunctionSymbol? found = null;
                foreach (var f in Functions)
                {
                    if (f.Name == req.Name)
                    {
                        found = f;
                        break;
                    }
                }

                if (found is null)
                {
                    Err(SemanticCodes.BadEntryPoint, "The entry point '" + req.Name + "' was not found; define a function with that name.", new SourceSpan(_file, 1, 1));
                    continue;
                }

                BuildEntry(found, req.Stage);
            }

            return;
        }

        foreach (var f in Functions)
        {
            if (f.EntryStage is { } stage)
            {
                BuildEntry(f, stage);
            }
        }
    }

    private static (string Name, uint Index) SplitSemantic(string text)
    {
        int end = text.Length;
        while (end > 0 && text[end - 1] is >= '0' and <= '9')
        {
            end--;
        }

        if (end == text.Length || end == 0)
        {
            return (text, 0);
        }

        uint.TryParse(text.AsSpan(end), NumberStyles.None, CultureInfo.InvariantCulture, out uint idx);
        return (text.Substring(0, end), idx);
    }

    private void BuildEntry(FunctionSymbol f, ShaderStage stage)
    {
        var entry = new EntryInfo { Function = f, Stage = stage };
        var d = f.Decl;
        var nameSpan = d.Name.Span;
        if (f.ReturnType.IsError)
        {
            return;
        }

        if (stage == ShaderStage.Compute)
        {
            var nt = FindAttribute(d.Attributes, "numthreads");
            if (nt is null)
            {
                Err(SemanticCodes.BadEntryPoint, "The compute shader '" + f.Name + "' needs a thread group size: add [numthreads(x, y, z)] above it.", nameSpan);
            }
            else
            {
                uint[] n = [1, 1, 1];
                for (int i = 0; i < 3; i++)
                {
                    if (i < nt.Arguments.Count)
                    {
                        int? v = IntAttributeArg(nt, i);
                        if (v is null or <= 0 or > 1024)
                        {
                            if (v is not null)
                            {
                                Err(SemanticCodes.BadEntryPoint, "numthreads values must be between 1 and 1024.", nt.Arguments[i].Span);
                            }
                        }
                        else
                        {
                            n[i] = (uint)v.Value;
                        }
                    }
                }

                entry.NumThreads = (n[0], n[1], n[2]);
            }

            if (!f.ReturnType.IsVoid)
            {
                Err(SemanticCodes.BadEntryPoint, "A compute shader must return void.", nameSpan);
            }
        }

        // Parameters.
        for (int i = 0; i < f.Params.Count; i++)
        {
            var p = f.Params[i];
            if (p.Type.IsError)
            {
                continue;
            }

            var pd = p.Decl!;
            string? sem = null;
            foreach (var a in pd.Annotations)
            {
                if (a is SemanticAnnotation sa)
                {
                    sem = sa.Text;
                }
            }

            int? loc = FindAttribute(pd.Attributes, "vk::location") is { } la ? IntAttributeArg(la, 0) : null;
            string? fmt = FindAttribute(pd.Attributes, "oneshader::format") is { } fa ? StringAttributeArg(fa, 0) : null;
            if (p.IsIn)
            {
                Flatten(entry, entry.Inputs, p.Type, p.Name, sem, [], i, false, InterpolationOf(p.Mods), loc, fmt, pd.Span, 0);
            }

            if (p.IsOut)
            {
                Flatten(entry, entry.Outputs, p.Type, p.Name, sem, [], i, true, InterpolationOf(p.Mods), null, null, pd.Span, 0);
            }
        }

        if (!f.ReturnType.IsVoid)
        {
            string? sem = null;
            foreach (var a in d.ReturnAnnotations)
            {
                if (a is SemanticAnnotation sa)
                {
                    sem = sa.Text;
                }
            }

            Flatten(entry, entry.Outputs, f.ReturnType, "", sem, [], -1, true, Interpolation.Default, null, null, nameSpan, 0);
        }

        ClassifyAndAssign(entry);
        Entries.Add(entry);
    }

    private void Flatten(EntryInfo entry, List<InterfaceVar> into, HlslType type, string name, string? semantic, List<int> path, int paramIndex, bool isOutput, Interpolation interp, int? location, string? format, SourceSpan span, int depth)
    {
        if (depth > 8)
        {
            Err(SemanticCodes.BadSemantic, "Entry point structs are nested too deeply.", span);
            return;
        }

        if (type is StructType st)
        {
            if (semantic is not null)
            {
                Err(SemanticCodes.BadSemantic, "A semantic cannot be attached to the struct '" + st.Name + "'; put semantics on its members.", span);
            }

            for (int i = 0; i < st.Fields.Count; i++)
            {
                var f = st.Fields[i];
                var p2 = new List<int>(path) { i };
                string n = name.Length == 0 ? f.Name : name + "." + f.Name;
                var fspan = _fieldSpans.TryGetValue(f, out var fs) ? fs : span;
                Flatten(entry, into, f.Type, n, f.Semantic, p2, paramIndex, isOutput, f.Interpolation != Interpolation.Default ? f.Interpolation : interp, f.Location ?? location, f.VertexFormat ?? format, fspan, depth + 1);
            }

            return;
        }

        if (semantic is null)
        {
            string what = name.Length == 0 ? "The return value of '" + entry.Function.Name + "'" : "'" + name + "'";
            Err(SemanticCodes.BadSemantic, what + " has no semantic. Add one after its name, for example ': TEXCOORD0'" + (isOutput && entry.Stage == ShaderStage.Pixel ? " or ': SV_Target'" : "") + ".", span);
            return;
        }

        if (type is not (ScalarType or VectorType))
        {
            Err(SemanticCodes.BadSemantic, "'" + (name.Length == 0 ? entry.Function.Name : name) + "' has type '" + type + "', which cannot be a shader input or output; use scalars and vectors (split matrices into rows).", span);
            return;
        }

        var (sn, si) = SplitSemantic(semantic);
        into.Add(new InterfaceVar
        {
            Name = name.Length == 0 ? "result" : name,
            Semantic = semantic,
            SemanticName = sn,
            SemanticIndex = si,
            Type = type,
            Interpolation = interp,
            FormatName = format,
            ParamIndex = paramIndex,
            Path = path,
            Span = span,
            ExplicitLocation = location,
        });
    }

    private void ClassifyAndAssign(EntryInfo e)
    {
        var stage = e.Stage;
        string stageName = stage.ToString().ToLowerInvariant();
        foreach (bool outputs in new[] { false, true })
        {
            var list = outputs ? e.Outputs : e.Inputs;
            var taken = new HashSet<int>();
            foreach (var v in list)
            {
                if (v.ExplicitLocation is { } el)
                {
                    taken.Add(el);
                }
            }

            int next = 0;
            foreach (var v in list)
            {
                var bi = BuiltInFor(v, stage, outputs, out bool valid, out int? targetIndex);
                if (!valid)
                {
                    Err(SemanticCodes.BadSemantic, "The semantic '" + v.Semantic + "' of '" + v.Name + "' is not valid as a " + stageName + " shader " + (outputs ? "output" : "input") + ".", v.Span);
                    continue;
                }

                v.BuiltIn = bi;
                if (bi != BuiltIn.None)
                {
                    if (bi == BuiltIn.FragDepth)
                    {
                        e.WritesDepth = true;
                    }

                    continue;
                }

                if (targetIndex is { } ti)
                {
                    v.Location = ti;
                    continue;
                }

                if (v.ExplicitLocation is { } loc)
                {
                    v.Location = loc;
                    continue;
                }

                while (taken.Contains(next))
                {
                    next++;
                }

                v.Location = next;
                taken.Add(next);
                next++;
            }
        }

        if (stage == ShaderStage.Vertex)
        {
            bool hasPosition = false;
            foreach (var o in e.Outputs)
            {
                if (o.BuiltIn == BuiltIn.Position)
                {
                    hasPosition = true;
                }
            }

            if (!hasPosition)
            {
                Err(SemanticCodes.BadEntryPoint, "The vertex shader '" + e.Function.Name + "' does not output SV_Position. Add a 'float4 Position : SV_Position' member to its output struct (or return a float4 with ': SV_Position').", e.Function.Decl.Name.Span);
            }

            AssignVertexFormats(e);
        }
        else if (stage == ShaderStage.Compute)
        {
            if (e.Outputs.Count > 0)
            {
                Err(SemanticCodes.BadEntryPoint, "A compute shader cannot have outputs; write results to a resource.", e.Function.Decl.Name.Span);
            }
        }
    }

    private static BuiltIn BuiltInFor(InterfaceVar v, ShaderStage stage, bool output, out bool valid, out int? targetIndex)
    {
        valid = true;
        targetIndex = null;
        string n = v.SemanticName;
        if (!n.StartsWith("SV_", StringComparison.OrdinalIgnoreCase))
        {
            valid = !(stage == ShaderStage.Pixel && output) && stage != ShaderStage.Compute;
            return BuiltIn.None;
        }

        string key = n.Substring(3).ToLowerInvariant();
        switch (key)
        {
            case "position":
                valid = (stage == ShaderStage.Vertex && output) || (stage == ShaderStage.Pixel && !output);
                return output ? BuiltIn.Position : BuiltIn.FragCoord;
            case "vertexid":
                valid = stage == ShaderStage.Vertex && !output;
                return BuiltIn.VertexIndex;
            case "instanceid":
                valid = stage == ShaderStage.Vertex && !output;
                return BuiltIn.InstanceIndex;
            case "isfrontface":
                valid = stage == ShaderStage.Pixel && !output;
                return BuiltIn.FrontFacing;
            case "sampleindex":
                valid = stage == ShaderStage.Pixel && !output;
                return BuiltIn.SampleIndex;
            case "coverage":
                valid = stage == ShaderStage.Pixel;
                return BuiltIn.SampleMask;
            case "primitiveid":
                valid = stage == ShaderStage.Pixel && !output;
                return BuiltIn.PrimitiveIndex;
            case "depth" or "depthgreaterequal" or "depthlessequal":
                valid = stage == ShaderStage.Pixel && output;
                return BuiltIn.FragDepth;
            case "rendertargetarrayindex":
                valid = (stage == ShaderStage.Vertex && output) || (stage == ShaderStage.Pixel && !output);
                return BuiltIn.Layer;
            case "viewportarrayindex":
                valid = (stage == ShaderStage.Vertex && output) || (stage == ShaderStage.Pixel && !output);
                return BuiltIn.ViewportIndex;
            case "clipdistance":
                valid = stage == ShaderStage.Vertex && output;
                return BuiltIn.ClipDistance;
            case "culldistance":
                valid = stage == ShaderStage.Vertex && output;
                return BuiltIn.CullDistance;
            case "target":
                valid = stage == ShaderStage.Pixel && output;
                targetIndex = (int)v.SemanticIndex;
                return BuiltIn.None;
            case "dispatchthreadid":
                valid = stage == ShaderStage.Compute && !output;
                return BuiltIn.GlobalInvocationId;
            case "groupthreadid":
                valid = stage == ShaderStage.Compute && !output;
                return BuiltIn.LocalInvocationId;
            case "groupid":
                valid = stage == ShaderStage.Compute && !output;
                return BuiltIn.WorkgroupId;
            case "groupindex":
                valid = stage == ShaderStage.Compute && !output;
                return BuiltIn.LocalInvocationIndex;
            default:
                valid = false;
                return BuiltIn.None;
        }
    }

    private void AssignVertexFormats(EntryInfo e)
    {
        uint cursor = 0;
        foreach (var v in e.Inputs)
        {
            if (v.BuiltIn != BuiltIn.None)
            {
                continue;
            }

            var s = v.Type.ScalarElement!;
            int n = TypeOps.Count(v.Type);
            VertexFormat? fmt;
            if (v.FormatName is { } fn)
            {
                if (!VertexFormats.TryGetValue(fn.ToLowerInvariant(), out var f))
                {
                    Err(SemanticCodes.BadAttribute, "'" + fn + "' is not a vertex format. Use a name such as float32x4, unorm8x4, uint8x4, snorm16x2 or float16x2.", v.Span);
                    continue;
                }

                fmt = f;
                if (FormatComponents(f) != n)
                {
                    Err(SemanticCodes.BadAttribute, "The vertex format '" + fn + "' has " + Num(FormatComponents(f)) + " components but '" + v.Name + "' is declared '" + v.Type + "'.", v.Span);
                    continue;
                }

                if (FormatIsInteger(f) != s.IsInteger && !(FormatIsInteger(f) && s.IsInteger))
                {
                    Err(SemanticCodes.BadAttribute, "The vertex format '" + fn + "' delivers " + (FormatIsInteger(f) ? "integers" : "floating-point values") + " but '" + v.Name + "' is declared '" + v.Type + "'.", v.Span);
                    continue;
                }
            }
            else
            {
                fmt = DefaultFormat(s, n);
                if (fmt is null)
                {
                    Err(SemanticCodes.BadSemantic, "'" + v.Name + "' has type '" + v.Type + "', which has no vertex format; use float, int or uint vectors, or name a format with [[oneshader::format(\"...\")]].", v.Span);
                    continue;
                }
            }

            v.Format = fmt;
            v.Offset = cursor;
            cursor += (uint)FormatBytes(fmt.Value);
        }

        e.VertexStride = cursor;
    }

    // ------------------------------------------------------------ binding assignment

    private void AssignBindings()
    {
        var owners = new Dictionary<(int, int), ResourceDecl>();
        var explicitDone = new List<ResourceDecl>();
        foreach (var r in Resources)
        {
            if (r.ExplicitBinding is not { } b)
            {
                continue;
            }

            r.Set = r.ExplicitSet ?? 0;
            r.Binding = b;
            explicitDone.Add(r);
            int span = Math.Max(1, r.Count);
            ResourceDecl? clash = null;
            for (int k = 0; k < span; k++)
            {
                if (owners.TryGetValue((r.Set, b + k), out var other))
                {
                    clash = other;
                    break;
                }
            }

            if (clash is not null)
            {
                Err(SemanticCodes.BindingCollision, "The resources '" + clash.Name + "' and '" + r.Name + "' both use set " + Num(r.Set) + ", binding " + Num(b)
                    + ". Add [[vk::binding(binding, set)]] to one of them, or give them different register numbers (t, s, b and u registers share binding numbers on Vulkan).", r.Span);
                continue;
            }

            for (int k = 0; k < span; k++)
            {
                owners[(r.Set, b + k)] = r;
            }
        }

        int next = 0;
        foreach (var r in Resources)
        {
            if (r.ExplicitBinding is not null)
            {
                continue;
            }

            int span = Math.Max(1, r.Count);
            while (true)
            {
                bool free = true;
                for (int k = 0; k < span; k++)
                {
                    if (owners.ContainsKey((0, next + k)))
                    {
                        free = false;
                        break;
                    }
                }

                if (free)
                {
                    break;
                }

                next++;
            }

            r.Set = 0;
            r.Binding = next;
            for (int k = 0; k < span; k++)
            {
                owners[(0, next + k)] = r;
            }

            next += span;
        }
    }
}
