using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using OneShader.Types;

namespace OneShader.Reflection;

/// <summary>
/// Writes and reads the serialized form of <see cref="ShaderReflection"/> (documented in
/// <c>docs/reflection-format.md</c>). The text is UTF-8 without a byte-order mark, uses <c>\n</c>
/// line ends and invariant number formatting, and lists everything in model order, so equal models
/// always produce identical bytes.
/// </summary>
public static class ReflectionFormat
{
    private const string Magic = "oneshader-reflection";
    private const int MaxDepth = 256;

    /// <summary>Serializes a model to text.</summary>
    public static string Write(ShaderReflection model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var sb = new StringBuilder();
        sb.Append(Magic).Append(' ').Append(ShaderReflection.FormatVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
        Line(sb, 0, "source", ("name", Q(model.SourceName)));
        var targets = new List<string>();
        foreach (var t in model.Targets)
        {
            targets.Add(t.ToString());
        }

        Line(sb, 0, "targets", ("list", List(targets)));
        foreach (var r in model.Resources)
        {
            if (r.ImageFormat.Length == 0)
            {
                Line(
                    sb,
                    0,
                    "resource",
                    ("name", Q(r.Name)),
                    ("kind", r.Kind.ToString()),
                    ("count", N(r.Count)),
                    ("stages", Stages(r.Stages)),
                    ("rtype", r.ResourceType.ToString()),
                    ("hlsl", Q(r.HlslType)));
            }
            else
            {
                // Written only when present, so files for shaders without storage-image formats are unchanged.
                Line(
                    sb,
                    0,
                    "resource",
                    ("name", Q(r.Name)),
                    ("kind", r.Kind.ToString()),
                    ("count", N(r.Count)),
                    ("stages", Stages(r.Stages)),
                    ("rtype", r.ResourceType.ToString()),
                    ("hlsl", Q(r.HlslType)),
                    ("format", Q(r.ImageFormat)));
            }
            foreach (var s in r.Slots)
            {
                Line(sb, 1, "slot", ("target", s.Target.ToString()), ("set", N(s.Set)), ("binding", N(s.Binding)));
            }

            foreach (var s in r.CounterSlots)
            {
                Line(sb, 1, "counter", ("target", s.Target.ToString()), ("set", N(s.Set)), ("binding", N(s.Binding)));
            }

            if (r.Layout is not null)
            {
                Line(sb, 1, "layout");
                WriteType(sb, r.Layout, 2, 0);
            }
        }

        foreach (var p in model.PushConstants)
        {
            Line(sb, 0, "push", ("name", Q(p.Name)), ("stages", Stages(p.Stages)));
            foreach (var s in p.Slots)
            {
                Line(sb, 1, "slot", ("target", s.Target.ToString()), ("set", N(s.Set)), ("binding", N(s.Binding)));
            }

            Line(sb, 1, "layout");
            WriteType(sb, p.Layout, 2, 0);
        }

        foreach (var e in model.EntryPoints)
        {
            Line(
                sb,
                0,
                "entry",
                ("name", Q(e.Name)),
                ("stage", e.Stage.ToString()),
                ("workgroup", N(e.WorkgroupSize.X) + "," + N(e.WorkgroupSize.Y) + "," + N(e.WorkgroupSize.Z)),
                ("stride", N(e.VertexStride)));
            foreach (var v in e.Inputs)
            {
                WriteVariable(sb, "input", v);
            }

            foreach (var v in e.Outputs)
            {
                WriteVariable(sb, "output", v);
            }

            foreach (var u in e.UsedResources)
            {
                Line(sb, 1, "uses", ("name", Q(u)));
            }

            if (e.PushConstant is not null)
            {
                Line(sb, 1, "pushuse", ("name", Q(e.PushConstant)));
            }
        }

        return sb.ToString();
    }

    /// <summary>Serializes a model to UTF-8 bytes.</summary>
    public static byte[] WriteBytes(ShaderReflection model) => new UTF8Encoding(false).GetBytes(Write(model));

    /// <summary>True when the two models serialize identically, which is this format's definition of equal.</summary>
    public static bool AreEqual(ShaderReflection a, ShaderReflection b) => string.Equals(Write(a), Write(b), StringComparison.Ordinal);

    /// <summary>Parses serialized text. Never throws on malformed input.</summary>
    /// <param name="text">The text produced by <see cref="Write"/>.</param>
    /// <param name="model">The model on success.</param>
    /// <param name="error">A message with the line number on failure.</param>
    /// <returns>True on success.</returns>
    public static bool TryRead(string text, out ShaderReflection? model, out string? error)
    {
        ArgumentNullException.ThrowIfNull(text);
        model = null;
        try
        {
            model = new Reader(text).Read();
            error = null;
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void WriteVariable(StringBuilder sb, string keyword, StageVariable v)
    {
        var attrs = new List<(string, string)>
        {
            ("name", Q(v.Name)),
            ("semantic", Q(v.Semantic)),
            ("index", N(v.SemanticIndex)),
            ("location", v.Location is { } loc ? N(loc) : "-"),
            ("builtin", v.BuiltIn.ToString()),
            ("scalar", v.Scalar.ToString()),
            ("components", N(v.Components)),
            ("format", v.Format?.ToString() ?? "None"),
            ("offset", N(v.Offset)),
            ("interp", v.Interpolation.ToString()),
        };
        Line(sb, 1, keyword, [.. attrs]);
    }

    private static void WriteType(StringBuilder sb, ReflectedType t, int depth, int nesting)
    {
        if (nesting > MaxDepth)
        {
            throw new ArgumentException("reflected type is nested too deeply");
        }

        Line(
            sb,
            depth,
            "type",
            ("kind", t.Kind.ToString()),
            ("scalar", t.Scalar.ToString()),
            ("rows", N(t.Rows)),
            ("cols", N(t.Columns)),
            ("name", Q(t.Name)),
            ("size", N(t.Size)),
            ("align", N(t.Alignment)),
            ("length", N(t.ArrayLength)),
            ("stride", N(t.ArrayStride)),
            ("mstride", N(t.MatrixStride)),
            ("packing", t.Packing.ToString()));
        if (t.Element is not null)
        {
            Line(sb, depth + 1, "element");
            WriteType(sb, t.Element, depth + 2, nesting + 1);
        }

        foreach (var m in t.Members)
        {
            Line(sb, depth + 1, "member", ("name", Q(m.Name)), ("offset", N(m.Offset)));
            WriteType(sb, m.Type, depth + 2, nesting + 1);
        }
    }

    private static void Line(StringBuilder sb, int depth, string keyword, params (string Key, string Value)[] attrs)
    {
        sb.Append(' ', depth * 2).Append(keyword);
        foreach (var (k, v) in attrs)
        {
            sb.Append(' ').Append(k).Append('=').Append(v);
        }

        sb.Append('\n');
    }

    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

    private static string List(List<string> items) => items.Count == 0 ? "-" : string.Join(',', items);

    private static string Stages(ShaderStages s)
    {
        if (s == ShaderStages.None)
        {
            return "-";
        }

        var parts = new List<string>();
        if ((s & ShaderStages.Vertex) != 0)
        {
            parts.Add("Vertex");
        }

        if ((s & ShaderStages.Pixel) != 0)
        {
            parts.Add("Pixel");
        }

        if ((s & ShaderStages.Compute) != 0)
        {
            parts.Add("Compute");
        }

        return string.Join(',', parts);
    }

    private static string Q(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
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

    private sealed class Node(int line, int depth, string keyword, Dictionary<string, string> attrs)
    {
        public int Line { get; } = line;

        public int Depth { get; } = depth;

        public string Keyword { get; } = keyword;

        public Dictionary<string, string> Attrs { get; } = attrs;
    }

    private sealed class Reader
    {
        private readonly List<Node> _nodes = [];
        private int _pos;

        public Reader(string text)
        {
            var lines = text.Split('\n');
            if (lines.Length == 0 || !lines[0].StartsWith(Magic + " ", StringComparison.Ordinal))
            {
                throw new FormatException("line 1: not a oneshader reflection file");
            }

            if (lines[0] != Magic + " " + ShaderReflection.FormatVersion.ToString(CultureInfo.InvariantCulture))
            {
                throw new FormatException("line 1: unsupported reflection format version '" + lines[0][(Magic.Length + 1)..] + "'");
            }

            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length > 0)
                {
                    _nodes.Add(ParseLine(i + 1, lines[i]));
                }
            }
        }

        public ShaderReflection Read()
        {
            string source = "";
            var targets = new List<CompileTarget>();
            var resources = new List<ResourceInfo>();
            var entries = new List<EntryPointInfo>();
            var pushes = new List<PushConstantInfo>();
            while (_pos < _nodes.Count)
            {
                var n = _nodes[_pos];
                if (n.Depth != 0)
                {
                    throw Err(n, "unexpected indentation");
                }

                _pos++;
                switch (n.Keyword)
                {
                    case "source": source = Str(n, "name"); break;
                    case "targets":
                        foreach (var t in Csv(n, "list"))
                        {
                            targets.Add(Enm<CompileTarget>(n, t));
                        }

                        break;
                    case "resource": resources.Add(ReadResource(n)); break;
                    case "push": pushes.Add(ReadPush(n)); break;
                    case "entry": entries.Add(ReadEntry(n)); break;
                    default: break; // Unknown top-level records are ignored so newer writers stay readable.
                }
            }

            return new ShaderReflection { SourceName = source, Targets = targets, Resources = resources, PushConstants = pushes, EntryPoints = entries };
        }

        private PushConstantInfo ReadPush(Node n)
        {
            var slots = new List<TargetSlot>();
            ReflectedType? layout = null;
            while (_pos < _nodes.Count && _nodes[_pos].Depth > 0)
            {
                var c = _nodes[_pos++];
                if (c.Depth != 1)
                {
                    throw Err(c, "unexpected indentation");
                }

                if (c.Keyword == "slot")
                {
                    slots.Add(new TargetSlot(Enm<CompileTarget>(c, Str(c, "target")), U(c, "set"), U(c, "binding")));
                }
                else if (c.Keyword == "layout")
                {
                    layout = ReadType(2, 0);
                }
            }

            return new PushConstantInfo
            {
                Name = Str(n, "name"),
                Stages = ParseStages(n, Str(n, "stages")),
                Slots = slots,
                Layout = layout ?? throw Err(n, "'push' has no layout"),
            };
        }

        private ResourceInfo ReadResource(Node n)
        {
            var slots = new List<TargetSlot>();
            var counters = new List<TargetSlot>();
            ReflectedType? layout = null;
            while (_pos < _nodes.Count && _nodes[_pos].Depth > 0)
            {
                var c = _nodes[_pos++];
                if (c.Depth != 1)
                {
                    throw Err(c, "unexpected indentation");
                }

                if (c.Keyword == "slot")
                {
                    slots.Add(new TargetSlot(Enm<CompileTarget>(c, Str(c, "target")), U(c, "set"), U(c, "binding")));
                }
                else if (c.Keyword == "counter")
                {
                    counters.Add(new TargetSlot(Enm<CompileTarget>(c, Str(c, "target")), U(c, "set"), U(c, "binding")));
                }
                else if (c.Keyword == "layout")
                {
                    layout = ReadType(2, 0);
                }
            }

            return new ResourceInfo
            {
                Name = Str(n, "name"),
                Kind = Enm<ResourceKind>(n, Str(n, "kind")),
                Count = U(n, "count"),
                Stages = ParseStages(n, Str(n, "stages")),
                ResourceType = Enm<ResourceTypeKind>(n, Str(n, "rtype")),
                HlslType = Str(n, "hlsl"),
                ImageFormat = n.Attrs.TryGetValue("format", out var imageFormat) ? imageFormat : "",
                Slots = slots,
                CounterSlots = counters,
                Layout = layout,
            };
        }

        private ReflectedType ReadType(int depth, int nesting)
        {
            if (nesting > MaxDepth)
            {
                throw new FormatException("line " + (_pos < _nodes.Count ? _nodes[_pos].Line : 0) + ": type nested too deeply");
            }

            if (_pos >= _nodes.Count || _nodes[_pos].Depth != depth || _nodes[_pos].Keyword != "type")
            {
                throw new FormatException("line " + (_pos < _nodes.Count ? _nodes[_pos].Line : _nodes.Count + 1) + ": expected a 'type' record");
            }

            var n = _nodes[_pos++];
            ReflectedType? element = null;
            var members = new List<ReflectedMember>();
            while (_pos < _nodes.Count && _nodes[_pos].Depth > depth)
            {
                var c = _nodes[_pos++];
                if (c.Depth != depth + 1)
                {
                    throw Err(c, "unexpected indentation");
                }

                if (c.Keyword == "element")
                {
                    element = ReadType(depth + 2, nesting + 1);
                }
                else if (c.Keyword == "member")
                {
                    members.Add(new ReflectedMember(Str(c, "name"), U(c, "offset"), ReadType(depth + 2, nesting + 1)));
                }
            }

            return new ReflectedType
            {
                Kind = Enm<ReflectedTypeKind>(n, Str(n, "kind")),
                Scalar = Enm<ScalarKind>(n, Str(n, "scalar")),
                Rows = U(n, "rows"),
                Columns = U(n, "cols"),
                Name = Str(n, "name"),
                Size = U(n, "size"),
                Alignment = U(n, "align"),
                ArrayLength = U(n, "length"),
                ArrayStride = U(n, "stride"),
                MatrixStride = U(n, "mstride"),
                Packing = Enm<MatrixPacking>(n, Str(n, "packing")),
                Element = element,
                Members = members,
            };
        }

        private EntryPointInfo ReadEntry(Node n)
        {
            var inputs = new List<StageVariable>();
            var outputs = new List<StageVariable>();
            var uses = new List<string>();
            string? push = null;
            while (_pos < _nodes.Count && _nodes[_pos].Depth > 0)
            {
                var c = _nodes[_pos++];
                switch (c.Keyword)
                {
                    case "input": inputs.Add(ReadVariable(c)); break;
                    case "output": outputs.Add(ReadVariable(c)); break;
                    case "uses": uses.Add(Str(c, "name")); break;
                    case "pushuse": push = Str(c, "name"); break;
                    default: break;
                }
            }

            var wg = Str(n, "workgroup").Split(',');
            if (wg.Length != 3)
            {
                throw Err(n, "workgroup needs three numbers");
            }

            return new EntryPointInfo
            {
                Name = Str(n, "name"),
                Stage = Enm<ShaderStage>(n, Str(n, "stage")),
                WorkgroupSize = (ParseUInt(n, wg[0]), ParseUInt(n, wg[1]), ParseUInt(n, wg[2])),
                VertexStride = U(n, "stride"),
                Inputs = inputs,
                Outputs = outputs,
                UsedResources = uses,
                PushConstant = push,
            };
        }

        private static StageVariable ReadVariable(Node c)
        {
            string format = Str(c, "format");
            return new StageVariable
            {
                Name = Str(c, "name"),
                Semantic = Str(c, "semantic"),
                SemanticIndex = U(c, "index"),
                Location = Str(c, "location") == "-" ? null : U(c, "location"),
                BuiltIn = Enm<BuiltIn>(c, Str(c, "builtin")),
                Scalar = Enm<ScalarKind>(c, Str(c, "scalar")),
                Components = U(c, "components"),
                Format = format == "None" ? null : Enm<VertexFormat>(c, format),
                Offset = U(c, "offset"),
                Interpolation = Enm<Interpolation>(c, Str(c, "interp")),
            };
        }

        private static FormatException Err(Node n, string message) => new("line " + n.Line.ToString(CultureInfo.InvariantCulture) + ": " + message);

        private static string Str(Node n, string key) =>
            n.Attrs.TryGetValue(key, out var v) ? v : throw Err(n, "'" + n.Keyword + "' is missing '" + key + "'");

        private static uint U(Node n, string key) => ParseUInt(n, Str(n, key));

        private static uint ParseUInt(Node n, string v) =>
            uint.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out uint r) ? r : throw Err(n, "not a number: " + v);

        private static T Enm<T>(Node n, string v)
            where T : struct, Enum =>
            Enum.TryParse<T>(v, out var r) && Enum.IsDefined(r) ? r : throw Err(n, "unknown " + typeof(T).Name + " '" + v + "'");

        private static string[] Csv(Node n, string key)
        {
            string v = Str(n, key);
            return v == "-" ? [] : v.Split(',');
        }

        private static ShaderStages ParseStages(Node n, string v)
        {
            var result = ShaderStages.None;
            if (v != "-")
            {
                foreach (var p in v.Split(','))
                {
                    result |= Enm<ShaderStages>(n, p);
                }
            }

            return result;
        }

        private static Node ParseLine(int lineNo, string line)
        {
            int i = 0;
            while (i < line.Length && line[i] == ' ')
            {
                i++;
            }

            if (i % 2 != 0)
            {
                throw new FormatException("line " + lineNo.ToString(CultureInfo.InvariantCulture) + ": indentation must be a multiple of two spaces");
            }

            int depth = i / 2;
            int start = i;
            while (i < line.Length && line[i] != ' ')
            {
                i++;
            }

            string keyword = line[start..i];
            var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
            while (i < line.Length)
            {
                if (line[i] != ' ')
                {
                    throw new FormatException("line " + lineNo.ToString(CultureInfo.InvariantCulture) + ": expected a space");
                }

                i++;
                int ks = i;
                while (i < line.Length && line[i] != '=' && line[i] != ' ')
                {
                    i++;
                }

                if (i >= line.Length || line[i] != '=')
                {
                    throw new FormatException("line " + lineNo.ToString(CultureInfo.InvariantCulture) + ": expected key=value");
                }

                string key = line[ks..i];
                i++;
                string value;
                if (i < line.Length && line[i] == '"')
                {
                    i++;
                    var sb = new StringBuilder();
                    bool closed = false;
                    while (i < line.Length)
                    {
                        char c = line[i++];
                        if (c == '"')
                        {
                            closed = true;
                            break;
                        }

                        if (c != '\\')
                        {
                            sb.Append(c);
                            continue;
                        }

                        if (i >= line.Length)
                        {
                            break;
                        }

                        char e = line[i++];
                        switch (e)
                        {
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                if (i + 4 > line.Length || !int.TryParse(line.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp))
                                {
                                    throw new FormatException("line " + lineNo.ToString(CultureInfo.InvariantCulture) + ": bad \\u escape");
                                }

                                sb.Append((char)cp);
                                i += 4;
                                break;
                            default: sb.Append(e); break;
                        }
                    }

                    if (!closed)
                    {
                        throw new FormatException("line " + lineNo.ToString(CultureInfo.InvariantCulture) + ": unterminated string");
                    }

                    value = sb.ToString();
                }
                else
                {
                    int vs = i;
                    while (i < line.Length && line[i] != ' ')
                    {
                        i++;
                    }

                    value = line[vs..i];
                }

                attrs[key] = value;
            }

            return new Node(lineNo, depth, keyword, attrs);
        }
    }
}
