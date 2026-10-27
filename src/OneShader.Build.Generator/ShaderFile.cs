using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OneShader.Build.Generator;

/// <summary>A type node of a reflected buffer layout (see docs/reflection-format.md).</summary>
internal sealed class TypeNode
{
    public string Kind = "";
    public string Scalar = "";
    public uint Rows;
    public uint Cols;
    public string Name = "";
    public uint Size;
    public uint ArrayLength;
    public uint ArrayStride;
    public uint MatrixStride;
    public string Packing = "";
    public TypeNode? Element;
    public List<MemberNode> Members = new List<MemberNode>();
}

internal sealed class MemberNode
{
    public MemberNode(string name, uint offset, TypeNode type)
    {
        Name = name;
        Offset = offset;
        Type = type;
    }

    public string Name { get; }
    public uint Offset { get; }
    public TypeNode Type { get; }
}

internal sealed class ResourceNode
{
    public string Name = "";
    public string Kind = "";
    public uint Count;
    public string Stages = "";

    /// <summary>
    /// True when some selected target gave the resource a slot. <see cref="Set"/> and <see cref="Binding"/> are the SPIR-V slot, the WGSL slot
    /// (same numbers) when SPIR-V was not selected, and the Metal index (set 0) when only Metal was.
    /// </summary>
    public bool HasSlot;
    public uint Set;
    public uint Binding;
    public bool HasCounter;
    public uint CounterBinding;

    /// <summary>The Metal argument index (within the resource's class: buffer, texture or sampler) when Msl was selected.</summary>
    public bool HasMsl;
    public uint MslIndex;
    public bool HasMslCounter;
    public uint MslCounterIndex;
    public TypeNode? Layout;

    /// <summary>The HLSL resource family (<c>Texture2D</c>, <c>RWTexture2D</c>, ...), as the <c>ResourceTypeKind</c> member name.</summary>
    public string ResourceType = "ConstantBuffer";

    /// <summary>The HLSL type as spelled; empty for a cbuffer.</summary>
    public string Hlsl = "";

    /// <summary>The storage-image format name, empty if none.</summary>
    public string ImageFormat = "";
}

/// <summary>A push-constant block: its layout, stage visibility and where each selected target takes the bytes.</summary>
internal sealed class PushNode
{
    public string Name = "";
    public string Stages = "";
    public bool HasMsl;
    public uint MslIndex;
    public bool HasWgsl;
    public uint WgslGroup;
    public uint WgslBinding;
    public TypeNode? Layout;
}

internal sealed class VariableNode
{
    public string Name = "";
    public bool HasLocation;
    public uint Location;
    public string Format = "None";
    public uint Offset;
}

internal sealed class EntryNode
{
    public uint Subgroup;
    public string Name = "";
    public string Stage = "";
    public uint X;
    public uint Y;
    public uint Z;
    public uint Stride;
    public List<VariableNode> Inputs = new List<VariableNode>();

    /// <summary>Names of the resources the entry point uses, in the order the reflection lists them.</summary>
    public List<string> Uses = new List<string>();

    /// <summary>The name of the push-constant block it reads, or empty.</summary>
    public string PushUse = "";
}

/// <summary>A keyword axis of a variant set: a flag (defined as 1 or not) or a choice among named values.</summary>
internal sealed class AxisNode
{
    public string Name = "";
    public bool IsFlag;
    public List<string> Values = new List<string>();
}

/// <summary>One line of a variant manifest: which file holds the variant and which axis values it stands for.</summary>
internal sealed class VariantRef
{
    public uint Index;
    public string Key = "";
    public string Name = "";
    public string Defines = "";
    public List<uint> Ordinals = new List<uint>();
}

/// <summary>The text of one entry point for one text target.</summary>
internal sealed class TextBlob
{
    public byte[] Data = [];

    /// <summary>The function name in the target source.</summary>
    public string Function = "";

    /// <summary>MSL vertex functions: the buffer index the vertex attributes are read from.</summary>
    public bool HasVertexBuffer;
    public uint VertexBuffer;
}

/// <summary>
/// One <c>.oneshader</c> file written by the build task: the reflection records followed by
/// <c>generate</c> and <c>blob</c> records. Parsed here, without the compiler library, so the
/// generator can run inside any compiler host.
/// </summary>
internal sealed class ShaderFile
{
    public string Source = "";
    public string Namespace = "";
    public string ClassName = "";
    public List<ResourceNode> Resources = new List<ResourceNode>();
    public List<EntryNode> Entries = new List<EntryNode>();
    public List<PushNode> Pushes = new List<PushNode>();

    /// <summary>
    /// The reflection records of the file as written (header line included, build bookkeeping excluded). Generated code embeds it so a
    /// program can compare a recompiled shader against the interface it was built with.
    /// </summary>
    public string ReflectionText = "";

