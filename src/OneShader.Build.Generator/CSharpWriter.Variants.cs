using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OneShader.Build.Generator;

// Shader variants: one class for a file whose build output is several compiles of the same HLSL under different defines.
internal sealed partial class CSharpWriter
{
    /// <summary>One compiled variant: its position, the names it is known by and what the compile produced.</summary>
    internal sealed class VariantInfo
    {
        public VariantInfo(VariantRef reference, ShaderFile file)
        {
            Ref = reference;
            File = file;
        }

        public VariantRef Ref { get; }
        public ShaderFile File { get; }
        public string ClassName { get; set; } = "";
        public bool HasPipeline { get; set; }
    }

    private sealed class AxisInfo
    {
        public string Property = "";
        public string Parameter = "";
        public string EnumName = "";
        public bool IsFlag;
        public List<string> Members = new List<string>();
    }

    private sealed class EntryUnion
    {
        public string Name = "";
        public bool Always;
        public EntryNode First = null!;
    }

    // Names of the variant set, shared by the parts that are written separately.
    private HashSet<string>? _fileNames;
    private readonly HashSet<string> _sharedStructs = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sharedVertex = new Dictionary<string, string>(StringComparer.Ordinal);
    private bool _pipelineEmitted;

    private static readonly HashSet<string> VariantMembers = new HashSet<string>(StringComparer.Ordinal)
    {
        "Index", "Name", "Defines", "Key", "Bindings", "Reflection", "Pipeline",
    };

    private static readonly HashSet<string> KeyMembers = new HashSet<string>(StringComparer.Ordinal)
    {
        "Index", "Equals", "GetHashCode", "ToString", "Key",
    };

    private static readonly HashSet<string> VariantsMembers = new HashSet<string>(StringComparer.Ordinal)
    {
        "Count", "All", "Get", "TryGet", "CreateAll",
    };

    // Inside a variant set a nested struct name must also be free at file level, so every variant sees the same type.
    private bool ClaimTopName(string name)
    {
        if (!_topNames.Add(name))
        {
            return false;
        }

        _fileNames?.Add(name);
        return true;
    }

    private bool HasSharedVertex(EntryNode e) => _sharedVertex.ContainsKey(e.Name);

    /// <summary>
    /// Generates the class for a file compiled in several variants. <paramref name="manifest"/> lists the variants and the axes they come from;
    /// <paramref name="files"/> holds each variant's compile, in manifest order.
    /// </summary>
    public static string? GenerateVariants(ShaderFile manifest, IReadOnlyList<ShaderFile> files, out List<GenProblem> problems)
    {
        var w = new CSharpWriter(files[0], ClassNameFor(manifest));
        var infos = new List<VariantInfo>();
        for (int i = 0; i < files.Count; i++)
        {
            infos.Add(new VariantInfo(manifest.VariantRefs[i], files[i]));
        }

        string? text = w.RunVariants(manifest, infos);
        problems = w._problems;
        return w._problems.Count == 0 ? text : null;
    }

    // PascalCase for an all-capitals macro name or value (ALPHA_TEST gives AlphaTest); other spellings are kept.
    private static string Pascal(string name)
    {
        foreach (char c in name)
        {
            if (char.IsLower(c))
            {
                return TypeName(name);
            }
        }

        var sb = new StringBuilder();
        bool start = true;
        foreach (char c in name)
        {
            if (!char.IsLetterOrDigit(c))
            {
                start = true;
                continue;
            }

            sb.Append(start ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
            start = false;
        }

        return Sanitize(sb.ToString());
    }

    private static string CamelParameter(string pascal)
    {
        string p = pascal.Length > 0 ? char.ToLowerInvariant(pascal[0]) + pascal.Substring(1) : "value";
        return Esc(p);
    }

    private string? RunVariants(ShaderFile manifest, List<VariantInfo> variants)
    {
        _fileNames = new HashSet<string>(StringComparer.Ordinal) { _className, "Key", "Variant", "Entry", "Variants", "Lookup", "Bindings", "Reflection", "ReflectionData" };
        bool named = manifest.VariantMode == "names";

        var axes = BuildAxes(manifest, variants, named);
        if (axes is null)
        {
            return null;
        }

        // Class names of the per-variant data: the axis values joined, or the declared name.
        var usedClasses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in variants)
        {
            string cls;
            if (named)
            {
                cls = TypeName(v.Ref.Name);
            }
            else
            {
                var parts = new List<string>();
                for (int a = 0; a < axes.Count; a++)
                {
                    parts.Add(axes[a].IsFlag
                        ? axes[a].Property + (v.Ref.Ordinals[a] == 1 ? "On" : "Off")
                        : axes[a].Property + axes[a].Members[(int)v.Ref.Ordinals[a]]);
                }

                cls = string.Join("_", parts);
            }

            while (_fileNames.Contains(cls) || VariantsMembers.Contains(cls) || !usedClasses.Add(cls))
            {
                cls += "_";
            }

            v.ClassName = cls;
            _fileNames.Add(cls);
        }

        // Entry points across variants, in first-seen order.
        var union = new List<EntryUnion>();
        var unionIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var v in variants)
        {
            foreach (var e in v.File.Entries)
            {
                string key = TypeName(e.Name);
                if (!unionIndex.TryGetValue(key, out int at))
                {
                    unionIndex[key] = union.Count;
                    union.Add(new EntryUnion { Name = key, First = e });
                }
            }
        }

