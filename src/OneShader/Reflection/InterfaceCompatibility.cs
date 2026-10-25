using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using OneShader.Types;

namespace OneShader.Reflection;

/// <summary>Which part of a shader's interface differs.</summary>
public enum InterfaceDifferenceKind
{
    /// <summary>An entry point is missing or has another stage.</summary>
    EntryPoint,

    /// <summary>A resource was added or removed, or its kind, array size, slot or stage visibility changed.</summary>
    Resource,

    /// <summary>The layout of a constant buffer, structured buffer or texel type changed: member offsets, types, sizes or strides.</summary>
    BufferLayout,

    /// <summary>A vertex input was added or removed, or its location, format or offset changed, or the vertex stride changed.</summary>
    VertexInput,

    /// <summary>A compute entry point's thread-group size changed.</summary>
    WorkgroupSize,
}

/// <summary>One specific way a recompiled shader no longer matches the interface the application was built with.</summary>
/// <param name="Kind">The part of the interface.</param>
/// <param name="Message">A sentence fragment naming the item and the old and new values, for example <c>Frame.Tint moved from offset 16 to 32</c>.</param>
public sealed record InterfaceDifference(InterfaceDifferenceKind Kind, string Message)
{
    /// <inheritdoc />
    public override string ToString() => Message;
}

/// <summary>The result of <see cref="InterfaceCompatibility.Compare"/>.</summary>
public sealed class CompatibilityReport
{
    internal CompatibilityReport(IReadOnlyList<InterfaceDifference> differences)
    {
        Differences = differences;
    }

    /// <summary>True when the recompiled shader can replace the built one without changing any generated C# type or binding.</summary>
    public bool IsCompatible => Differences.Count == 0;

    /// <summary>Every difference, in a stable order (resources, then entry points, each in the built shader's order).</summary>
    public IReadOnlyList<InterfaceDifference> Differences { get; }

    /// <summary>
    /// "compatible", or one line per difference followed by what to do. Meant to be shown to the person who edited the shader.
    /// </summary>
    public override string ToString()
    {
        if (IsCompatible)
        {
            return "compatible";
        }

        var sb = new StringBuilder("incompatible with the compiled-in C# bindings:");
        foreach (var d in Differences)
        {
            sb.Append("\n  ").Append(d.Message);
        }

        return sb.Append("\nRebuild the project to update the generated C# bindings.").ToString();
    }
}

/// <summary>
/// Decides whether a recompiled shader can be swapped in while the program runs. The program's generated C# structs and
/// binding constants were produced from one <see cref="ShaderReflection"/> (exposed by the generated code as
/// <c>Reflection</c>); a fresh compile of the edited file is compatible when everything the C# side depends on is unchanged.
/// Edits to function bodies, to code that no other shader sees, and added entry points are compatible.
/// </summary>
public static class InterfaceCompatibility
{
    private const int MaxDepth = 64;

    /// <summary>Compares the reflection the program was built with against the reflection of a fresh compile. Never throws for well-formed models.</summary>
    /// <param name="builtWith">The reflection the C# code was generated from.</param>
    /// <param name="recompiled">The reflection of the recompiled shader.</param>
    /// <returns>The report; <see cref="CompatibilityReport.IsCompatible"/> is true when nothing the C# side relies on changed.</returns>
    public static CompatibilityReport Compare(ShaderReflection builtWith, ShaderReflection recompiled)
    {
        ArgumentNullException.ThrowIfNull(builtWith);
        ArgumentNullException.ThrowIfNull(recompiled);
        var diffs = new List<InterfaceDifference>();
        CompareResources(builtWith, recompiled, diffs);
        CompareEntryPoints(builtWith, recompiled, diffs);
        return new CompatibilityReport(diffs);
    }

    private static void Add(List<InterfaceDifference> diffs, InterfaceDifferenceKind kind, string message) =>
        diffs.Add(new InterfaceDifference(kind, message));

