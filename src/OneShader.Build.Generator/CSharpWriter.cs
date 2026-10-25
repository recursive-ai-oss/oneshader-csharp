using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OneShader.Build.Generator;

/// <summary>A problem found while generating code for one shader file.</summary>
internal sealed class GenProblem
{
    public GenProblem(string code, string message)
    {
        Code = code;
        Message = message;
    }

    public string Code { get; }
    public string Message { get; }
}

/// <summary>Turns one parsed shader file into C# source.</summary>
internal sealed class CSharpWriter
{
    // Generated code spells every type out with global:: so a project's own namespaces
    // (a namespace called "OneShader" or "System", say) cannot change what it means.
    private const string Fo = "[global::System.Runtime.InteropServices.FieldOffset(";
    private const string V2 = "global::System.Numerics.Vector2";
    private const string V3 = "global::System.Numerics.Vector3";
    private const string V4 = "global::System.Numerics.Vector4";
    private const string M44 = "global::System.Numerics.Matrix4x4";
    private const int MaxMembersPerStruct = 4096;

    private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
        "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected",
        "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
        "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile", "while",
    };

    // Names an entry-point class declares itself; a class cannot share its name with a member.
    private static readonly HashSet<string> EntryMembers = new HashSet<string>(StringComparer.Ordinal)
    {
        "Stage", "EntryPoint", "SpirV", "Vertex", "Stride", "Inputs", "ThreadGroupSizeX", "ThreadGroupSizeY", "ThreadGroupSizeZ",
    };

    private readonly ShaderFile _file;
    private readonly string _className;
    private readonly List<GenProblem> _problems = new List<GenProblem>();
    private readonly StringBuilder _sb = new StringBuilder();
    private readonly HashSet<string> _topNames = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _structBySignature = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly List<KeyValuePair<string, TypeNode>> _structQueue = new List<KeyValuePair<string, TypeNode>>();
    private int _indent;

    private CSharpWriter(ShaderFile file, string className)
    {
        _file = file;
        _className = className;
    }

    /// <summary>The identifier used for a shader file's class: the file name without extension, made into a valid name.</summary>
    public static string ClassNameFor(ShaderFile file)
    {
        if (file.ClassName.Length > 0)
        {
            return file.ClassName;
        }

        string name = file.Source.Replace('\\', '/');
        int slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name.Substring(slash + 1);
        }

        int dot = name.LastIndexOf('.');
        if (dot > 0)
        {
            name = name.Substring(0, dot);
        }

        return TypeName(name);
    }

    /// <summary>Generates the source; returns null when <paramref name="problems"/> holds errors.</summary>
    public static string? Generate(ShaderFile file, out List<GenProblem> problems)
    {
        var w = new CSharpWriter(file, ClassNameFor(file));
        string? text = w.Run();
        problems = w._problems;
        return w._problems.Count == 0 ? text : null;
    }

    /// <summary>
    /// The C# type name for an HLSL or file name. The C# compiler warns (CS8981, an error when
    /// warnings are errors) about type names made only of lower-case ASCII letters, so such names
    /// become PascalCase: split at every character that cannot be in an identifier (and at '_'),
    /// upper-case the first letter of each part, join (<c>msdf_text</c> gives <c>MsdfText</c>,
    /// <c>ui-rect</c> gives <c>UiRect</c>). A name that already has an upper-case letter keeps its
    /// spelling, with only invalid characters turned into '_' (<c>Quad</c>, <c>UiRect</c>, <c>UI_rect</c>
    /// are unchanged). A leading digit gets a '_' in front (<c>2d</c> gives <c>_2d</c>).
    /// </summary>
    internal static string TypeName(string name)
    {
        bool hasUpper = false;
        foreach (char c in name)
        {
            hasUpper |= char.IsUpper(c);
        }

        if (hasUpper)
        {
            return Sanitize(name);
        }

        var sb = new StringBuilder(name.Length + 1);
        bool startOfPart = true;
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(startOfPart ? char.ToUpperInvariant(c) : c);
                startOfPart = false;
            }
            else
            {
                startOfPart = true;
            }
        }

        return Sanitize(sb.ToString());
    }

    internal static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length + 1);
        foreach (char c in name)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }

        if (sb.Length == 0 || char.IsDigit(sb[0]))
        {
            sb.Insert(0, '_');
        }

        return sb.ToString();
    }

    private static string Esc(string id) => Keywords.Contains(id) ? "@" + id : id;

    private static string Num(uint v) => v.ToString(CultureInfo.InvariantCulture) + "u";

    private static string Lit(uint v) => v.ToString(CultureInfo.InvariantCulture);

    private static string Str(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            if (c == '"' || c == '\\')
            {
                sb.Append('\\').Append(c);
            }
            else if (c < ' ' || c > '~')
            {
                sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.Append('"').ToString();
    }

    private void Problem(string code, string message) => _problems.Add(new GenProblem(code, message));

    private void Line(string text)
    {
        if (text.Length > 0)
        {
            _sb.Append(' ', _indent * 4).Append(text);
        }

        _sb.Append('\n');
    }

    private string? Run()
    {
        var f = _file;
        _topNames.Add("Bindings");
        _topNames.Add("Reflection");
        _topNames.Add("ReflectionData");
        _topNames.Add(_className);
        foreach (var e in f.Entries)
        {
            if (!_topNames.Add(TypeName(e.Name)) || EntryMembers.Contains(TypeName(e.Name)))
            {
                Problem(Codes.NameClash, "Entry point '" + e.Name + "' (C# class '" + TypeName(e.Name) + "') in " + f.Source + " clashes with another generated name (a constant buffer, a struct or 'Bindings'). Rename the HLSL function or the other declaration.");
            }
        }

        var cbuffers = new List<ResourceNode>();
        foreach (var r in f.Resources)
        {
            if (r.Kind == "ConstantBuffer" && r.Layout is { Kind: "Struct" })
            {
                if (!_topNames.Add(TypeName(r.Name)))
                {
                    Problem(Codes.NameClash, "Constant buffer '" + r.Name + "' (C# struct '" + TypeName(r.Name) + "') in " + f.Source + " clashes with an entry point or another generated name. Rename the cbuffer, the function or the struct.");
                }

                cbuffers.Add(r);
            }
        }

        if (_problems.Count > 0)
        {
            return null;
        }

        // Element types of structured buffers get a struct as well, so the CPU side fills the
        // same bytes the shader reads.
        foreach (var r in f.Resources)
        {
            if ((r.Kind == "ReadOnlyStorageBuffer" || r.Kind == "StorageBuffer") && r.Layout is { Kind: "Struct" } el)
            {
                NestedName(el);
            }
        }

        Line("// <auto-generated>");
        Line("// Generated by OneShader from " + f.Source.Replace('\n', ' ').Replace('\r', ' ') + ". Changes are lost when the shader is built again.");
        Line("// </auto-generated>");
        Line("#nullable enable");
        Line("#pragma warning disable CS1591 // generated members are described by the shader, not by XML comments");
        Line("");
        if (f.Namespace.Length > 0)
        {
            Line("namespace " + f.Namespace + ";");
            Line("");
        }

        Line("public static partial class " + Esc(_className));
        Line("{");
        _indent++;
        bool first = true;
        foreach (var cb in cbuffers)
        {
            Separator(ref first);
            EmitStruct(TypeName(cb.Name), cb.Layout!);
        }

        // The queue grows while structs are emitted (a struct can contain another).
        for (int i = 0; i < _structQueue.Count; i++)
        {
            Separator(ref first);
            EmitStruct(_structQueue[i].Key, _structQueue[i].Value);
        }

        Separator(ref first);
        EmitBindings();
        foreach (var e in f.Entries)
        {
            Line("");
            EmitEntry(e);
        }

        Line("");
        EmitReflection();
        _indent--;
        Line("}");
        return _sb.ToString();
    }

    // The reflection the C# above was generated from, as data in the assembly (no I/O). A program that recompiles the shader
    // at run time compares a fresh reflection with this one before swapping in the new code. Nothing here runs unless
    // Reflection is read, so an AOT build that never asks drops the parser along with it.
    private void EmitReflection()
    {
        EmitBytes("ReflectionData", new UTF8Encoding(false).GetBytes(_file.ReflectionText));
        Line("");
        Line("private static global::OneShader.Reflection.ShaderReflection? s_reflection;");
        Line("");
        Line("/// <summary>The reflection this class was generated from; parsed on first use.</summary>");
        Line("public static global::OneShader.Reflection.ShaderReflection Reflection => s_reflection ??= ParseReflection();");
        Line("");
        Line("private static global::OneShader.Reflection.ShaderReflection ParseReflection()");
        Line("{");
        _indent++;
        Line("if (!global::OneShader.Reflection.ReflectionFormat.TryRead(global::System.Text.Encoding.UTF8.GetString(ReflectionData), out var model, out var error) || model is null)");
        Line("{");
        _indent++;
        Line("throw new global::System.InvalidOperationException(\"The embedded shader reflection is unreadable (\" + error + \"). Rebuild the project.\");");
        _indent--;
        Line("}");
        Line("");
        Line("return model;");
        _indent--;
        Line("}");
    }

    private void Separator(ref bool first)
    {
        if (!first)
        {
            Line("");
        }

        first = false;
    }

    // ---- structs ----

    private string NestedName(TypeNode s)
    {
        string sig = Signature(s);
        if (_structBySignature.TryGetValue(sig, out var existing))
        {
            return existing;
        }

        string baseName = TypeName(s.Name.Length == 0 ? "Struct" : s.Name);
        string name = baseName;
        for (int n = 1; !_topNames.Add(name); n++)
        {
            name = baseName + "_" + n.ToString(CultureInfo.InvariantCulture);
        }

        _structBySignature[sig] = name;
        _structQueue.Add(new KeyValuePair<string, TypeNode>(name, s));
        return name;
    }

    private static string Signature(TypeNode t)
    {
        var sb = new StringBuilder();
        AppendSignature(sb, t);
        return sb.ToString();
    }

    private static void AppendSignature(StringBuilder sb, TypeNode t)
    {
        sb.Append(t.Kind).Append(':').Append(t.Scalar).Append(':').Append(t.Rows).Append(':').Append(t.Cols).Append(':')
          .Append(t.Name).Append(':').Append(t.Size).Append(':').Append(t.ArrayLength).Append(':').Append(t.ArrayStride).Append(':')
          .Append(t.MatrixStride).Append(':').Append(t.Packing).Append('{');
        if (t.Element is not null)
        {
            AppendSignature(sb, t.Element);
        }

        foreach (var m in t.Members)
        {
            sb.Append(m.Name).Append('@').Append(m.Offset).Append('=');
            AppendSignature(sb, m.Type);
        }

        sb.Append('}');
    }

    private sealed class StructContext
    {
        public StructContext(string typeName)
        {
            TypeName = typeName;
        }

        public string TypeName { get; }
        public HashSet<string> Names { get; } = new HashSet<string>(StringComparer.Ordinal) { "Size" };
        public List<string> Members { get; } = new List<string>();
        public bool Failed { get; set; }
    }

    private void EmitStruct(string csName, TypeNode layout)
    {
        var ctx = new StructContext(csName);
        foreach (var m in layout.Members)
        {
            AddMember(ctx, m.Name, m.Offset, m.Type, 0);
        }

        if (ctx.Failed)
        {
            return;
        }

        Line("[global::System.Runtime.InteropServices.StructLayout(global::System.Runtime.InteropServices.LayoutKind.Explicit, Size = " + Lit(layout.Size) + ")]");
        Line("public struct " + csName);
        Line("{");
        _indent++;
        Line("public const uint Size = " + Num(layout.Size) + ";");
        foreach (var m in ctx.Members)
        {
            Line("");
            foreach (var l in m.Split('\n'))
            {
                Line(l);
            }
        }

        _indent--;
        Line("}");
    }

    private void Fail(StructContext ctx, string message)
    {
        ctx.Failed = true;
        Problem(Codes.Unsupported, message + " (in " + _file.Source + ", struct '" + ctx.TypeName + "')");
    }

    private bool Claim(StructContext ctx, string name)
    {
        if (name == ctx.TypeName)
        {
            Fail(ctx, "A member named '" + name + "' cannot have the same name as its struct in C#. Rename the HLSL member or the buffer");
            return false;
        }

        if (!ctx.Names.Add(name))
        {
            Fail(ctx, "The member name '" + name + "' is generated twice (a member named 'Size' or an array element name like 'a_0' can collide). Rename one of the HLSL members");
            return false;
        }

        return true;
    }

    private static int ScalarBytes(string kind, uint fallback)
    {
        switch (kind)
        {
            case "Int16":
            case "UInt16":
                return 2;
            case "Int64":
            case "UInt64":
            case "Double":
                return 8;
            case "Half":
                return fallback == 2 ? 2 : 4;
            default:
                return 4;
        }
    }

    private static string ScalarCs(string kind, int bytes)
    {
        switch (kind)
        {
            case "Bool": return "uint"; // HLSL stores bool as a 32-bit word in buffers.
            case "Int": return "int";
            case "UInt": return "uint";
            case "Int16": return "short";
            case "UInt16": return "ushort";
            case "Int64": return "long";
            case "UInt64": return "ulong";
            case "Half": return bytes == 2 ? "global::System.Half" : "float";
            case "Double": return "double";
            default: return "float";
        }
    }

    private void AddMember(StructContext ctx, string name, uint offset, TypeNode t, int depth)
    {
        if (ctx.Members.Count > MaxMembersPerStruct)
        {
            Fail(ctx, "Too many generated members; shorten the arrays or split the buffer");
            return;
        }

        switch (t.Kind)
        {
            case "Scalar":
            {
                string id = Esc(name);
                if (Claim(ctx, name))
                {
                    ctx.Members.Add(Fo + Lit(offset) + ")] public " + ScalarCs(t.Scalar, ScalarBytes(t.Scalar, t.Size)) + " " + id + ";");
                }

                break;
            }

            case "Vector":
                AddVector(ctx, name, offset, t);
                break;
            case "Matrix":
                AddMatrix(ctx, name, offset, t);
                break;
            case "Array":
                if (t.ArrayLength == 0 || t.Element is null)
                {
                    Fail(ctx, "Member '" + name + "' is an unsized array, which a CPU struct cannot hold");
                    return;
                }

                if (depth > 8)
                {
                    Fail(ctx, "Member '" + name + "' has too many nested array dimensions");
                    return;
                }

                for (uint i = 0; i < t.ArrayLength && !ctx.Failed; i++)
                {
                    AddMember(ctx, name + "_" + Lit(i), offset + (i * t.ArrayStride), t.Element, depth + 1);
                }

                break;
            case "Struct":
            {
                string typeName = NestedName(t);
                if (Claim(ctx, name))
                {
                    ctx.Members.Add(Fo + Lit(offset) + ")] public " + Esc(typeName) + " " + Esc(name) + ";");
                }

                break;
            }

            default:
                Fail(ctx, "Member '" + name + "' has an unknown type kind '" + t.Kind + "'");
                break;
        }
    }

    private void AddVector(StructContext ctx, string name, uint offset, TypeNode t)
    {
        if (t.Rows < 1 || t.Rows > 4)
        {
            Fail(ctx, "Member '" + name + "' is a vector with " + Lit(t.Rows) + " components");
            return;
        }

        int bytes = ScalarBytes(t.Scalar, t.Size / t.Rows);
        string elem = ScalarCs(t.Scalar, bytes);
        if (t.Rows == 1)
        {
            if (Claim(ctx, name))
            {
                ctx.Members.Add(Fo + Lit(offset) + ")] public " + elem + " " + Esc(name) + ";");
            }

            return;
        }

        if (elem == "float")
        {
            string vt = t.Rows == 2 ? V2 : t.Rows == 3 ? V3 : V4;
            if (Claim(ctx, name))
            {
                ctx.Members.Add(Fo + Lit(offset) + ")] public " + vt + " " + Esc(name) + ";");
            }

            return;
        }

        // No System.Numerics type for other element types: one field per component.
        string[] comps = { "X", "Y", "Z", "W" };
        for (uint i = 0; i < t.Rows; i++)
        {
            string cn = name + "_" + comps[i];
            if (Claim(ctx, cn))
            {
                ctx.Members.Add(Fo + Lit(offset + (i * (uint)bytes)) + ")] public " + elem + " " + cn + ";");
            }
        }
    }

    private void AddMatrix(StructContext ctx, string name, uint offset, TypeNode t)
    {
        if (t.Rows < 1 || t.Rows > 4 || t.Cols < 1 || t.Cols > 4 || ScalarBytes(t.Scalar, 4) != 4 || (t.Scalar != "Float" && t.Scalar != "Half"))
        {
            Fail(ctx, "Member '" + name + "' is a " + t.Scalar + " matrix; only float matrices of up to 4x4 can be generated. Use a float matrix or split it into vectors");
            return;
        }

        bool column = t.Packing != "RowMajor";
        uint stored = column ? t.Cols : t.Rows;   // number of stored vectors
        uint length = column ? t.Rows : t.Cols;   // components in each stored vector
        if (!column && t.Rows == 4 && t.Cols == 4 && t.MatrixStride == 16)
        {
            // Row-major 4x4 with 16-byte rows is exactly the memory layout of Matrix4x4.
            if (Claim(ctx, name))
            {
                ctx.Members.Add(Fo + Lit(offset) + ")] public " + M44 + " " + Esc(name) + ";");
            }

            return;
        }

        if (!Claim(ctx, name))
        {
            return;
        }

        string vt = length == 1 ? "float" : length == 2 ? V2 : length == 3 ? V3 : V4;
        string[] comps = { "X", "Y", "Z", "W" };
        var fields = new List<string>();
        var sb = new StringBuilder();
        for (uint k = 0; k < stored; k++)
        {
            string fn = "_" + name + (column ? "_c" : "_r") + Lit(k);
            if (!Claim(ctx, fn))
            {
                return;
            }

            fields.Add(fn);
            sb.Append(Fo).Append(Lit(offset + (k * t.MatrixStride))).Append(")] private ").Append(vt).Append(' ').Append(fn).Append(";\n");
        }

        // Element (i, j) of the C# matrix is M[i][j] in HLSL whichever way it is stored.
        sb.Append("public ").Append(M44).Append(' ').Append(Esc(name)).Append('\n');
        sb.Append("{\n");
        sb.Append("    get\n    {\n        var m = default(").Append(M44).Append(");\n");
        for (uint i = 0; i < t.Rows; i++)
        {
            for (uint j = 0; j < t.Cols; j++)
            {
                uint k = column ? j : i;
                uint c = column ? i : j;
                string src = fields[(int)k] + (length == 1 ? "" : "." + comps[c]);
                sb.Append("        m.M").Append(i + 1).Append(j + 1).Append(" = ").Append(src).Append(";\n");
            }
        }

        sb.Append("        return m;\n    }\n\n    set\n    {\n");
        for (uint k = 0; k < stored; k++)
        {
            var args = new List<string>();
            for (uint c = 0; c < length; c++)
            {
                uint i = column ? c : k;
                uint j = column ? k : c;
                args.Add("value.M" + (i + 1).ToString(CultureInfo.InvariantCulture) + (j + 1).ToString(CultureInfo.InvariantCulture));
            }

            sb.Append("        ").Append(fields[(int)k]).Append(" = ")
              .Append(length == 1 ? args[0] : "new " + vt + "(" + string.Join(", ", args) + ")").Append(";\n");
        }

        sb.Append("    }\n}");
        ctx.Members.Add(sb.ToString());
    }

    // ---- bindings ----

    private void EmitBindings()
    {
        Line("public static class Bindings");
        Line("{");
        _indent++;
        var used = new HashSet<string>(StringComparer.Ordinal) { "All", "Bindings" };
        var listed = new List<ResourceNode>();
        bool first = true;
        foreach (var r in _file.Resources)
        {
            if (!r.HasSpirVSlot)
            {
                continue;
            }

            if (!used.Add(TypeName(r.Name)))
            {
                Problem(Codes.NameClash, "Resource '" + r.Name + "' (C# class '" + TypeName(r.Name) + "') in " + _file.Source + " clashes with another generated name in 'Bindings'. Rename the resource.");
                continue;
            }

            if (!first)
            {
                Line("");
            }

            first = false;
            listed.Add(r);
            Line("public static class " + TypeName(r.Name));
            Line("{");
            _indent++;
            Line("public const uint Set = " + Num(r.Set) + ", Binding = " + Num(r.Binding) + ", Count = " + Num(r.Count) + ";");
            Line("public const global::OneShader.ResourceKind Kind = global::OneShader.ResourceKind." + r.Kind + ";");
            if (r.HasCounter)
            {
                Line("/// <summary>Binding of the hidden element counter, in the same set. Bind a 4-byte storage buffer there; zero it to start empty.</summary>");
                Line("public const uint CounterBinding = " + Num(r.CounterBinding) + ";");
            }

            _indent--;
            Line("}");
        }

        if (!first)
        {
            Line("");
        }

        Line("private static readonly global::OneShader.ResourceBinding[] s_all = new global::OneShader.ResourceBinding[]");
        Line("{");
        _indent++;
        foreach (var r in listed)
        {
            Line("new global::OneShader.ResourceBinding(" + Str(r.Name) + ", " + Num(r.Set) + ", " + Num(r.Binding) + ", " + Num(r.Count)
                + ", global::OneShader.ResourceKind." + r.Kind + ", " + StagesExpr(r.Stages) + "),");
            if (r.HasCounter)
            {
                Line("new global::OneShader.ResourceBinding(" + Str(r.Name + ".counter") + ", " + Num(r.Set) + ", " + Num(r.CounterBinding) + ", 1u"
                    + ", global::OneShader.ResourceKind.StorageBuffer, " + StagesExpr(r.Stages) + "),");
            }
        }

        _indent--;
        Line("};");
        Line("");
        Line("public static global::System.ReadOnlySpan<global::OneShader.ResourceBinding> All => s_all;");
        _indent--;
        Line("}");
    }

    private static string StagesExpr(string stages)
    {
        if (stages == "-" || stages.Length == 0)
        {
            return "global::OneShader.ShaderStages.None";
        }

        var parts = new List<string>();
        foreach (var s in stages.Split(','))
        {
            parts.Add("global::OneShader.ShaderStages." + s);
        }

        return string.Join(" | ", parts);
    }

    // ---- entry points ----

    private void EmitEntry(EntryNode e)
    {
        Line("public static class " + TypeName(e.Name));
        Line("{");
        _indent++;
        Line("public const global::OneShader.ShaderStage Stage = global::OneShader.ShaderStage." + e.Stage + ";");
        Line("public const string EntryPoint = " + Str(e.Name) + ";");
        if (_file.SpirV.TryGetValue(e.Name, out var blob))
        {
            Line("");
            EmitBytes("SpirV", blob);
        }

        if (e.Stage == "Vertex")
        {
            Line("");
            EmitVertex(e);
        }
        else if (e.Stage == "Compute")
        {
            Line("");
            Line("public const uint ThreadGroupSizeX = " + Num(e.X) + ", ThreadGroupSizeY = " + Num(e.Y) + ", ThreadGroupSizeZ = " + Num(e.Z) + ";");
        }

        _indent--;
        Line("}");
    }

    private void EmitBytes(string property, byte[] data)
    {
        // A constant byte-array literal returned as a span is stored once in the assembly's
        // data section and costs no allocation.
        Line("public static global::System.ReadOnlySpan<byte> " + property + " => new byte[]");
        Line("{");
        _indent++;
        var line = new StringBuilder();
        for (int i = 0; i < data.Length; i++)
        {
            line.Append("0x").Append(data[i].ToString("x2", CultureInfo.InvariantCulture)).Append(',');
            if ((i % 24) == 23 || i == data.Length - 1)
            {
                Line(line.ToString());
                line.Clear();
            }
            else
            {
                line.Append(' ');
            }
        }

        _indent--;
        Line("};");
    }

    private void EmitVertex(EntryNode e)
    {
        var attrs = new List<VariableNode>();
        foreach (var v in e.Inputs)
        {
            if (v.HasLocation && v.Format != "None")
            {
                attrs.Add(v);
            }
        }

        if (attrs.Count > 0)
        {
            var ctx = new StructContext("Vertex");
            ctx.Names.Remove("Size"); // a vertex has no Size constant
            foreach (var v in attrs)
            {
                string n = v.Name;
                int dot = n.LastIndexOf('.');
                string shortName = dot >= 0 ? Sanitize(n.Substring(dot + 1)) : Sanitize(n);
                if (ctx.Names.Contains(shortName))
                {
                    shortName = Sanitize(n);
                }

                AddVertexField(ctx, shortName, v);
            }

            if (ctx.Failed)
            {
                return;
            }

            Line("[global::System.Runtime.InteropServices.StructLayout(global::System.Runtime.InteropServices.LayoutKind.Explicit, Size = " + Lit(e.Stride) + ")]");
            Line("public struct Vertex");
            Line("{");
            _indent++;
            bool first = true;
            foreach (var m in ctx.Members)
            {
                if (!first)
                {
                    Line("");
                }

                first = false;
                Line(m);
            }

            _indent--;
            Line("}");
            Line("");
        }

        Line("public const uint Stride = " + Num(e.Stride) + ";");
        Line("");
        Line("private static readonly global::OneShader.VertexAttribute[] s_inputs = new global::OneShader.VertexAttribute[]");
        Line("{");
        _indent++;
        foreach (var v in attrs)
        {
            Line("new global::OneShader.VertexAttribute(" + Num(v.Location) + ", global::OneShader.VertexFormat." + v.Format + ", " + Num(v.Offset) + "),");
        }

        _indent--;
        Line("};");
        Line("");
        Line("public static global::System.ReadOnlySpan<global::OneShader.VertexAttribute> Inputs => s_inputs;");
    }

    private void AddVertexField(StructContext ctx, string name, VariableNode v)
    {
        string single;
        string comp = "";
        int count = 1;
        uint step = 0;
        switch (v.Format)
        {
            case "Float32": single = "float"; break;
            case "Float32x2": single = V2; break;
            case "Float32x3": single = V3; break;
            case "Float32x4": single = V4; break;
            case "Sint32": single = "int"; break;
            case "Uint32": single = "uint"; break;
            case "Sint32x2": single = ""; comp = "int"; count = 2; step = 4; break;
            case "Sint32x3": single = ""; comp = "int"; count = 3; step = 4; break;
            case "Sint32x4": single = ""; comp = "int"; count = 4; step = 4; break;
            case "Uint32x2": single = ""; comp = "uint"; count = 2; step = 4; break;
            case "Uint32x3": single = ""; comp = "uint"; count = 3; step = 4; break;
            case "Uint32x4": single = ""; comp = "uint"; count = 4; step = 4; break;
            case "Float16x2": single = ""; comp = "global::System.Half"; count = 2; step = 2; break;
            case "Float16x4": single = ""; comp = "global::System.Half"; count = 4; step = 2; break;

            // Packed formats are one word (or two) holding the raw bits, as the GPU reads them.
            case "Unorm8x4":
            case "Snorm8x4":
            case "Uint8x4":
            case "Sint8x4":
            case "Unorm16x2":
            case "Snorm16x2":
            case "Uint16x2":
            case "Sint16x2":
                single = "uint";
                break;
            case "Unorm16x4":
            case "Snorm16x4":
            case "Uint16x4":
            case "Sint16x4":
                single = "ulong";
                break;
            default:
                Fail(ctx, "Vertex input '" + v.Name + "' has format '" + v.Format + "', which has no C# field type");
                return;
        }

        if (single.Length > 0)
        {
            if (Claim(ctx, name))
            {
                ctx.Members.Add(Fo + Lit(v.Offset) + ")] public " + single + " " + Esc(name) + ";");
            }

            return;
        }

        string[] comps = { "X", "Y", "Z", "W" };
        for (int i = 0; i < count; i++)
        {
            string cn = name + "_" + comps[i];
            if (Claim(ctx, cn))
            {
                ctx.Members.Add(Fo + Lit(v.Offset + ((uint)i * step)) + ")] public " + comp + " " + cn + ";");
            }
        }
    }
}