        foreach (var u in union)
        {
            u.Always = true;
            foreach (var v in variants)
            {
                bool has = false;
                foreach (var e in v.File.Entries)
                {
                    has |= TypeName(e.Name) == u.Name;
                }

                u.Always &= has;
            }

            if (VariantMembers.Contains(u.Name) || EntryMembers.Contains(u.Name))
            {
                Problem(Codes.NameClash, "Entry point '" + u.First.Name + "' (C# class '" + u.Name + "') in " + _file.Source + " clashes with a member of the generated Variant class. Rename the HLSL function.");
            }
        }

        if (_problems.Count > 0)
        {
            return null;
        }

        DecideSharing(variants);

        // Part: Variants (written first because it fills the nested-struct queue and the pipeline flags).
        var variantsText = new StringBuilder();
        _sb = variantsText;
        _indent = 1;
        EmitVariantsClass(variants, axes, union, named);
        if (_problems.Count > 0)
        {
            return null;
        }

        var lookupText = new StringBuilder();
        _sb = lookupText;
        _indent = 1;
        EmitLookup(variants, union);

        var typesText = new StringBuilder();
        _sb = typesText;
        _indent = 1;
        EmitAxisTypes(axes);
        EmitKey(axes, variants.Count);
        Line("");
        EmitEntryClass(variants, union);
        Line("");
        EmitVariantClass(variants, union);

        // Shared structs last: emitting them can queue nested structs, which are drained after.
        var sharedText = new StringBuilder();
        _sb = sharedText;
        _indent = 1;
        _topNames = new HashSet<string>(_fileNames, StringComparer.Ordinal);
        _file = variants[0].File;
        EmitSharedStructs();
        if (_problems.Count > 0)
        {
            return null;
        }

        var all = new StringBuilder();
        _sb = all;
        _indent = 0;
        Line("// <auto-generated>");
        Line("// Generated by OneShader from " + manifest.Source.Replace('\n', ' ').Replace('\r', ' ') + ", compiled in " + variants.Count.ToString(CultureInfo.InvariantCulture) + " variants. Changes are lost when the shader is built again.");
        Line("// </auto-generated>");
        Line("#nullable enable");
        Line("#pragma warning disable CS1591 // generated members are described by the shader, not by XML comments");
        Line("");
        if (manifest.Namespace.Length > 0)
        {
            Line("namespace " + manifest.Namespace + ";");
            Line("");
        }

        Line("public static partial class " + Esc(_className));
        Line("{");
        if (sharedText.Length > 0)
        {
            all.Append(sharedText).Append('\n');
        }