    private static string U(uint v) => v.ToString(CultureInfo.InvariantCulture);

    // ---- resources ----

    private static void CompareResources(ShaderReflection a, ShaderReflection b, List<InterfaceDifference> diffs)
    {
        var fresh = new Dictionary<string, ResourceInfo>(StringComparer.Ordinal);
        foreach (var r in b.Resources)
        {
            fresh[r.Name] = r;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var old in a.Resources)
        {
            seen.Add(old.Name);
            if (!fresh.TryGetValue(old.Name, out var now))
            {
                Add(diffs, InterfaceDifferenceKind.Resource, "Resource " + old.Name + " was removed");
                continue;
            }

            CompareResource(old, now, diffs);
        }

        foreach (var r in b.Resources)
        {
            if (!seen.Contains(r.Name))
            {
                Add(diffs, InterfaceDifferenceKind.Resource, "Resource " + r.Name + " was added (" + Describe(r) + ")");
            }
        }
    }

    private static string Describe(ResourceInfo r) => r.HlslType.Length > 0 ? r.HlslType : r.Kind.ToString();

    private static void CompareResource(ResourceInfo old, ResourceInfo now, List<InterfaceDifference> diffs)
    {
        string n = old.Name;
        if (old.Kind != now.Kind)
        {
            Add(diffs, InterfaceDifferenceKind.Resource, "Resource " + n + " changed kind from " + old.Kind + " to " + now.Kind);
            return;
        }

        if (old.ResourceType != now.ResourceType)
        {
            Add(diffs, InterfaceDifferenceKind.Resource, "Resource " + n + " changed type from " + Describe(old) + " to " + Describe(now));
            return;
        }

        if (old.Count != now.Count)
        {
            Add(diffs, InterfaceDifferenceKind.Resource, "Resource " + n + " changed array size from " + U(old.Count) + " to " + U(now.Count));
        }

        CompareSlots("Resource " + n, old.Slots, now.Slots, diffs);
        CompareSlots("Resource " + n + " counter buffer", old.CounterSlots, now.CounterSlots, diffs);

        // A resource that becomes visible to more stages needs a descriptor layout the built code never made; fewer stages is harmless.
        var added = now.Stages & ~old.Stages;
        if (added != ShaderStages.None)
        {
            Add(diffs, InterfaceDifferenceKind.Resource, "Resource " + n + " is now also used by the " + added + " stage, which the compiled-in bindings do not make it visible to (was " + old.Stages + ")");
        }

        if (old.Layout is not null && now.Layout is not null)
        {
            CompareType(n, old.Layout, now.Layout, 0, diffs);
        }
        else if ((old.Layout is null) != (now.Layout is null))
        {
            Add(diffs, InterfaceDifferenceKind.BufferLayout, "Resource " + n + (now.Layout is null ? " no longer has a described layout" : " now has a described layout"));
        }
    }

    private static void CompareSlots(string subject, IReadOnlyList<TargetSlot> old, IReadOnlyList<TargetSlot> now, List<InterfaceDifference> diffs)
    {
        // Only targets both models know can be compared; a model built for more targets is not "different" for the others.
        foreach (var o in old)
        {
            foreach (var m in now)
            {
                if (o.Target == m.Target && (o.Set != m.Set || o.Binding != m.Binding))
                {
                    Add(
                        diffs,
                        InterfaceDifferenceKind.Resource,
                        subject + " moved from set " + U(o.Set) + " binding " + U(o.Binding) + " to set " + U(m.Set) + " binding " + U(m.Binding) + " (" + o.Target + ")");
                }
            }
        }
    }

    // ---- layouts ----

    private static void CompareType(string path, ReflectedType a, ReflectedType b, int depth, List<InterfaceDifference> diffs)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        if (a.Kind != b.Kind || a.Scalar != b.Scalar || a.Rows != b.Rows || a.Columns != b.Columns)
        {
            Add(diffs, InterfaceDifferenceKind.BufferLayout, path + " changed type from " + Describe(a) + " to " + Describe(b));
            return;
        }

