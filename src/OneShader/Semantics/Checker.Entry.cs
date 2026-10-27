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

        // 16-bit attributes exist as two- and four-component formats only.
        if (n is 2 or 4 && TypeOps.BitWidth(s) == 16)
        {
            if (s.IsFloatingPoint)
            {
                return n == 2 ? VertexFormat.Float16x2 : VertexFormat.Float16x4;
            }

            if (s.IsSignedInteger)
            {
                return n == 2 ? VertexFormat.Sint16x2 : VertexFormat.Sint16x4;
            }

            return n == 2 ? VertexFormat.Uint16x2 : VertexFormat.Uint16x4;
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

        // Checking an entry point can instantiate templates, which adds to Functions: walk a snapshot.
        foreach (var f in (FunctionSymbol[])[.. Functions])
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
        _interfaceOverflow = false;
        var entry = new EntryInfo { Function = f, Stage = stage };
        var d = f.Decl;
        var nameSpan = d.Name.Span;
        if (f.ReturnType.IsError)
        {
            return;
        }

        foreach (var p in f.Params)
        {
            if (p.Decl is null)
            {
                // The implicit 'this' of a member function has no declaration.
                Err(SemanticCodes.BadEntryPoint, "The entry point '" + f.Name + "' is a member function. Move it out of the struct and make it a free function.", nameSpan);
                return;
            }
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

    /// <summary>Most stage variables one entry point may declare after arrays and matrices are expanded (devices offer a few dozen locations).</summary>
    private const int MaxInterfaceVars = 1024;

    private bool _interfaceOverflow;

    /// <summary>Explicit stage-variable locations must be below this; devices offer a few dozen.</summary>
    private const int MaxLocation = 4096;

    private void Flatten(EntryInfo entry, List<InterfaceVar> into, HlslType type, string name, string? semantic, List<int> path, int paramIndex, bool isOutput, Interpolation interp, int? location, string? format, SourceSpan span, int depth)
    {
        if (_interfaceOverflow || type.IsError)
        {
            // An error was already reported for the declaration; do not add a second one about its type.
            return;
        }

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

        if (into.Count >= MaxInterfaceVars)
        {
            _interfaceOverflow = true;
            Err(SemanticCodes.BadSemantic, "'" + (name.Length == 0 ? entry.Function.Name : name) + "' and the other stage variables expand to more than " + Num(MaxInterfaceVars) + " inputs or outputs (arrays and matrices count once per element or row). Shrink the arrays.", span);
            return;
        }

        var (sn, si) = SplitSemantic(semantic);
        bool distance = sn.Equals("SV_ClipDistance", StringComparison.OrdinalIgnoreCase) || sn.Equals("SV_CullDistance", StringComparison.OrdinalIgnoreCase);

        // Arrays and matrices take one location per element or row, with the semantic index counting up (TEXCOORD1 on a
        // float3x3 covers TEXCOORD1 to TEXCOORD3), as DXC lays them out. Clip and cull distances keep their index and
        // are merged into one built-in array later, so their elements stay in order.
        if (type is ArrayType { Length: > 0 } arr)
        {
            for (int i = 0; i < arr.Length; i++)
            {
                string es = distance ? semantic : sn + (si + (uint)i).ToString(CultureInfo.InvariantCulture);
                var p2 = new List<int>(path) { i };
                Flatten(entry, into, arr.Element, name + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", es, p2, paramIndex, isOutput, interp, location is { } l0 ? l0 + i * ElementLocations(arr.Element) : null, format, span, depth + 1);
            }

            return;
        }

        if (type is MatrixType { Rows: > 1, Columns: > 1 } mat)
        {
            for (int i = 0; i < mat.Rows; i++)
            {
                string es = distance ? semantic : sn + (si + (uint)i).ToString(CultureInfo.InvariantCulture);
                var p2 = new List<int>(path) { i };
                Flatten(entry, into, HlslType.Vector(mat.Element, mat.Columns), name + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", es, p2, paramIndex, isOutput, interp, location is { } l1 ? l1 + i : null, format, span, depth + 1);
            }

            return;
        }

        if (type is not (ScalarType or VectorType))
        {
            Err(SemanticCodes.BadSemantic, "'" + (name.Length == 0 ? entry.Function.Name : name) + "' has type '" + type + "', which cannot be a shader input or output; use scalars, vectors, matrices of at least 2x2, and arrays of them (a one-row or one-column matrix: use a vector).", span);
            return;
        }

        if (distance && type.ScalarElement is not { ScalarKind: ScalarKind.Float })
        {
            Err(SemanticCodes.BadSemantic, "'" + name + "' is a " + sn.Substring(3) + " and must be float, float2, float3 or float4 (or an array of float); it is declared '" + type + "'.", span);
            return;
        }

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

    /// <summary>How many consecutive locations one element of an array of stage variables uses.</summary>
    private static int ElementLocations(HlslType t) => t switch
    {
        ArrayType { Length: > 0 } a => a.Length * ElementLocations(a.Element),
        MatrixType { Rows: > 1, Columns: > 1 } m => m.Rows,
        _ => 1,
    };

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

            // Two stage variables on one location would be rejected by the driver (Vulkan: conflicting location assignment).
            var claimed = new HashSet<int>();
            bool Claim(InterfaceVar v, int location)
            {
                if (claimed.Add(location))
                {
                    return true;
                }

                Err(SemanticCodes.BadSemantic, "'" + v.Name + "' uses location " + Num(location) + ", which another " + stageName + " shader " + (outputs ? "output" : "input") + " already uses. Give each stage variable its own [[vk::location(n)]] or SV_Target index.", v.Span);
                return false;
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
                    if (BuiltInTypeProblem(v, bi) is { } problem)
                    {
                        Err(SemanticCodes.BadSemantic, "'" + v.Name + "' has the semantic " + v.Semantic + ", which needs " + problem + ", not '" + v.Type + "'.", v.Span);
                        continue;
                    }

                    if (bi == BuiltIn.FragDepth)
                    {
                        e.WritesDepth = true;
                    }

                    continue;
                }

                if (targetIndex is { } ti)
                {
                    if (ti is < 0 or >= MaxLocation)
                    {
                        Err(SemanticCodes.BadSemantic, "The render target index " + Num(ti) + " of '" + v.Name + "' is out of range; use SV_Target0 to SV_Target" + Num(MaxLocation - 1) + ".", v.Span);
                        continue;
                    }

                    v.Location = ti;
                    Claim(v, ti);
                    continue;
                }

                if (v.ExplicitLocation is { } loc)
                {
                    if (loc is < 0 or >= MaxLocation)
                    {
                        Err(SemanticCodes.BadSemantic, "The location " + Num(loc) + " of '" + v.Name + "' is out of range; use a value from 0 to " + Num(MaxLocation - 1) + ".", v.Span);
                        continue;
                    }

                    v.Location = loc;
                    Claim(v, loc);
                    continue;
                }

                while (taken.Contains(next))
                {
                    next++;
                }

                v.Location = next;
                claimed.Add(next);
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

    /// <summary>What a built-in's type must be (Vulkan's BuiltIn rules), or null when <paramref name="v"/> has it.</summary>
    private static string? BuiltInTypeProblem(InterfaceVar v, BuiltIn bi)
    {
        var t = v.Type;
        int n = TypeOps.Count(t);
        var e = t.ScalarElement;
        switch (bi)
        {
            case BuiltIn.Position or BuiltIn.FragCoord:
                return n == 4 && e == ScalarType.Float && t is not MatrixType ? null : "a float4";
            case BuiltIn.FragDepth:
                return n == 1 && e == ScalarType.Float && t is not MatrixType ? null : "a float";
            case BuiltIn.FrontFacing:
                return n == 1 && e == ScalarType.Bool && t is not MatrixType ? null : "a bool";
            case BuiltIn.VertexIndex or BuiltIn.InstanceIndex or BuiltIn.PrimitiveIndex or BuiltIn.SampleIndex or BuiltIn.SampleMask
                or BuiltIn.LocalInvocationIndex or BuiltIn.Layer or BuiltIn.ViewportIndex:
                return n == 1 && (e == ScalarType.UInt || e == ScalarType.Int) && t is not MatrixType ? null : "a uint or int";
            case BuiltIn.GlobalInvocationId or BuiltIn.LocalInvocationId or BuiltIn.WorkgroupId:
                return n is >= 1 and <= 3 && (e == ScalarType.UInt || e == ScalarType.Int) && t is not MatrixType ? null : "a uint3 (or fewer components, or int)";
            default:
                return null;
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
                valid = (stage == ShaderStage.Vertex && output) || (stage == ShaderStage.Pixel && !output);
                return BuiltIn.ClipDistance;
            case "culldistance":
                valid = (stage == ShaderStage.Vertex && output) || (stage == ShaderStage.Pixel && !output);
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
                    Err(SemanticCodes.BadSemantic, "'" + v.Name + "' has type '" + v.Type + "', which has no vertex format; use float, int or uint vectors (or two- or four-component 16-bit vectors), or name a format with [[oneshader::format(\"...\")]].", v.Span);
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
        // A resource array is one descriptor binding with a count, as in DXC's SPIR-V output.
        var owners = new Dictionary<(int, int), ResourceDecl>();
        foreach (var r in Resources)
        {
            if (r.ExplicitBinding is not { } b)
            {
                continue;
            }

            r.Set = r.ExplicitSet ?? 0;
            r.Binding = b;
            if (owners.TryGetValue((r.Set, b), out var clash))
            {
                Err(SemanticCodes.BindingCollision, "The resources '" + clash.Name + "' and '" + r.Name + "' both use set " + Num(r.Set) + ", binding " + Num(b)
                    + ". Add [[vk::binding(binding, set)]] to one of them, or give them different register numbers (t, s, b and u registers share binding numbers on Vulkan).", r.Span);
                continue;
            }

            owners[(r.Set, b)] = r;
        }

        // A counter always lives in its buffer's set; an explicit counter binding is reserved before automatic assignment.
        foreach (var r in Resources)
        {
            if (r.ExplicitBinding is null)
            {
                r.Set = 0;
            }

            if (!r.HasCounter || r.ExplicitCounterBinding is not { } cb)
            {
                continue;
            }

            if (owners.TryGetValue((r.Set, cb), out var other))
            {
                Err(SemanticCodes.BindingCollision, "The counter of '" + r.Name + "' and '" + other.Name + "' both use set " + Num(r.Set) + ", binding " + Num(cb)
                    + ". Change [[vk::counter_binding]] or the other resource's binding.", r.Span);
                continue;
            }

            owners[(r.Set, cb)] = r;
        }

        // Everything else takes the next free binding of its set in declaration order; a counter follows its buffer.
        var next = new Dictionary<int, int>();
        int Take(int set)
        {
            next.TryGetValue(set, out int n);
            while (owners.ContainsKey((set, n)))
            {
                n++;
            }

            return n;
        }

        foreach (var r in Resources)
        {
            if (r.IsPushConstant)
            {
                continue;
            }

            if (r.ExplicitBinding is null)
            {
                r.Set = 0;
                int n = Take(0);
                r.Binding = n;
                owners[(0, n)] = r;
                next[0] = n + 1;
            }

            if (!r.HasCounter)
            {
                continue;
            }

            if (r.ExplicitCounterBinding is { } explicitCounter)
            {
                r.CounterBinding = explicitCounter;
                continue;
            }

            int c = Take(r.Set);
            r.CounterBinding = c;
            owners[(r.Set, c)] = r;
            next[r.Set] = c + 1;
        }

        // WebGPU has no push constants, so the WGSL backend binds the block as a uniform buffer. It takes the binding after the
        // highest one in the highest group the shader already uses, which cannot collide with anything the shader declares.
        // Vulkan and Metal ignore this slot.
        int pushSet = 0, pushBinding = 0;
        foreach (var key in owners.Keys)
        {
            if (key.Item1 > pushSet || (key.Item1 == pushSet && key.Item2 >= pushBinding))
            {
                pushSet = key.Item1;
                pushBinding = key.Item2 + 1;
            }
        }

        foreach (var r in Resources)
        {
            if (r.IsPushConstant)
            {
                r.Set = pushSet;
                r.Binding = pushBinding;
                pushBinding++;
            }
        }
    }
}