        all.Append(typesText).Append('\n').Append(lookupText).Append('\n').Append(variantsText);
        Line("}");
        return all.ToString();
    }

    // ---- axes ----

    private List<AxisInfo>? BuildAxes(ShaderFile manifest, List<VariantInfo> variants, bool named)
    {
        var axes = new List<AxisInfo>();
        if (named)
        {
            var axis = new AxisInfo { Property = "Name", Parameter = "name", EnumName = "VariantName" };
            foreach (var v in variants)
            {
                axis.Members.Add(TypeName(v.Ref.Name));
            }

            axes.Add(axis);
            foreach (var v in variants)
            {
                v.Ref.Ordinals.Clear();
                v.Ref.Ordinals.Add(v.Ref.Index);
            }
        }
        else
        {
            foreach (var a in manifest.Axes)
            {
                var axis = new AxisInfo { Property = Pascal(a.Name), IsFlag = a.IsFlag };
                axis.Parameter = CamelParameter(axis.Property);
                axis.EnumName = axis.Property + "Value";
                foreach (string value in a.Values)
                {
                    axis.Members.Add(Pascal(value));
                }

                axes.Add(axis);
            }

            foreach (var v in variants)
            {
                if (v.Ref.Ordinals.Count != axes.Count)
                {
                    Problem(Codes.BadFile, "The variant list of " + manifest.Source + " does not match its axes. Rebuild the project; if it persists, delete the obj/ folder.");
                    return null;
                }

                for (int i = 0; i < axes.Count; i++)
                {
                    if (v.Ref.Ordinals[i] >= (axes[i].IsFlag ? 2u : (uint)axes[i].Members.Count))
                    {
                        Problem(Codes.BadFile, "The variant list of " + manifest.Source + " does not match its axes. Rebuild the project; if it persists, delete the obj/ folder.");
                        return null;
                    }
                }
            }
        }

        var props = new HashSet<string>(KeyMembers, StringComparer.Ordinal);
        foreach (var axis in axes)
        {
            if (!props.Add(axis.Property))
            {
                Problem(Codes.NameClash, "Axis '" + axis.Property + "' of " + manifest.Source + " clashes with another axis or with a member of the generated Key struct. Rename the axis macro.");
                return null;
            }

            if (axis.IsFlag)
            {
                continue;
            }

            if (!_fileNames!.Add(axis.EnumName))
            {
                Problem(Codes.NameClash, "The type for axis '" + axis.Property + "' (C# enum '" + axis.EnumName + "') in " + manifest.Source + " clashes with another generated name. Rename the axis macro or the HLSL declaration it collides with.");
                return null;
            }

            var members = new HashSet<string>(StringComparer.Ordinal);
            foreach (string m in axis.Members)
            {
                if (!members.Add(m))
                {
                    Problem(Codes.NameClash, "Values of axis '" + axis.Property + "' in " + manifest.Source + " become the same C# name '" + m + "'. Rename one of the values.");
                    return null;
                }
            }
        }

        return axes;
    }

    private void EmitAxisTypes(List<AxisInfo> axes)
    {
        bool first = true;
        foreach (var axis in axes)
        {
            if (axis.IsFlag)
            {
                continue;
            }

            if (!first)
            {
                Line("");
            }

            first = false;
            Line("/// <summary>The values of the " + axis.Property + " axis, in the order they were declared.</summary>");
            Line("public enum " + axis.EnumName);
            Line("{");
            _indent++;
            for (int i = 0; i < axis.Members.Count; i++)
            {
                Line(Esc(axis.Members[i]) + " = " + i.ToString(CultureInfo.InvariantCulture) + ",");
            }

            _indent--;
            Line("}");
        }

        if (!first)
        {
            Line("");
        }
    }

    private static string AxisType(AxisInfo a) => a.IsFlag ? "bool" : a.EnumName;

    private static string AxisCount(AxisInfo a) => (a.IsFlag ? 2 : a.Members.Count).ToString(CultureInfo.InvariantCulture);

    private void EmitKey(List<AxisInfo> axes, int count)
    {
        var args = new List<string>();
        foreach (var a in axes)
        {
            args.Add(AxisType(a) + " " + a.Parameter);
        }

        Line("/// <summary>Picks a variant: one value for each axis. <c>Variants.Get</c> turns it into the variant without allocating.</summary>");
        Line("public readonly struct Key : global::System.IEquatable<Key>");
        Line("{");
        _indent++;
        Line("public Key(" + string.Join(", ", args) + ")");
        Line("{");
        _indent++;
        foreach (var a in axes)
        {
            Line(a.Property + " = " + a.Parameter + ";");
        }

        Line("int index = 0;");
        Line("bool valid = true;");
        foreach (var a in axes)
        {
            if (a.IsFlag)
            {
                Line("index = (index * 2) + (" + a.Parameter + " ? 1 : 0);");
            }
            else
            {
                Line("valid &= (uint)" + a.Parameter + " < " + AxisCount(a) + "u;");
                Line("index = (index * " + AxisCount(a) + ") + (int)" + a.Parameter + ";");
            }
        }

        Line("Index = valid ? index : -1;");
        _indent--;
        Line("}");
        foreach (var a in axes)
        {
            Line("");
            Line("public " + AxisType(a) + " " + a.Property + " { get; }");
        }

        Line("");
        Line("/// <summary>The variant's position in <c>Variants.All</c>, or -1 when a value is outside its enum.</summary>");
        Line("public int Index { get; }");
        Line("");
        var eq = new List<string>();
        foreach (var a in axes)
        {
            eq.Add(a.Property + " == other." + a.Property);
        }

        Line("public bool Equals(Key other) => " + string.Join(" && ", eq) + ";");
        Line("");
        Line("public override bool Equals(object? obj) => obj is Key other && Equals(other);");
        Line("");
        Line("public override int GetHashCode() => Index;");
        Line("");
        Line("public static bool operator ==(Key left, Key right) => left.Equals(right);");
        Line("");
        Line("public static bool operator !=(Key left, Key right) => !left.Equals(right);");
        Line("");
        Line("public override string ToString() => (uint)Index < " + count.ToString(CultureInfo.InvariantCulture) + "u ? Variants.All[Index].Name : \"invalid\";");
        _indent--;
        Line("}");
    }

    // ---- entries and variants ----

    private static bool Any(List<VariantInfo> variants, Func<ShaderFile, EntryNode, bool> test)
    {
        foreach (var v in variants)
        {
            foreach (var e in v.File.Entries)
            {
                if (test(v.File, e))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void EmitEntryClass(List<VariantInfo> variants, List<EntryUnion> union)
    {
        bool spirv = Any(variants, (f, e) => f.SpirV.ContainsKey(e.Name));
        bool msl = Any(variants, (f, e) => f.Msl.ContainsKey(e.Name));
        bool mslVb = Any(variants, (f, e) => f.Msl.TryGetValue(e.Name, out var b) && b.HasVertexBuffer);
        bool wgsl = Any(variants, (f, e) => f.Wgsl.ContainsKey(e.Name));
        bool compute = Any(variants, (f, e) => e.Stage == "Compute");
        bool vertex = Any(variants, (f, e) => e.Stage == "Vertex");
        bool subgroup = Any(variants, (f, e) => e.Subgroup != 0);
        bool layout = HasLayout(union);

        Line("/// <summary>One entry point of one variant: its code for each selected target, and what a host needs to create the pipeline stage.</summary>");
        Line("public sealed class Entry");
        Line("{");
        _indent++;
        Line("private readonly int _id;");
        Line("");
        Line("internal Entry(int id, global::OneShader.ShaderStage stage, string entryPoint, string? mslFunctionName, uint? mslVertexBufferIndex, string? wgslEntryPoint, uint stride, uint threadGroupSizeX, uint threadGroupSizeY, uint threadGroupSizeZ, uint subgroupOperations)");
        Line("{");
        _indent++;
        Line("_id = id;");
        Line("Stage = stage;");
        Line("EntryPoint = entryPoint;");
        if (msl)
        {
            Line("MslFunctionName = mslFunctionName;");
        }

        if (mslVb)
        {
            Line("MslVertexBufferIndex = mslVertexBufferIndex;");
        }

        if (wgsl)
        {
            Line("WgslEntryPoint = wgslEntryPoint;");
        }

        if (vertex)
        {
            Line("Stride = stride;");
        }

        if (compute)
        {
            Line("ThreadGroupSizeX = threadGroupSizeX;");
            Line("ThreadGroupSizeY = threadGroupSizeY;");
            Line("ThreadGroupSizeZ = threadGroupSizeZ;");
        }

        if (subgroup)
        {
            Line("SubgroupOperations = (global::OneShader.Reflection.SubgroupOperations)subgroupOperations;");
        }

        _indent--;
        Line("}");
        Line("");
        Line("public global::OneShader.ShaderStage Stage { get; }");
        Line("");
        Line("public string EntryPoint { get; }");
        if (spirv)
        {
            Line("");
            Line("/// <summary>The SPIR-V words (little-endian bytes) over data embedded in the assembly; empty when SpirV was not selected.</summary>");
            Line("public global::System.ReadOnlySpan<byte> SpirV => Lookup.SpirV(_id);");
        }

        if (msl)
        {
            Line("");
            Line("/// <summary>UTF-8 Metal Shading Language source; empty when Msl was not selected.</summary>");
            Line("public global::System.ReadOnlySpan<byte> Msl => Lookup.Msl(_id);");
            Line("");
            Line("/// <summary>The function to look up in the compiled library; null when Msl was not selected.</summary>");
            Line("public string? MslFunctionName { get; }");
        }

        if (mslVb)
        {
            Line("");
            Line("/// <summary>Metal buffer index of the interleaved vertex buffer; null for entry points without vertex inputs.</summary>");
            Line("public uint? MslVertexBufferIndex { get; }");
        }

        if (wgsl)
        {
            Line("");
            Line("/// <summary>UTF-8 WGSL source; empty when Wgsl was not selected.</summary>");
            Line("public global::System.ReadOnlySpan<byte> Wgsl => Lookup.Wgsl(_id);");
            Line("");
            Line("/// <summary>The entry point name in the WGSL source; null when Wgsl was not selected.</summary>");
            Line("public string? WgslEntryPoint { get; }");
        }

        if (vertex)
        {
            Line("");
            Line("/// <summary>Bytes between vertices of the interleaved vertex buffer; 0 for entry points without vertex inputs.</summary>");
            Line("public uint Stride { get; }");
            Line("");
            Line("/// <summary>The vertex attributes; empty for entry points without vertex inputs.</summary>");
            Line("public global::System.ReadOnlySpan<global::OneShader.VertexAttribute> Inputs => Lookup.Inputs(_id);");
        }

        if (compute)
        {
            Line("");
            Line("/// <summary>The <c>[numthreads]</c> size; 0 for entry points that are not compute shaders.</summary>");
            Line("public uint ThreadGroupSizeX { get; }");
            Line("");
            Line("public uint ThreadGroupSizeY { get; }");
            Line("");
            Line("public uint ThreadGroupSizeZ { get; }");
        }

        if (subgroup)
        {
            Line("");
            Line("public global::OneShader.Reflection.SubgroupOperations SubgroupOperations { get; }");
        }

        if (Any(variants, (f, e) => e.SpecUses.Count > 0))
        {
            Line("");
            Line("/// <summary>The <c>constant_id</c>s of the specialization constants this entry point reads in this variant; empty when it reads none.</summary>");
            Line("public global::System.ReadOnlySpan<uint> SpecConstantIds => Lookup.SpecConstantIds(_id);");
        }

        if (layout)
        {
            Line("");
            Line("/// <summary>The resources, push-constant block and vertex input of this entry point as plain data.</summary>");
            Line("public global::OneShader.Layouts.EntryLayout Layout => Lookup.Layout(_id);");
        }

        _indent--;
        Line("}");
    }

    private static bool HasLayout(List<EntryUnion> union)
    {
        // An entry point called Layout does without a Layout member, as in a single-variant file.
        foreach (var u in union)
        {
            if (u.Name == "Layout")
            {
                return false;
            }
        }

        return true;
    }

    private void EmitVariantClass(List<VariantInfo> variants, List<EntryUnion> union)
    {
        bool pipeline = false;
        foreach (var v in variants)
        {
            pipeline |= v.HasPipeline;
        }

        Line("/// <summary>One compiled variant: the define set it was built with and its entry points. Bindings can differ between variants; read them from here.</summary>");
        Line("public sealed class Variant");
        Line("{");
        _indent++;
        Line("private readonly Entry?[] _entries;");
        Line("");
        Line("internal Variant(int index, string name, string defines, Key key, Entry?[] entries)");
        Line("{");
        _indent++;
        Line("Index = index;");
        Line("Name = name;");
        Line("Defines = defines;");
        Line("Key = key;");
        Line("_entries = entries;");
        _indent--;
        Line("}");
        Line("");
        Line("/// <summary>The position in <c>Variants.All</c>.</summary>");
        Line("public int Index { get; }");
        Line("");
        Line("/// <summary>A readable name made from the axis values (or the declared name); also the name of the class under <c>Variants</c> that holds this variant's data.</summary>");
        Line("public string Name { get; }");
        Line("");
        Line("/// <summary>The macros this variant was compiled with, as <c>NAME=value</c> separated by spaces.</summary>");
        Line("public string Defines { get; }");
        Line("");
        Line("public Key Key { get; }");
        Line("");
        Line("/// <summary>The resources this variant binds, with their slots for each selected target.</summary>");
        Line("public global::System.ReadOnlySpan<global::OneShader.ResourceBinding> Bindings => Lookup.Bindings(Index);");
        Line("");
        Line("/// <summary>The reflection of this variant, parsed on first use.</summary>");
        Line("public global::OneShader.Reflection.ShaderReflection Reflection => Lookup.Reflection(Index);");
        if (pipeline)
        {
            Line("");
            Line("/// <summary>The pipeline layout as plain data when this variant is a vertex and pixel pair or a single compute entry point; otherwise null.</summary>");
            Line("public global::OneShader.Layouts.PipelineLayout? Pipeline => Lookup.Pipeline(Index);");
        }

        for (int i = 0; i < union.Count; i++)
        {
            Line("");
            string idx = i.ToString(CultureInfo.InvariantCulture);
            if (union[i].Always)
            {
                Line("public Entry " + Esc(union[i].Name) + " => _entries[" + idx + "]!;");
            }
            else
            {
                Line("/// <summary>Null in variants that do not contain this entry point.</summary>");
                Line("public Entry? " + Esc(union[i].Name) + " => _entries[" + idx + "];");
            }
        }

        _indent--;
        Line("}");
    }

    // ---- Lookup: switches from (variant, entry) to the per-variant static data ----

    private void EmitLookup(List<VariantInfo> variants, List<EntryUnion> union)
    {
        int entryCount = union.Count;
        Line("// Maps a variant (and entry point) number to the static data of that variant. A switch over constants, so a lookup costs no allocation.");
        Line("internal static class Lookup");
        Line("{");
        _indent++;
        EmitSpanSwitch("SpirV", "byte", variants, union, (f, e) => f.SpirV.ContainsKey(e.Name), "SpirV");
        Line("");
        EmitSpanSwitch("Msl", "byte", variants, union, (f, e) => f.Msl.ContainsKey(e.Name), "Msl");
        Line("");
        EmitSpanSwitch("Wgsl", "byte", variants, union, (f, e) => f.Wgsl.ContainsKey(e.Name), "Wgsl");
        Line("");
        EmitSpanSwitch("Inputs", "global::OneShader.VertexAttribute", variants, union, (f, e) => e.Stage == "Vertex", "Inputs");
        if (Any(variants, (f, e) => e.SpecUses.Count > 0))
        {
            Line("");
            EmitSpanSwitch("SpecConstantIds", "uint", variants, union, (f, e) => e.SpecUses.Count > 0, "SpecConstantIds");
        }
        if (HasLayout(union))
        {
            Line("");
            Line("internal static global::OneShader.Layouts.EntryLayout Layout(int id)");
            Line("{");
            _indent++;
            Line("switch (id)");
            Line("{");
            _indent++;
            ForEachEntry(variants, union, entryCount, (id, v, u, e) => true, (id, v, u) =>
            {
                Line("case " + id.ToString(CultureInfo.InvariantCulture) + ": return Variants." + v.ClassName + "." + Esc(u.Name) + ".Layout;");
            });
            Line("default: throw new global::System.ArgumentOutOfRangeException(nameof(id));");
            _indent--;
            Line("}");
            _indent--;
            Line("}");
        }

        Line("");
        Line("internal static global::System.ReadOnlySpan<global::OneShader.ResourceBinding> Bindings(int variant)");
        Line("{");
        _indent++;
        Line("switch (variant)");
        Line("{");
        _indent++;
        for (int i = 0; i < variants.Count; i++)
        {
            Line("case " + i.ToString(CultureInfo.InvariantCulture) + ": return Variants." + variants[i].ClassName + ".Bindings.All;");
        }

        Line("default: throw new global::System.ArgumentOutOfRangeException(nameof(variant));");
        _indent--;
        Line("}");
        _indent--;
        Line("}");
        Line("");
        Line("internal static global::OneShader.Reflection.ShaderReflection Reflection(int variant)");
        Line("{");
        _indent++;
        Line("switch (variant)");
        Line("{");
        _indent++;
        for (int i = 0; i < variants.Count; i++)
        {
            Line("case " + i.ToString(CultureInfo.InvariantCulture) + ": return Variants." + variants[i].ClassName + ".Reflection;");
        }

        Line("default: throw new global::System.ArgumentOutOfRangeException(nameof(variant));");
        _indent--;
        Line("}");
        _indent--;
        Line("}");

        bool pipeline = false;
        foreach (var v in variants)
        {
            pipeline |= v.HasPipeline;
        }

        if (pipeline)
        {
            Line("");
            Line("internal static global::OneShader.Layouts.PipelineLayout? Pipeline(int variant)");
            Line("{");
            _indent++;
            Line("switch (variant)");
            Line("{");
            _indent++;
            for (int i = 0; i < variants.Count; i++)
            {
                if (variants[i].HasPipeline)
                {
                    Line("case " + i.ToString(CultureInfo.InvariantCulture) + ": return Variants." + variants[i].ClassName + ".Pipeline;");
                }
            }

            Line("default: return null;");
            _indent--;
            Line("}");
            _indent--;
            Line("}");
        }

        _indent--;
        Line("}");
    }

    private static void ForEachEntry(List<VariantInfo> variants, List<EntryUnion> union, int entryCount, Func<int, VariantInfo, EntryUnion, EntryNode, bool> filter, Action<int, VariantInfo, EntryUnion> emit)
    {
        for (int vi = 0; vi < variants.Count; vi++)
        {
            for (int ui = 0; ui < union.Count; ui++)
            {
                EntryNode? node = null;
                foreach (var e in variants[vi].File.Entries)
                {
                    if (TypeName(e.Name) == union[ui].Name)
                    {
                        node = e;
                        break;
                    }
                }

                int id = (vi * entryCount) + ui;
                if (node is not null && filter(id, variants[vi], union[ui], node))
                {
                    emit(id, variants[vi], union[ui]);
                }
            }
        }
    }

    private void EmitSpanSwitch(string name, string element, List<VariantInfo> variants, List<EntryUnion> union, Func<ShaderFile, EntryNode, bool> has, string member)
    {
        bool any = Any(variants, has);
        if (!any)
        {
            return;
        }

        Line("internal static global::System.ReadOnlySpan<" + element + "> " + name + "(int id)");
        Line("{");
        _indent++;
        Line("switch (id)");
        Line("{");
        _indent++;
        ForEachEntry(variants, union, union.Count, (id, v, u, e) => has(v.File, e), (id, v, u) =>
        {
            Line("case " + id.ToString(CultureInfo.InvariantCulture) + ": return Variants." + v.ClassName + "." + Esc(u.Name) + "." + member + ";");
        });
        Line("default: return default;");
        _indent--;
        Line("}");
        _indent--;
        Line("}");
    }

    // ---- the Variants class ----

    private void EmitVariantsClass(List<VariantInfo> variants, List<AxisInfo> axes, List<EntryUnion> union, bool named)
    {
        string count = variants.Count.ToString(CultureInfo.InvariantCulture);
        Line("/// <summary>Every variant, the lookup from a <c>Key</c>, and each variant's static data (<c>Variants.&lt;Name&gt;.&lt;EntryPoint&gt;.SpirV</c> and so on, shaped like a single-variant file's class).</summary>");
        Line("public static class Variants");
        Line("{");
        _indent++;
        Line("/// <summary>The number of variants.</summary>");
        Line("public const int Count = " + count + ";");
        Line("");
        Line("private static readonly Variant[] s_all = CreateAll();");
        Line("");
        Line("/// <summary>All variants in declaration order (the first axis changes slowest).</summary>");
        Line("public static global::System.ReadOnlySpan<Variant> All => s_all;");
        Line("");
        Line("/// <summary>The variant for a key. Throws <c>ArgumentOutOfRangeException</c> when a value is outside its enum.</summary>");
        Line("public static Variant Get(Key key)");
        Line("{");
        _indent++;
        Line("int index = key.Index;");
        Line("if ((uint)index >= (uint)s_all.Length)");
        Line("{");
        _indent++;
        Line("throw new global::System.ArgumentOutOfRangeException(nameof(key), \"The key holds a value that is not one of the axis values.\");");
        _indent--;
        Line("}");
        Line("");
        Line("return s_all[index];");
        _indent--;
        Line("}");
        Line("");
        Line("/// <summary>The variant at a position in <c>All</c>.</summary>");
        Line("public static Variant Get(int index) => s_all[index];");
        Line("");
        Line("/// <summary>Like <c>Get(Key)</c>, without the exception.</summary>");
        Line("public static bool TryGet(Key key, [global::System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Variant? variant)");
        Line("{");
        _indent++;
        Line("int index = key.Index;");
        Line("if ((uint)index >= (uint)s_all.Length)");
        Line("{");
        _indent++;
        Line("variant = null;");
        Line("return false;");
        _indent--;
        Line("}");
        Line("");
        Line("variant = s_all[index];");
        Line("return true;");
        _indent--;
        Line("}");
        Line("");
        Line("private static Variant[] CreateAll()");
        Line("{");
        _indent++;
        Line("return new Variant[]");
        Line("{");
        _indent++;
        for (int vi = 0; vi < variants.Count; vi++)
        {
            var v = variants[vi];
            var keyArgs = new List<string>();
            for (int a = 0; a < axes.Count; a++)
            {
                uint o = v.Ref.Ordinals[a];
                keyArgs.Add(axes[a].IsFlag ? (o == 1 ? "true" : "false") : axes[a].EnumName + "." + Esc(axes[a].Members[(int)o]));
            }

            Line("new Variant(" + vi.ToString(CultureInfo.InvariantCulture) + ", " + Str(v.ClassName) + ", " + Str(v.Ref.Defines) + ", new Key(" + string.Join(", ", keyArgs) + "), new Entry?[]");
            Line("{");
            _indent++;
            for (int ui = 0; ui < union.Count; ui++)
            {
                EntryNode? node = null;
                foreach (var e in v.File.Entries)
                {
                    if (TypeName(e.Name) == union[ui].Name)
                    {
                        node = e;
                        break;
                    }
                }

                if (node is null)
                {
                    Line("null,");
                    continue;
                }

                v.File.Msl.TryGetValue(node.Name, out var msl);
                v.File.Wgsl.TryGetValue(node.Name, out var wgsl);
                string id = ((vi * union.Count) + ui).ToString(CultureInfo.InvariantCulture);
                Line("new Entry(" + id + ", global::OneShader.ShaderStage." + node.Stage + ", " + Str(node.Name) + ", "
                    + (msl is null ? "null" : Str(msl.Function)) + ", "
                    + (msl is { HasVertexBuffer: true } ? Num(msl.VertexBuffer) : "null") + ", "
                    + (wgsl is null ? "null" : Str(wgsl.Function)) + ", "
                    + Num(node.Stride) + ", " + Num(node.X) + ", " + Num(node.Y) + ", " + Num(node.Z) + ", " + Num(node.Subgroup) + "),");
            }

            _indent--;
            Line("}),");
        }

        _indent--;
        Line("};");
        _indent--;
        Line("}");

        foreach (var v in variants)
        {
            Line("");
            EmitVariantData(v);
        }

        _indent--;
        Line("}");
    }

    // The same members a single-variant file's class has, minus what is shared at file level.
    private void EmitVariantData(VariantInfo v)
    {
        _file = v.File;
        _topNames = new HashSet<string>(_fileNames!, StringComparer.Ordinal);
        var f = v.File;
        if (f.Specs.Count > 0)
        {
            _topNames.Add("SpecializationConstants");
            _topNames.Add("Specialization");
            CheckSpecNames();
        }

        foreach (var e in f.Entries)
        {
            string name = TypeName(e.Name);
            if (!_topNames.Add(name) || EntryMembers.Contains(name))
            {
                Problem(Codes.NameClash, "Entry point '" + e.Name + "' (C# class '" + name + "') in " + f.Source + " clashes with another generated name (a constant buffer, a struct, a variant or 'Bindings'). Rename the HLSL function or the other declaration.");
            }
        }

        var localBuffers = new List<ResourceNode>();
        foreach (var r in f.Resources)
        {
            if (r.Kind == "ConstantBuffer" && r.Layout is { Kind: "Struct" })
            {
                string name = TypeName(r.Name);
                if (_sharedStructs.Contains(name))
                {
                    continue;
                }

                if (!_topNames.Add(name))
                {
                    Problem(Codes.NameClash, "Constant buffer '" + r.Name + "' (C# struct '" + name + "') in " + f.Source + " clashes with an entry point or another generated name. Rename the cbuffer, the function or the struct.");
                }

                localBuffers.Add(r);
            }
        }

        var pushNames = new List<string>();
        var localPushes = new List<int>();
        for (int i = 0; i < f.Pushes.Count; i++)
        {
            string csName = f.Pushes.Count == 1 ? "PushConstants" : TypeName(f.Pushes[i].Name) + "PushConstants";
            pushNames.Add(csName);
            if (_sharedStructs.Contains(csName))
            {
                continue;
            }

            if (!_topNames.Add(csName))
            {
                Problem(Codes.NameClash, "The push-constant block '" + f.Pushes[i].Name + "' (C# struct '" + csName + "') in " + f.Source + " clashes with an entry point or another generated name. Rename the variable, the function or the struct.");
            }

            localPushes.Add(i);
        }

        if (_problems.Count > 0)
        {
            return;
        }

        foreach (var r in f.Resources)
        {
            if ((r.Kind == "ReadOnlyStorageBuffer" || r.Kind == "StorageBuffer") && r.Layout is { Kind: "Struct" } el)
            {
                NestedName(el);
            }
        }

        Line("/// <summary>The data of the variant built with: " + (v.Ref.Defines.Length == 0 ? "no variant defines" : EscapeDoc(v.Ref.Defines)) + ".</summary>");
        Line("public static class " + Esc(v.ClassName));
        Line("{");
        _indent++;
        bool first = true;
        foreach (var cb in localBuffers)
        {
            Separator(ref first);
            EmitStruct(TypeName(cb.Name), cb.Layout!);
        }

        foreach (int i in localPushes)
        {
            if (f.Pushes[i].Layout is { Kind: "Struct" } pl)
            {
                Separator(ref first);
                Line("/// <summary>The bytes of the push-constant block '" + Sanitize(f.Pushes[i].Name) + "'; push all <c>Size</c> bytes to the stages listed in <c>Bindings." + pushNames[i] + "</c>.</summary>");
                EmitStruct(pushNames[i], pl);
            }
        }

        // Vertex structs that differ between variants stay with their entry point (see EmitVertex).
        if (f.Specs.Count > 0)
        {
            Separator(ref first);
            EmitSpecializationConstants();
        }

        Separator(ref first);
        EmitBindings(pushNames);
        foreach (var e in f.Entries)
        {
            Line("");
            EmitEntry(e);
        }

        _pipelineEmitted = false;
        EmitPipeline();
        v.HasPipeline = _pipelineEmitted;
        Line("");
        EmitReflection();
        _indent--;
        Line("}");
    }

    private static string EscapeDoc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // ---- structs every variant agrees on ----

    private static string VertexSignature(EntryNode e)
    {
        var sb = new StringBuilder();
        sb.Append(e.Stride);
        foreach (var v in VertexAttributes(e))
        {
            sb.Append('|').Append(v.Name).Append(':').Append(v.Location).Append(':').Append(v.Format).Append(':').Append(v.Offset);
        }

        return sb.ToString();
    }

    private readonly List<KeyValuePair<string, TypeNode>> _sharedOrder = new List<KeyValuePair<string, TypeNode>>();
    private readonly List<KeyValuePair<string, EntryNode>> _sharedVertexOrder = new List<KeyValuePair<string, EntryNode>>();

    // A struct that every variant that has it lays out the same way is generated once at file level; one that differs
    // between variants stays inside each variant's class, so a variant never gets a type that does not match its shader.
    private void DecideSharing(List<VariantInfo> variants)
    {
        var signature = new Dictionary<string, string>(StringComparer.Ordinal);
        var layout = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        var order = new List<string>();
        var conflicted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in variants)
        {
            var f = v.File;
            foreach (var r in f.Resources)
            {
                if (r.Kind == "ConstantBuffer" && r.Layout is { Kind: "Struct" })
                {
                    Note(TypeName(r.Name), r.Layout);
                }
            }

            for (int i = 0; i < f.Pushes.Count; i++)
            {
                if (f.Pushes[i].Layout is { Kind: "Struct" } pl)
                {
                    Note(f.Pushes.Count == 1 ? "PushConstants" : TypeName(f.Pushes[i].Name) + "PushConstants", pl);
                }
            }
        }

        void Note(string name, TypeNode node)
        {
            string sig = Signature(node);
            if (!signature.TryGetValue(name, out string? seen))
            {
                signature[name] = sig;
                layout[name] = node;
                order.Add(name);
            }
            else if (seen != sig)
            {
                conflicted.Add(name);
            }
        }

        foreach (string name in order)
        {
            if (!conflicted.Contains(name) && _fileNames!.Add(name))
            {
                _sharedStructs.Add(name);
                _sharedOrder.Add(new KeyValuePair<string, TypeNode>(name, layout[name]));
            }
        }

        // Vertex structs, by entry point.
        var vsig = new Dictionary<string, string>(StringComparer.Ordinal);
        var vnode = new Dictionary<string, EntryNode>(StringComparer.Ordinal);
        var vorder = new List<string>();
        var vconflict = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in variants)
        {
            foreach (var e in v.File.Entries)
            {
                if (e.Stage != "Vertex" || VertexAttributes(e).Count == 0)
                {
                    continue;
                }

                string sig = VertexSignature(e);
                if (!vsig.TryGetValue(e.Name, out string? seen))
                {
                    vsig[e.Name] = sig;
                    vnode[e.Name] = e;
                    vorder.Add(e.Name);
                }
                else if (seen != sig)
                {
                    vconflict.Add(e.Name);
                }
            }
        }

        foreach (string name in vorder)
        {
            string structName = TypeName(name) + "Vertex";
            if (!vconflict.Contains(name) && _fileNames!.Add(structName))
            {
                _sharedVertex[name] = structName;
                _sharedVertexOrder.Add(new KeyValuePair<string, EntryNode>(structName, vnode[name]));
            }
        }
    }

    private void EmitSharedStructs()
    {
        bool first = true;
        foreach (var pair in _sharedOrder)
        {
            Separator(ref first);
            EmitStruct(pair.Key, pair.Value);
        }

        foreach (var pair in _sharedVertexOrder)
        {
            Separator(ref first);
            Line("/// <summary>The vertex of entry point '" + Sanitize(pair.Value.Name) + "', the same in every variant that has it.</summary>");
            if (!EmitVertexStruct(pair.Value, VertexAttributes(pair.Value), pair.Key))
            {
                return;
            }
        }

        // The queue grows while structs are emitted (a struct can contain another).
        for (int i = 0; i < _structQueue.Count; i++)
        {
            Separator(ref first);
            EmitStruct(_structQueue[i].Key, _structQueue[i].Value);
        }
    }
}