    /// <summary>SPIR-V blobs by entry point name.</summary>
    public Dictionary<string, byte[]> SpirV = new Dictionary<string, byte[]>(StringComparer.Ordinal);

    /// <summary>MSL sources by entry point name.</summary>
    public Dictionary<string, TextBlob> Msl = new Dictionary<string, TextBlob>(StringComparer.Ordinal);

    /// <summary>WGSL sources by entry point name.</summary>
    public Dictionary<string, TextBlob> Wgsl = new Dictionary<string, TextBlob>(StringComparer.Ordinal);

    /// <summary>Set on the output of one variant: the key the manifest lists it under.</summary>
    public string VariantKey = "";

    /// <summary>Set on a variant manifest: <c>axes</c> or <c>names</c>, with the axes and the variants it lists.</summary>
    public string VariantMode = "";
    public List<AxisNode> Axes = new List<AxisNode>();
    public List<VariantRef> VariantRefs = new List<VariantRef>();

    /// <summary>The targets the shader was compiled for (<c>SpirV</c>, <c>Msl</c>, <c>Wgsl</c>).</summary>
    public HashSet<string> Targets = new HashSet<string>(StringComparer.Ordinal);

    private const int MaxTypeDepth = 64;

    private sealed class Rec
    {
        public int Depth;
        public string Keyword = "";
        public Dictionary<string, string> Attrs = new Dictionary<string, string>(StringComparer.Ordinal);
        public int Line;
    }

    /// <summary>Parses the text; returns null and sets <paramref name="error"/> when it is not a version 1 file.</summary>
    public static ShaderFile? Parse(string text, out string error)
    {
        try
        {
            error = "";
            return new ShaderFile().Read(text);
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private ShaderFile Read(string text)
    {
        var lines = text.Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd('\r') != "oneshader-reflection 1")
        {
            throw new FormatException("line 1: expected 'oneshader-reflection 1'; the file was written by a different version of the build task.");
        }

        var recs = new List<Rec>();
        var reflection = new StringBuilder("oneshader-reflection 1\n");
        bool inReflection = true;
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var rec = ParseLine(line, i + 1);
            recs.Add(rec);
            if (rec.Depth == 0)
            {
                // Records the compiler's reflection writer produces; the rest is build bookkeeping.
                inReflection = rec.Keyword == "source" || rec.Keyword == "targets" || rec.Keyword == "resource" || rec.Keyword == "push" || rec.Keyword == "entry";
            }

            if (inReflection)
            {
                reflection.Append(line).Append('\n');
            }
        }

        ReflectionText = reflection.ToString();

        int pos = 0;
        while (pos < recs.Count)
        {
            var r = recs[pos];
            if (r.Depth != 0)
            {
                throw Err(r, "unexpected indentation");
            }

            pos++;
            switch (r.Keyword)
            {
                case "source":
                    Source = Str(r, "name");
                    break;
                case "generate":
                    Namespace = Str(r, "namespace");
                    ClassName = Str(r, "class");
                    break;
                case "targets":
                    foreach (string t in Str(r, "list").Split(','))
                    {
                        Targets.Add(t);
                    }

                    break;
                case "blob":
                    ReadBlob(r);
                    break;
                case "variantof":
                    VariantKey = Str(r, "key");
                    break;
                case "variantset":
                    VariantMode = Str(r, "mode");
                    break;
                case "axis":
                    ReadAxis(r);
                    break;
                case "variant":
                    ReadVariantRef(r);
                    break;
                case "resource":
                    pos = ReadResource(recs, pos, r);
                    break;
                case "push":
                    pos = ReadPush(recs, pos, r);
                    break;
                case "entry":
                    pos = ReadEntry(recs, pos, r);
                    break;
                default:
                    break; // targets, diagnostic, dep, options and records from newer writers.
            }
        }

        return this;
    }

    private void ReadAxis(Rec r)
    {
        var axis = new AxisNode { Name = Str(r, "name"), IsFlag = Str(r, "kind") == "flag" };
        if (!axis.IsFlag)
        {
            axis.Values.AddRange(Str(r, "values").Split(','));
        }

        Axes.Add(axis);
    }

    private void ReadVariantRef(Rec r)
    {
        var v = new VariantRef { Index = U(r, "index"), Key = Str(r, "key"), Defines = r.Attrs.TryGetValue("defines", out var d) ? d : "" };
        if (r.Attrs.TryGetValue("name", out var name))
        {
            v.Name = name;
        }

        if (r.Attrs.TryGetValue("ordinals", out var ords) && ords.Length > 0)
        {
            foreach (string o in ords.Split(','))
            {
                v.Ordinals.Add(Num(r, o));
            }
        }

        VariantRefs.Add(v);
    }