        int before = diffs.Count;
        switch (a.Kind)
        {
            case ReflectedTypeKind.Matrix:
                if (a.Packing != b.Packing)
                {
                    Add(diffs, InterfaceDifferenceKind.BufferLayout, path + " changed matrix packing from " + Pack(a.Packing) + " to " + Pack(b.Packing));
                }
                else if (a.MatrixStride != b.MatrixStride)
                {
                    Add(diffs, InterfaceDifferenceKind.BufferLayout, path + " changed matrix stride from " + U(a.MatrixStride) + " to " + U(b.MatrixStride));
                }

                break;
            case ReflectedTypeKind.Array:
                if (a.ArrayLength != b.ArrayLength)
                {
                    Add(diffs, InterfaceDifferenceKind.BufferLayout, path + " changed array length from " + U(a.ArrayLength) + " to " + U(b.ArrayLength));
                }

                if (a.ArrayStride != b.ArrayStride)
                {
                    Add(diffs, InterfaceDifferenceKind.BufferLayout, path + " changed array stride from " + U(a.ArrayStride) + " to " + U(b.ArrayStride));
                }

                if (a.Element is not null && b.Element is not null)
                {
                    CompareType(path + "[]", a.Element, b.Element, depth + 1, diffs);
                }

                break;
            case ReflectedTypeKind.Struct:
                CompareMembers(path, a, b, depth, diffs);
                break;
            default:
                break;
        }

