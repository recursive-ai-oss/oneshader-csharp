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
    public bool HasSpirVSlot;
    public uint Set;
    public uint Binding;
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
    public string Name = "";
    public string Stage = "";
    public uint X;
    public uint Y;
    public uint Z;
    public uint Stride;
    public List<VariableNode> Inputs = new List<VariableNode>();
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

    /// <summary>SPIR-V blobs by entry point name.</summary>
    public Dictionary<string, byte[]> SpirV = new Dictionary<string, byte[]>(StringComparer.Ordinal);

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
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            recs.Add(ParseLine(line, i + 1));
        }

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
                case "blob":
                    if (Str(r, "target") == "SpirV")
                    {
                        try
                        {
                            SpirV[Str(r, "entry")] = Convert.FromBase64String(Str(r, "data"));
                        }
                        catch (FormatException)
                        {
                            throw Err(r, "blob data is not base64");
                        }
                    }

                    break;
                case "resource":
                    pos = ReadResource(recs, pos, r);
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

    private int ReadResource(List<Rec> recs, int pos, Rec r)
    {
        var res = new ResourceNode
        {
            Name = Str(r, "name"),
            Kind = Str(r, "kind"),
            Count = U(r, "count"),
            Stages = Str(r, "stages"),
        };
        while (pos < recs.Count && recs[pos].Depth > 0)
        {
            var c = recs[pos];
            if (c.Depth == 1 && c.Keyword == "slot" && Str(c, "target") == "SpirV")
            {
                res.HasSpirVSlot = true;
                res.Set = U(c, "set");
                res.Binding = U(c, "binding");
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

        Resources.Add(res);
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