    private void ReadBlob(Rec r)
    {
        string target = Str(r, "target");
        byte[] data;
        try
        {
            data = Convert.FromBase64String(Str(r, "data"));
        }
        catch (FormatException)
        {
            throw Err(r, "blob data is not base64");
        }

        if (target == "SpirV")
        {
            SpirV[Str(r, "entry")] = data;
        }
        else if (target == "Msl" || target == "Wgsl")
        {
            var blob = new TextBlob { Data = data, Function = Str(r, "function") };
            if (r.Attrs.ContainsKey("vertexBuffer"))
            {
                blob.HasVertexBuffer = true;
                blob.VertexBuffer = U(r, "vertexBuffer");
            }

            (target == "Msl" ? Msl : Wgsl)[Str(r, "entry")] = blob;
        }
    }

    private int ReadResource(List<Rec> recs, int pos, Rec r)
    {
        var res = new ResourceNode
        {
            Name = Str(r, "name"),
            Kind = Str(r, "kind"),
            Count = U(r, "count"),
            Stages = Str(r, "stages"),
            ResourceType = r.Attrs.TryGetValue("rtype", out var rtype) ? rtype : "ConstantBuffer",
            Hlsl = r.Attrs.TryGetValue("hlsl", out var hlsl) ? hlsl : "",
            ImageFormat = r.Attrs.TryGetValue("format", out var imageFormat) ? imageFormat : "",
        };
        bool spirvSlot = false, spirvCounter = false, primary = false;
        while (pos < recs.Count && recs[pos].Depth > 0)
        {
            var c = recs[pos];
            if (c.Depth == 1 && c.Keyword == "slot")
            {
                string t = Str(c, "target");
                if (t == "Msl")
                {
                    res.HasMsl = true;
                    res.MslIndex = U(c, "binding");
                }
                else if (t == "SpirV" || (t == "Wgsl" && !spirvSlot))
                {
                    spirvSlot = t == "SpirV";
                    res.HasSlot = true;
                    res.Set = U(c, "set");
                    res.Binding = U(c, "binding");
                    primary = true;
                }

                pos++;
            }
            else if (c.Depth == 1 && c.Keyword == "counter")
            {
                string t = Str(c, "target");
                if (t == "Msl")
                {
                    res.HasMslCounter = true;
                    res.MslCounterIndex = U(c, "binding");
                }
                else if (t == "SpirV" || (t == "Wgsl" && !spirvCounter))
                {
                    spirvCounter = t == "SpirV";
                    res.HasCounter = true;
                    res.CounterBinding = U(c, "binding");
                }

                pos++;
            }
            else if (c.Depth == 1 && c.Keyword == "layout")
            {
                pos++;
                if (pos < recs.Count && recs[pos].Depth == 2 && recs[pos].Keyword == "type")
                {
                    res.Layout = ReadType(recs, ref pos, 0);
                }
            }
            else
            {
                pos++;
            }
        }

        if (!primary && res.HasMsl)
        {
            // Metal only: the index stands in for set and binding.
            res.HasSlot = true;
            res.Set = 0;
            res.Binding = res.MslIndex;
            res.HasCounter = res.HasMslCounter;
            res.CounterBinding = res.MslCounterIndex;
        }

        Resources.Add(res);
        return pos;
    }

    private int ReadPush(List<Rec> recs, int pos, Rec r)
    {
        var p = new PushNode { Name = Str(r, "name"), Stages = Str(r, "stages") };
        while (pos < recs.Count && recs[pos].Depth > 0)
        {
            var c = recs[pos];
            if (c.Depth == 1 && c.Keyword == "slot")
            {
                string t = Str(c, "target");
                if (t == "Msl")
                {
                    p.HasMsl = true;
                    p.MslIndex = U(c, "binding");
                }
                else if (t == "Wgsl")
                {
                    p.HasWgsl = true;
                    p.WgslGroup = U(c, "set");
                    p.WgslBinding = U(c, "binding");
                }

                pos++;
            }
            else if (c.Depth == 1 && c.Keyword == "layout")
            {
                pos++;
                if (pos < recs.Count && recs[pos].Depth == 2 && recs[pos].Keyword == "type")
                {
                    p.Layout = ReadType(recs, ref pos, 0);
                }
            }
            else
            {
                pos++;
            }
        }

        Pushes.Add(p);
        return pos;
    }

