using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace OneShader.ParityTests;

/// <summary>Raised for a malformed case file; the message names the file and line.</summary>
public sealed class CaseFormatException : Exception
{
    public CaseFormatException(string message) : base(message)
    {
    }
}

/// <summary>
/// Parser for the plain-text case format. One directive per line; a line that starts with
/// whitespace continues the previous directive; <c>#</c> starts a comment. Data follows a colon.
/// <code>
/// case NAME
/// shader 2d/file.hlsl
/// define NAME=VALUE
/// graphics vs=ENTRY ps=ENTRY
/// target W H [background=GEN]
/// blend none|premul|alpha
/// tolerance N
/// vertexbuffer BINDING STRIDE [instance] : DATA
/// attr LOCATION FORMAT OFFSET [binding=N]
/// index u16|u32 : DATA
/// draw vertices=N|indexed=N [instances=M]
/// dispatch ENTRY X Y Z
/// uniform SET BINDING : DATA
/// storage SET BINDING [out] [size=BYTES] [: DATA]
/// texture SET BINDING WxH[xLAYERS] FORMAT GENERATOR
/// image SET BINDING WxH FORMAT GENERATOR [out]
/// sampler SET BINDING linear|nearest clamp|repeat
/// expect NAME
/// </code>
/// </summary>
public static class CaseFile
{
    /// <summary>Parses a case from text. <paramref name="origin"/> is used in error messages.</summary>
    public static CaseModel Parse(string text, string origin)
    {
        var directives = new List<(int Line, string Text)>();
        int lineNo = 0;
        foreach (string raw in text.Split('\n'))
        {
            lineNo++;
            string line = raw;
            int hash = line.IndexOf('#', StringComparison.Ordinal);
            if (hash >= 0)
            {
                line = line[..hash];
            }
            line = line.TrimEnd();
            if (line.Length == 0)
            {
                continue;
            }
            if (char.IsWhiteSpace(line[0]))
            {
                if (directives.Count == 0)
                {
                    throw Error(origin, lineNo, "continuation line before any directive");
                }
                var last = directives[^1];
                directives[^1] = (last.Line, last.Text + " " + line.Trim());
            }
            else
            {
                directives.Add((lineNo, line));
            }
        }

        string? name = null;
        string? shader = null;
        var defines = new List<KeyValuePair<string, string>>();
        var resources = new List<ResourceDecl>();
        var dispatches = new List<DispatchDecl>();
        var expects = new List<ExpectDecl>();
        string? vs = null;
        string? ps = null;
        int width = 0;
        int height = 0;
        string background = "";
        var blend = BlendMode.None;
        int tolerance = 0;
        var vertexBuffers = new List<VertexBufferDecl>();
        var attributes = new List<VertexInputAttr>();
        string? indexData = null;
        bool index16 = false;
        int drawCount = 0;
        int instances = 1;
        bool indexed = false;

        foreach (var (line, directive) in directives)
        {
            string head = directive;
            string? data = null;
            int colon = directive.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0)
            {
                head = directive[..colon];
                data = directive[(colon + 1)..].Trim();
            }
            string[] t = head.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Func<string, Exception> fail = m => Error(origin, line, m);
            switch (t[0])
            {
                case "case":
                    name = Arg(t, 1, fail);
                    break;
                case "shader":
                    shader = Arg(t, 1, fail);
                    break;
                case "define":
                    {
                        string d = Arg(t, 1, fail);
                        int eq = d.IndexOf('=', StringComparison.Ordinal);
                        defines.Add(eq < 0 ? new(d, "1") : new(d[..eq], d[(eq + 1)..]));
                        break;
                    }
                case "graphics":
                    vs = Option(t, "vs", fail);
                    ps = Option(t, "ps", fail);
                    break;
                case "target":
                    width = Int(Arg(t, 1, fail), fail);
                    height = Int(Arg(t, 2, fail), fail);
                    background = OptionOrNull(t, "background") ?? "";
                    break;
                case "blend":
                    blend = Arg(t, 1, fail) switch
                    {
                        "none" => BlendMode.None,
                        "premul" => BlendMode.Premultiplied,
                        "alpha" => BlendMode.Alpha,
                        var other => throw fail($"unknown blend mode '{other}' (use none, premul or alpha)"),
                    };
                    break;
                case "tolerance":
                    tolerance = Int(Arg(t, 1, fail), fail);
                    break;
                case "vertexbuffer":
                    vertexBuffers.Add(new VertexBufferDecl(Int(Arg(t, 1, fail), fail), Int(Arg(t, 2, fail), fail),
                        data ?? throw fail("vertexbuffer needs ': data'"), Array.IndexOf(t, "instance") > 0));
                    break;
                case "attr":
                    attributes.Add(new VertexInputAttr(Int(Arg(t, 1, fail), fail), ParseVertexFormat(Arg(t, 2, fail), fail),
                        Int(Arg(t, 3, fail), fail), int.Parse(OptionOrNull(t, "binding") ?? "0", CultureInfo.InvariantCulture)));
                    break;
                case "index":
                    index16 = Arg(t, 1, fail) switch
                    {
                        "u16" => true,
                        "u32" => false,
                        var other => throw fail($"unknown index type '{other}' (use u16 or u32)"),
                    };
                    indexData = data ?? throw fail("index needs ': data'");
                    break;
                case "draw":
                    {
                        string? v = OptionOrNull(t, "vertices");
                        string? ix = OptionOrNull(t, "indexed");
                        if ((v is null) == (ix is null))
                        {
                            throw fail("draw needs exactly one of vertices=N or indexed=N");
                        }
                        indexed = ix is not null;
                        drawCount = Int(v ?? ix!, fail);
                        instances = int.Parse(OptionOrNull(t, "instances") ?? "1", CultureInfo.InvariantCulture);
                        break;
                    }
                case "dispatch":
                    dispatches.Add(new DispatchDecl(Arg(t, 1, fail), Int(Arg(t, 2, fail), fail),
                        Int(Arg(t, 3, fail), fail), Int(Arg(t, 4, fail), fail)));
                    break;
                case "uniform":
                    resources.Add(new ResourceDecl
                    {
                        Kind = ResourceKind.Uniform,
                        Set = Int(Arg(t, 1, fail), fail),
                        Binding = Int(Arg(t, 2, fail), fail),
                        Data = data ?? throw fail("uniform needs ': data'"),
                    });
                    break;
                case "storage":
                    resources.Add(new ResourceDecl
                    {
                        Kind = ResourceKind.Storage,
                        Set = Int(Arg(t, 1, fail), fail),
                        Binding = Int(Arg(t, 2, fail), fail),
                        Output = Array.IndexOf(t, "out") >= 0,
                        SizeBytes = int.Parse(OptionOrNull(t, "size") ?? "0", CultureInfo.InvariantCulture),
                        Data = data,
                    });
                    break;
                case "texture":
                case "image":
                    {
                        bool isImage = t[0] == "image";
                        string[] dims = Arg(t, 3, fail).Split('x');
                        if (dims.Length is < 2 or > 3)
                        {
                            throw fail("size must be WxH or WxHxLAYERS");
                        }
                        resources.Add(new ResourceDecl
                        {
                            Kind = isImage ? ResourceKind.StorageImage : ResourceKind.Texture,
                            Set = Int(Arg(t, 1, fail), fail),
                            Binding = Int(Arg(t, 2, fail), fail),
                            Width = Int(dims[0], fail),
                            Height = Int(dims[1], fail),
                            Layers = dims.Length == 3 ? Int(dims[2], fail) : 0,
                            Format = ParseFormat(Arg(t, 4, fail), fail),
                            Generator = Arg(t, 5, fail),
                            Output = isImage && Array.IndexOf(t, "out") >= 0,
                        });
                        break;
                    }
                case "sampler":
                    resources.Add(new ResourceDecl
                    {
                        Kind = ResourceKind.Sampler,
                        Set = Int(Arg(t, 1, fail), fail),
                        Binding = Int(Arg(t, 2, fail), fail),
                        Linear = Arg(t, 3, fail) switch
                        {
                            "linear" => true,
                            "nearest" => false,
                            var other => throw fail($"unknown filter '{other}' (use linear or nearest)"),
                        },
                        Repeat = Arg(t, 4, fail) switch
                        {
                            "repeat" => true,
                            "clamp" => false,
                            var other => throw fail($"unknown address mode '{other}' (use clamp or repeat)"),
                        },
                    });
                    break;
                case "expect":
                    expects.Add(new ExpectDecl(Arg(t, 1, fail)));
                    break;
                default:
                    throw fail($"unknown directive '{t[0]}'");
            }
        }