        // Size only when nothing above explains it (a moved member already changes the size of the struct around it).
        if (a.Size != b.Size && diffs.Count == before)
        {
            Add(diffs, InterfaceDifferenceKind.BufferLayout, path + " size changed from " + U(a.Size) + " to " + U(b.Size) + " bytes");
        }
    }

    private static void CompareMembers(string path, ReflectedType a, ReflectedType b, int depth, List<InterfaceDifference> diffs)
    {
        var fresh = new Dictionary<string, ReflectedMember>(StringComparer.Ordinal);
        foreach (var m in b.Members)
        {
            fresh[m.Name] = m;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var old in a.Members)
        {
            seen.Add(old.Name);
            string member = path + "." + old.Name;
            if (!fresh.TryGetValue(old.Name, out var now))
            {
                Add(diffs, InterfaceDifferenceKind.BufferLayout, member + " was removed");
                continue;
            }

            if (old.Offset != now.Offset)
            {
                Add(diffs, InterfaceDifferenceKind.BufferLayout, member + " moved from offset " + U(old.Offset) + " to " + U(now.Offset));
            }

            CompareType(member, old.Type, now.Type, depth + 1, diffs);
        }

        foreach (var m in b.Members)
        {
            if (!seen.Contains(m.Name))
            {
                Add(diffs, InterfaceDifferenceKind.BufferLayout, path + "." + m.Name + " was added at offset " + U(m.Offset));
            }
        }
    }

    private static string Pack(MatrixPacking p) => p == MatrixPacking.RowMajor ? "row_major" : "column_major";

    private static string ScalarName(ScalarKind k) => k switch
    {
        ScalarKind.Int16 => "int16_t",
        ScalarKind.UInt16 => "uint16_t",
        ScalarKind.Int64 => "int64_t",
        ScalarKind.UInt64 => "uint64_t",
        _ => k.ToString().ToLowerInvariant(),
    };

    /// <summary>The type spelled as HLSL would: <c>float4</c>, <c>float4x4</c>, <c>float[4]</c>, <c>struct Light</c>.</summary>
    private static string Describe(ReflectedType t) => t.Kind switch
    {
        ReflectedTypeKind.Scalar => ScalarName(t.Scalar),
        ReflectedTypeKind.Vector => ScalarName(t.Scalar) + U(t.Rows),
        ReflectedTypeKind.Matrix => ScalarName(t.Scalar) + U(t.Rows) + "x" + U(t.Columns),
        ReflectedTypeKind.Array => (t.Element is null ? "?" : Describe(t.Element)) + "[" + (t.ArrayLength == 0 ? "" : U(t.ArrayLength)) + "]",
        _ => "struct " + t.Name,
    };

    // ---- entry points ----

    private static void CompareEntryPoints(ShaderReflection a, ShaderReflection b, List<InterfaceDifference> diffs)
    {
        // Entry points that only the recompiled shader has are fine: the built code has no class for them.
        foreach (var old in a.EntryPoints)
        {
            EntryPointInfo? now = null;
            foreach (var e in b.EntryPoints)
            {
                if (e.Name == old.Name)
                {
                    now = e;
                    break;
                }
            }

            if (now is null)
            {
                Add(diffs, InterfaceDifferenceKind.EntryPoint, "Entry point " + old.Name + " is missing");
                continue;
            }

            if (now.Stage != old.Stage)
            {
                Add(diffs, InterfaceDifferenceKind.EntryPoint, "Entry point " + old.Name + " changed stage from " + old.Stage + " to " + now.Stage);
                continue;
            }

            if (old.Stage == ShaderStage.Compute && old.WorkgroupSize != now.WorkgroupSize)
            {
                Add(diffs, InterfaceDifferenceKind.WorkgroupSize, old.Name + " workgroup size changed from " + Size3(old.WorkgroupSize) + " to " + Size3(now.WorkgroupSize));
            }

            if (old.Stage == ShaderStage.Vertex)
            {
                CompareVertex(old, now, diffs);
            }
        }
    }

    private static string Size3((uint X, uint Y, uint Z) s) => U(s.X) + "x" + U(s.Y) + "x" + U(s.Z);

    private static void CompareVertex(EntryPointInfo old, EntryPointInfo now, List<InterfaceDifference> diffs)
    {
        // Only inputs that become vertex attributes (a location and a format) appear in the generated Vertex struct.
        string who = old.Name + " vertex input ";
        var fresh = new Dictionary<string, StageVariable>(StringComparer.Ordinal);
        foreach (var v in now.Inputs)
        {
            if (IsAttribute(v))
            {
                fresh[v.Name] = v;
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in old.Inputs)
        {
            if (!IsAttribute(o))
            {
                continue;
            }

            seen.Add(o.Name);
            if (!fresh.TryGetValue(o.Name, out var m))
            {
                Add(diffs, InterfaceDifferenceKind.VertexInput, who + o.Name + " was removed");
                continue;
            }

            if (o.Location != m.Location)
            {
                Add(diffs, InterfaceDifferenceKind.VertexInput, who + o.Name + " moved from location " + U(o.Location!.Value) + " to " + U(m.Location!.Value));
            }

            if (o.Format != m.Format)
            {
                Add(diffs, InterfaceDifferenceKind.VertexInput, who + o.Name + " changed format from " + o.Format + " to " + m.Format);
            }

            if (o.Offset != m.Offset)
            {
                Add(diffs, InterfaceDifferenceKind.VertexInput, who + o.Name + " moved from offset " + U(o.Offset) + " to " + U(m.Offset));
            }
        }

        foreach (var v in now.Inputs)
        {
            if (IsAttribute(v) && !seen.Contains(v.Name))
            {
                Add(diffs, InterfaceDifferenceKind.VertexInput, who + v.Name + " was added at location " + U(v.Location!.Value));
            }
        }

        if (old.VertexStride != now.VertexStride)
        {
            Add(diffs, InterfaceDifferenceKind.VertexInput, old.Name + " vertex stride changed from " + U(old.VertexStride) + " to " + U(now.VertexStride));
        }
    }

    private static bool IsAttribute(StageVariable v) => v.Location is not null && v.Format is not null;
}