    private static TypeNode ReadType(List<Rec> recs, ref int pos, int nesting)
    {
        if (nesting > MaxTypeDepth)
        {
            throw Err(recs[pos], "type is nested too deeply");
        }

        var r = recs[pos++];
        var t = new TypeNode
        {
            Kind = Str(r, "kind"),
            Scalar = Str(r, "scalar"),
            Rows = U(r, "rows"),
            Cols = U(r, "cols"),
            Name = Str(r, "name"),
            Size = U(r, "size"),
            ArrayLength = U(r, "length"),
            ArrayStride = U(r, "stride"),
            MatrixStride = U(r, "mstride"),
            Packing = Str(r, "packing"),
        };
        while (pos < recs.Count && recs[pos].Depth > r.Depth)
        {
            var c = recs[pos];
            if (c.Depth == r.Depth + 1 && (c.Keyword == "member" || c.Keyword == "element"))
            {
                pos++;
                if (pos >= recs.Count || recs[pos].Keyword != "type" || recs[pos].Depth != c.Depth + 1)
                {
                    throw Err(c, "'" + c.Keyword + "' needs a type");
                }

                var child = ReadType(recs, ref pos, nesting + 1);
                if (c.Keyword == "member")
                {
                    t.Members.Add(new MemberNode(Str(c, "name"), U(c, "offset"), child));
                }
                else
                {
                    t.Element = child;
                }
            }
            else
            {
                pos++;
            }
        }

        return t;
    }

    private int ReadEntry(List<Rec> recs, int pos, Rec r)
    {
        var e = new EntryNode { Name = Str(r, "name"), Stage = Str(r, "stage"), Stride = U(r, "stride") };
        var wg = Str(r, "workgroup").Split(',');
        if (wg.Length != 3)
        {
            throw Err(r, "workgroup needs three numbers");
        }

        e.X = Num(r, wg[0]);
        e.Y = Num(r, wg[1]);
        e.Z = Num(r, wg[2]);
        while (pos < recs.Count && recs[pos].Depth > 0)
        {
            var c = recs[pos++];
            if (c.Keyword == "input")
            {
                string loc = Str(c, "location");
                e.Inputs.Add(new VariableNode
                {
                    Name = Str(c, "name"),
                    HasLocation = loc != "-",
                    Location = loc == "-" ? 0 : Num(c, loc),
                    Format = Str(c, "format"),
                    Offset = U(c, "offset"),
                });
            }
            else if (c.Keyword == "uses")
            {
                e.Uses.Add(Str(c, "name"));
            }
            else if (c.Keyword == "pushuse")
            {
                e.PushUse = Str(c, "name");
            }
            else if (c.Keyword == "subgroup")
            {
                e.Subgroup = U(c, "ops");
            }
        }

        Entries.Add(e);
        return pos;
    }

    private static Rec ParseLine(string line, int number)
    {
        var rec = new Rec { Line = number };
        int i = 0;
        while (i < line.Length && line[i] == ' ')
        {
            i++;
        }

        rec.Depth = i / 2;
        int start = i;
        while (i < line.Length && line[i] != ' ')
        {
            i++;
        }

        rec.Keyword = line.Substring(start, i - start);
        while (i < line.Length)
        {
            while (i < line.Length && line[i] == ' ')
            {
                i++;
            }

            if (i >= line.Length)
            {
                break;
            }

            int ks = i;
            while (i < line.Length && line[i] != '=' && line[i] != ' ')
            {
                i++;
            }

            if (i >= line.Length || line[i] != '=')
            {
                throw new FormatException("line " + number.ToString(CultureInfo.InvariantCulture) + ": expected key=value");
            }

            string key = line.Substring(ks, i - ks);
            i++;
            string value;
            if (i < line.Length && line[i] == '"')
            {
                i++;
                var sb = new StringBuilder();
                bool closed = false;
                while (i < line.Length)
                {
                    char ch = line[i++];
                    if (ch == '"')
                    {
                        closed = true;
                        break;
                    }

                    if (ch != '\\')
                    {
                        sb.Append(ch);
                        continue;
                    }

                    if (i >= line.Length)
                    {
                        break;
                    }

                    char esc = line[i++];
                    switch (esc)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > line.Length || !int.TryParse(line.Substring(i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int cp))
                            {
                                throw new FormatException("line " + number.ToString(CultureInfo.InvariantCulture) + ": bad \\u escape");
                            }

                            sb.Append((char)cp);
                            i += 4;
                            break;
                        default: sb.Append(esc); break;
                    }
                }

                if (!closed)
                {
                    throw new FormatException("line " + number.ToString(CultureInfo.InvariantCulture) + ": unterminated string");
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

                value = line.Substring(vs, i - vs);
            }

            rec.Attrs[key] = value;
        }

        return rec;
    }

    private static FormatException Err(Rec r, string message) =>
        new FormatException("line " + r.Line.ToString(CultureInfo.InvariantCulture) + ": " + message);

    private static string Str(Rec r, string key) =>
        r.Attrs.TryGetValue(key, out var v) ? v : throw Err(r, "'" + r.Keyword + "' is missing '" + key + "'");

    private static uint U(Rec r, string key) => Num(r, Str(r, key));

    private static uint Num(Rec r, string v) =>
        uint.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out uint n) ? n : throw Err(r, "not a number: " + v);
}