        if (name is null)
        {
            throw Error(origin, 1, "missing 'case NAME'");
        }
        if (shader is null)
        {
            throw Error(origin, 1, "missing 'shader PATH'");
        }
        var model = new CaseModel { Name = name, Shader = shader };
        model.Defines.AddRange(defines);
        model.Resources.AddRange(resources);
        model.Dispatches.AddRange(dispatches);
        model.Expects.AddRange(expects);
        bool graphics = vs is not null || ps is not null;
        if (graphics == (dispatches.Count > 0))
        {
            throw Error(origin, 1, "a case needs either 'graphics' and 'draw', or 'dispatch' lines");
        }
        if (graphics)
        {
            if (vs is null || ps is null || width <= 0 || height <= 0 || drawCount <= 0)
            {
                throw Error(origin, 1, "graphics case needs vs=, ps=, 'target W H' and 'draw'");
            }
            var g = new GraphicsDecl
            {
                VertexEntry = vs,
                PixelEntry = ps,
                Width = width,
                Height = height,
                Background = background,
                Blend = blend,
                Tolerance = tolerance,
                IndexData = indexed ? indexData ?? throw Error(origin, 1, "indexed draw needs an 'index' line") : null,
                IndexIs16Bit = index16,
                Count = drawCount,
                Instances = instances,
            };
            g.VertexBuffers.AddRange(vertexBuffers);
            g.Attributes.AddRange(attributes);
            model.Graphics = g;
        }
        return model;
    }

    private static CaseFormatException Error(string origin, int line, string message) =>
        new($"{origin}({line}): {message}");

    private static string Arg(string[] t, int i, Func<string, Exception> fail) =>
        i < t.Length ? t[i] : throw fail($"'{t[0]}' is missing argument {i}");

    private static int Int(string s, Func<string, Exception> fail) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : throw fail($"'{s}' is not an integer");

    private static string? OptionOrNull(string[] t, string key)
    {
        string prefix = key + "=";
        for (int i = 1; i < t.Length; i++)
        {
            if (t[i].StartsWith(prefix, StringComparison.Ordinal))
            {
                return t[i][prefix.Length..];
            }
        }
        return null;
    }

    private static string Option(string[] t, string key, Func<string, Exception> fail) =>
        OptionOrNull(t, key) ?? throw fail($"'{t[0]}' needs {key}=...");

    private static PixelFormat ParseFormat(string s, Func<string, Exception> fail) => s switch
    {
        "r8" => PixelFormat.R8,
        "rgba8" => PixelFormat.Rgba8,
        "r32f" => PixelFormat.R32F,
        "rgba32f" => PixelFormat.Rgba32F,
        "r32uint" => PixelFormat.R32UInt,
        _ => throw fail($"unknown texel format '{s}' (use r8, rgba8, r32f, rgba32f or r32uint)"),
    };

    private static VertexFormat ParseVertexFormat(string s, Func<string, Exception> fail) => s switch
    {
        "float" => VertexFormat.Float1,
        "float2" => VertexFormat.Float2,
        "float3" => VertexFormat.Float3,
        "float4" => VertexFormat.Float4,
        "unorm8x4" => VertexFormat.Unorm8x4,
        "uint" => VertexFormat.Word,
        "uint4" => VertexFormat.Uint4,
        _ => throw fail($"unknown vertex format '{s}'"),
    };

    /// <summary>Reads and parses a case file from disk.</summary>
    public static CaseModel Load(string path) =>
        Parse(File.ReadAllText(path, Encoding.UTF8), Path.GetFileName(path));
}
