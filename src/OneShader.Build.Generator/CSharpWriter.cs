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
internal sealed partial class CSharpWriter
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
        "Stage", "EntryPoint", "SpirV", "Msl", "MslFunctionName", "MslVertexBufferIndex", "Wgsl", "WgslEntryPoint", "Vertex", "Stride", "Inputs", "ThreadGroupSizeX", "ThreadGroupSizeY", "ThreadGroupSizeZ", "SubgroupOperations", "RequiredFeatures",
    };

    private ShaderFile _file;
    private readonly string _className;
    private readonly List<GenProblem> _problems = new List<GenProblem>();
    private StringBuilder _sb = new StringBuilder();
    private HashSet<string> _topNames = new HashSet<string>(StringComparer.Ordinal);
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

        // One block is simply 'PushConstants'; several (used by different entry points) are named after their variables.
        var pushNames = new List<string>();
        foreach (var p in f.Pushes)
        {
            string csName = f.Pushes.Count == 1 ? "PushConstants" : TypeName(p.Name) + "PushConstants";
            pushNames.Add(csName);
            if (!_topNames.Add(csName))
            {
                Problem(Codes.NameClash, "The push-constant block '" + p.Name + "' (C# struct '" + csName + "') in " + f.Source + " clashes with an entry point or another generated name. Rename the variable, the function or the struct.");
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

        for (int i = 0; i < f.Pushes.Count; i++)
        {
            if (f.Pushes[i].Layout is { Kind: "Struct" } pl)
            {
                Separator(ref first);
                Line("/// <summary>The bytes of the push-constant block '" + Sanitize(f.Pushes[i].Name) + "'; push all <c>Size</c> bytes to the stages listed in <c>Bindings." + pushNames[i] + "</c>.</summary>");
                EmitStruct(pushNames[i], pl);
            }
        }

        // The queue grows while structs are emitted (a struct can contain another).
        for (int i = 0; i < _structQueue.Count; i++)
        {
            Separator(ref first);
            EmitStruct(_structQueue[i].Key, _structQueue[i].Value);
        }

        Separator(ref first);
        EmitBindings(pushNames);
        foreach (var e in f.Entries)
        {
            Line("");
            EmitEntry(e);
        }

        EmitPipeline();
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
        for (int n = 1; !ClaimTopName(name); n++)
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

    private void EmitBindings(List<string> pushNames)
    {
        Line("public static class Bindings");
        Line("{");
        _indent++;
        var used = new HashSet<string>(StringComparer.Ordinal) { "All", "Bindings" };
        foreach (string pn in pushNames)
        {
            used.Add(pn);
        }

        var listed = new List<ResourceNode>();
        bool first = true;
        foreach (var r in _file.Resources)
        {
            if (!r.HasSlot)
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
            if (r.HasMsl)
            {
                Line("/// <summary>Metal argument index, counted within the resource's class (buffer, texture or sampler).</summary>");
                Line("public const uint MslIndex = " + Num(r.MslIndex) + ";");
            }

            Line("public const global::OneShader.ResourceKind Kind = global::OneShader.ResourceKind." + r.Kind + ";");
            if (r.HasCounter)
            {
                Line("/// <summary>Binding of the hidden element counter, in the same set. Bind a 4-byte storage buffer there; zero it to start empty.</summary>");
                Line("public const uint CounterBinding = " + Num(r.CounterBinding) + ";");
                if (r.HasMslCounter)
                {
                    Line("/// <summary>Metal buffer index of the hidden element counter.</summary>");
                    Line("public const uint MslCounterIndex = " + Num(r.MslCounterIndex) + ";");
                }
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
                + ", global::OneShader.ResourceKind." + r.Kind + ", " + StagesExpr(r.Stages) + (r.HasMsl ? ", " + Num(r.MslIndex) : "") + "),");
            if (r.HasCounter)
            {
                Line("new global::OneShader.ResourceBinding(" + Str(r.Name + ".counter") + ", " + Num(r.Set) + ", " + Num(r.CounterBinding) + ", 1u"
                    + ", global::OneShader.ResourceKind.StorageBuffer, " + StagesExpr(r.Stages) + (r.HasMslCounter ? ", " + Num(r.MslCounterIndex) : "") + "),");
            }
        }

        _indent--;
        Line("};");
        Line("");
        Line("public static global::System.ReadOnlySpan<global::OneShader.ResourceBinding> All => s_all;");
        for (int i = 0; i < _file.Pushes.Count; i++)
        {
            var p = _file.Pushes[i];
            Line("");
            Line("/// <summary>How to deliver the push-constant block '" + Sanitize(p.Name) + "': a Vulkan push-constant range, a Metal inline buffer or a WebGPU uniform buffer.</summary>");
            Line("public static class " + pushNames[i]);
            Line("{");
            _indent++;
            Line("/// <summary>Bytes to push.</summary>");
            Line("public const uint Size = " + Num(p.Layout?.Size ?? 0) + ";");
            Line("/// <summary>The <c>VkPushConstantRange</c> stage flags (offset 0, size <c>Size</c>).</summary>");
            Line("public const global::OneShader.ShaderStages Stages = " + StagesExpr(p.Stages) + ";");
            if (p.HasMsl)
            {
                Line("/// <summary>Metal buffer index: <c>setVertexBytes</c>, <c>setFragmentBytes</c> or <c>setBytes</c> with the struct's bytes at this index.</summary>");
                Line("public const uint MslIndex = " + Num(p.MslIndex) + ";");
            }

            if (p.HasWgsl)
            {
                Line("/// <summary>WebGPU: a uniform buffer at this group and binding, at least <c>Size</c> rounded up to 16 bytes long.</summary>");
                Line("public const uint WgslGroup = " + Num(p.WgslGroup) + ", WgslBinding = " + Num(p.WgslBinding) + ";");
            }

            _indent--;
            Line("}");
        }

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

        if (_file.Msl.TryGetValue(e.Name, out var msl))
        {
            Line("");
            Line("/// <summary>UTF-8 Metal Shading Language source.</summary>");
            EmitBytes("Msl", msl.Data);
            Line("");
            Line("/// <summary>The function to look up in the compiled library (differs from <see cref=\"EntryPoint\"/> when MSL reserves the name).</summary>");
            Line("public const string MslFunctionName = " + Str(msl.Function) + ";");
            if (msl.HasVertexBuffer)
            {
                Line("/// <summary>Metal buffer index of the interleaved vertex buffer (layout: <c>Stride</c> and <c>Inputs</c>).</summary>");
                Line("public const uint MslVertexBufferIndex = " + Num(msl.VertexBuffer) + ";");
            }
        }

        if (_file.Wgsl.TryGetValue(e.Name, out var wgsl))
        {
            Line("");
            Line("/// <summary>UTF-8 WGSL source.</summary>");
            EmitBytes("Wgsl", wgsl.Data);
            Line("");
            Line("/// <summary>The entry point name in the WGSL source (differs from <see cref=\"EntryPoint\"/> when WGSL reserves the name).</summary>");
            Line("public const string WgslEntryPoint = " + Str(wgsl.Function) + ";");
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

        if (e.Subgroup != 0)
        {
            Line("");
            Line("/// <summary>The subgroup (wave) operations this entry point uses: a Vulkan host checks them against <c>subgroupSupportedOperations</c>, a WebGPU host requests the <c>subgroups</c> feature, a Metal host needs SIMD-group support.</summary>");
            Line("public const global::OneShader.Reflection.SubgroupOperations SubgroupOperations = (global::OneShader.Reflection.SubgroupOperations)" + Num(e.Subgroup) + ";");
        }

        if (e.Features != 0)
        {
            Line("");
            Line("/// <summary>The optional device features this entry point needs for 16-bit and 64-bit types: the Vulkan features to enable, <c>shader-f16</c> for a WebGPU device.</summary>");
            Line("public const global::OneShader.Reflection.DeviceFeatures RequiredFeatures = (global::OneShader.Reflection.DeviceFeatures)" + Num(e.Features) + ";");
        }

        // An entry point that happens to be called Layout would clash with its own member; it just goes without.
        if (TypeName(e.Name) != "Layout")
        {
            Line("");
            EmitEntryLayout(e);
        }

        _indent--;
        Line("}");
    }

    // A file with one vertex and one pixel entry point (or a single compute entry point) has an obvious pipeline. The name is taken only
    // when free, so a file that already uses 'Pipeline' for something else keeps compiling.
    private void EmitPipeline()
    {
        EntryNode? vs = null, ps = null, cs = null;
        int count = 0;
        foreach (var e in _file.Entries)
        {
            if (TypeName(e.Name) == "Layout")
            {
                return;
            }

            count++;
            if (e.Stage == "Vertex" && vs == null)
            {
                vs = e;
            }
            else if (e.Stage == "Pixel" && ps == null)
            {
                ps = e;
            }
            else if (e.Stage == "Compute" && cs == null)
            {
                cs = e;
            }
            else
            {
                return;
            }
        }

        bool graphics = vs != null && ps != null && cs == null && count == 2;
        bool compute = cs != null && vs == null && ps == null && count == 1;
        if ((!graphics && !compute) || !_topNames.Add("Pipeline"))
        {
            return;
        }

        string args = graphics ? TypeName(vs!.Name) + ".Layout, " + TypeName(ps!.Name) + ".Layout" : TypeName(cs!.Name) + ".Layout";
        Line("");
        Line("private static global::OneShader.Layouts.PipelineLayout? s_pipeline;");
        Line("");
        Line("/// <summary>The pipeline layout of this file's " + (graphics ? "vertex and pixel" : "compute") + " entry point" + (graphics ? "s" : "") + " as plain data; convert it with <c>ToVulkan</c>, <c>TryToMetal</c> or <c>TryToWebGpu</c>.</summary>");
        Line("public static global::OneShader.Layouts.PipelineLayout Pipeline => s_pipeline ??= global::OneShader.Layouts.PipelineLayout.Create(new global::OneShader.Layouts.EntryLayout[] { " + args + " });");
        _pipelineEmitted = true;
    }

    // The entry point's share of the bindings as plain data, so a host can build a pipeline layout without parsing the reflection.
    private void EmitEntryLayout(EntryNode e)
    {
        string stageFlag = "global::OneShader.ShaderStages." + e.Stage;
        Line("private static global::OneShader.Layouts.EntryLayout? s_layout;");
        Line("");
        Line("/// <summary>The resources, push-constant block and vertex input of this entry point as plain data; combine entry points with <c>PipelineLayout</c>.</summary>");
        Line("public static global::OneShader.Layouts.EntryLayout Layout => s_layout ??= CreateLayout();");
        Line("");
        Line("private static global::OneShader.Layouts.EntryLayout CreateLayout()");
        Line("{");
        _indent++;
        Line("var resources = new global::OneShader.Layouts.ResourceLayout[]");
        Line("{");
        _indent++;
        foreach (string used in e.Uses)
        {
            foreach (var r in _file.Resources)
            {
                if (r.Name != used || !r.HasSlot)
                {
                    continue;
                }

                string texel = r.Layout != null && (r.Layout.Kind == "Scalar" || r.Layout.Kind == "Vector") ? r.Layout.Scalar : "Float";
                Line("new global::OneShader.Layouts.ResourceLayout(" + Str(r.Name) + ", " + Num(r.Set) + ", " + Num(r.Binding) + ", " + Num(r.Count)
                    + ", global::OneShader.ResourceKind." + r.Kind + ", " + stageFlag + ", " + (r.HasMsl ? Num(r.MslIndex) : "global::OneShader.Layouts.LayoutSlots.None")
                    + ", global::OneShader.Types.ResourceTypeKind." + r.ResourceType + ", global::OneShader.Types.ScalarKind." + texel + ", "
                    + (r.Hlsl == "SamplerComparisonState" ? "true" : "false") + ", " + Str(r.ImageFormat) + "),");
                if (r.HasCounter)
                {
                    Line("new global::OneShader.Layouts.ResourceLayout(" + Str(r.Name + ".counter") + ", " + Num(r.Set) + ", " + Num(r.CounterBinding) + ", 1u"
                        + ", global::OneShader.ResourceKind.StorageBuffer, " + stageFlag + ", " + (r.HasMslCounter ? Num(r.MslCounterIndex) : "global::OneShader.Layouts.LayoutSlots.None")
                        + ", global::OneShader.Types.ResourceTypeKind.RWStructuredBuffer, global::OneShader.Types.ScalarKind.Float, false, \"\"),");
                }
            }
        }

        _indent--;
        Line("};");
        string push = "null";
        foreach (var p in _file.Pushes)
        {
            if (p.Name == e.PushUse)
            {
                push = "new global::OneShader.Layouts.PushConstantLayout(" + Str(p.Name) + ", " + Num(p.Layout?.Size ?? 0) + ", " + stageFlag + ", "
                    + (p.HasMsl ? Num(p.MslIndex) : "global::OneShader.Layouts.LayoutSlots.None") + ", "
                    + (p.HasWgsl ? Num(p.WgslGroup) + ", " + Num(p.WgslBinding) : "global::OneShader.Layouts.LayoutSlots.None, global::OneShader.Layouts.LayoutSlots.None") + ")";
            }
        }

        string vertex = e.Stage == "Vertex" ? "new global::OneShader.Layouts.VertexLayout(Stride, s_inputs)" : "null";
        string group = e.Stage == "Compute" ? "(ThreadGroupSizeX, ThreadGroupSizeY, ThreadGroupSizeZ)" : "default";
        Line("return new global::OneShader.Layouts.EntryLayout(EntryPoint, Stage, resources, " + push + ", " + vertex + ", " + group + (e.Subgroup == 0 ? "" : ", SubgroupOperations") + (e.Features == 0 ? "" : ", requiredFeatures: RequiredFeatures") + ");");
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

    private static List<VariableNode> VertexAttributes(EntryNode e)
    {
        var attrs = new List<VariableNode>();
        foreach (var v in e.Inputs)
        {
            if (v.HasLocation && v.Format != "None")
            {
                attrs.Add(v);
            }
        }

        return attrs;
    }

    // Returns false when a field could not be generated (a problem has been recorded).
    private bool EmitVertexStruct(EntryNode e, List<VariableNode> attrs, string structName)
    {
        var ctx = new StructContext(structName);
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
            return false;
        }

        Line("[global::System.Runtime.InteropServices.StructLayout(global::System.Runtime.InteropServices.LayoutKind.Explicit, Size = " + Lit(e.Stride) + ")]");
        Line("public struct " + structName);
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
        return true;
    }

    private void EmitVertex(EntryNode e)
    {
        var attrs = VertexAttributes(e);
        if (attrs.Count > 0 && !HasSharedVertex(e))
        {
            if (!EmitVertexStruct(e, attrs, "Vertex"))
            {
                return;
            }

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
