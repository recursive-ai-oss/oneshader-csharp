using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using OneShader.Reflection;

namespace OneShader.Tool;

/// <summary>
/// Writes <see cref="ShaderReflection"/> as JSON (schema in the package README). Hand-written on Utf8JsonWriter so it needs
/// no reflection; output order follows the model's order, so the same compile gives the same bytes.
/// </summary>
internal static class ReflectionJson
{
    public const int SchemaVersion = 1;

    public static string Write(ShaderReflection r)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", SchemaVersion);
            // The file name only: reflection must not carry the machine's directory layout.
            int slash = r.SourceName.LastIndexOfAny(['/', '\\']);
            w.WriteString("source", slash < 0 ? r.SourceName : r.SourceName[(slash + 1)..]);
            w.WriteStartArray("targets");
            foreach (var t in r.Targets)
            {
                w.WriteStringValue(t.ToString());
            }

            w.WriteEndArray();

            w.WriteStartArray("resources");
            foreach (var res in r.Resources)
            {
                w.WriteStartObject();
                w.WriteString("name", res.Name);
                w.WriteString("kind", res.Kind.ToString());
                w.WriteString("hlslType", res.HlslType);
                w.WriteString("resourceType", res.ResourceType.ToString());
                w.WriteNumber("count", res.Count);
                if (res.IsUnbounded)
                {
                    // Written only for unbounded arrays and heaps, so other shaders' files stay as they were.
                    w.WriteBoolean("unbounded", true);
                }

                if (res.Heap != DescriptorHeaps.None)
                {
                    w.WriteString("heap", res.Heap.ToString());
                    w.WriteStartArray("heapKinds");
                    foreach (var k in res.HeapKinds)
                    {
                        w.WriteStringValue(k.ToString());
                    }

                    w.WriteEndArray();
                }

                Stages(w, "stages", res.Stages);
                Slots(w, "slots", res.Slots);
                Slots(w, "counterSlots", res.CounterSlots);
                if (res.Layout is not null)
                {
                    w.WritePropertyName("layout");
                    Type(w, res.Layout);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteStartArray("pushConstants");
            foreach (var pc in r.PushConstants)
            {
                w.WriteStartObject();
                w.WriteString("name", pc.Name);
                Stages(w, "stages", pc.Stages);
                Slots(w, "slots", pc.Slots);
                w.WritePropertyName("layout");
                Type(w, pc.Layout);
                w.WriteEndObject();
            }

            w.WriteEndArray();

            // Written only when the shader has specialization constants, so other shaders' files stay as they were.
            if (r.SpecConstants.Count > 0)
            {
                w.WriteStartArray("specConstants");
                foreach (var sc in r.SpecConstants)
                {
                    w.WriteStartObject();
                    w.WriteString("name", sc.Name);
                    w.WriteNumber("id", sc.Id);
                    w.WriteString("type", sc.Scalar.ToString());
                    w.WriteNumber("defaultBits", sc.DefaultBits);
                    w.WriteStartArray("slots");
                    foreach (var s in sc.Slots)
                    {
                        w.WriteStartObject();
                        w.WriteString("target", s.Target.ToString());
                        w.WriteNumber("index", s.Index);
                        w.WriteString("name", s.Name);
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                    w.WriteEndObject();
                }

                w.WriteEndArray();
            }

            w.WriteStartArray("entryPoints");
            foreach (var e in r.EntryPoints)
            {
                w.WriteStartObject();
                w.WriteString("name", e.Name);
                w.WriteString("stage", e.Stage.ToString());
                if (e.Stage == ShaderStage.Compute)
                {
                    w.WriteStartArray("workgroupSize");
                    w.WriteNumberValue(e.WorkgroupSize.X);
                    w.WriteNumberValue(e.WorkgroupSize.Y);
                    w.WriteNumberValue(e.WorkgroupSize.Z);
                    w.WriteEndArray();
                }

                if (e.Stage == ShaderStage.Vertex)
                {
                    w.WriteNumber("vertexStride", e.VertexStride);
                }

                Variables(w, "inputs", e.Inputs);
                Variables(w, "outputs", e.Outputs);
                w.WriteStartArray("usedResources");
                foreach (string u in e.UsedResources)
                {
                    w.WriteStringValue(u);
                }

                w.WriteEndArray();
                if (e.DescriptorHeaps != DescriptorHeaps.None)
                {
                    w.WriteString("descriptorHeaps", e.DescriptorHeaps.ToString());
                }

                if (e.RequiredFeatures != DeviceFeatures.None)
                {
                    w.WriteString("requiredFeatures", e.RequiredFeatures.ToString());
                }

                if (e.PushConstant is not null)
                {
                    w.WriteString("pushConstant", e.PushConstant);
                }

                if (e.UsedSpecConstants.Count > 0)
                {
                    w.WriteStartArray("usedSpecConstants");
                    foreach (string u in e.UsedSpecConstants)
                    {
                        w.WriteStringValue(u);
                    }

                    w.WriteEndArray();
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
    }

    private static void Stages(Utf8JsonWriter w, string name, ShaderStages stages)
    {
        w.WriteStartArray(name);
        if ((stages & ShaderStages.Vertex) != 0)
        {
            w.WriteStringValue("Vertex");
        }

        if ((stages & ShaderStages.Pixel) != 0)
        {
            w.WriteStringValue("Pixel");
        }

        if ((stages & ShaderStages.Compute) != 0)
        {
            w.WriteStringValue("Compute");
        }

        w.WriteEndArray();
    }

    private static void Slots(Utf8JsonWriter w, string name, IReadOnlyList<TargetSlot> slots)
    {
        w.WriteStartArray(name);
        foreach (var s in slots)
        {
            w.WriteStartObject();
            w.WriteString("target", s.Target.ToString());
            w.WriteNumber("set", s.Set);
            w.WriteNumber("binding", s.Binding);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void Variables(Utf8JsonWriter w, string name, IReadOnlyList<StageVariable> vars)
    {
        w.WriteStartArray(name);
        foreach (var v in vars)
        {
            w.WriteStartObject();
            w.WriteString("name", v.Name);
            w.WriteString("semantic", v.Semantic);
            w.WriteNumber("semanticIndex", v.SemanticIndex);
            if (v.Location is { } loc)
            {
                w.WriteNumber("location", loc);
            }

            w.WriteString("builtIn", v.BuiltIn.ToString());
            w.WriteString("scalar", v.Scalar.ToString());
            w.WriteNumber("components", v.Components);
            if (v.Format is { } f)
            {
                w.WriteString("format", f.ToString());
            }

            w.WriteNumber("offset", v.Offset);
            w.WriteString("interpolation", v.Interpolation.ToString());
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void Type(Utf8JsonWriter w, ReflectedType t)
    {
        w.WriteStartObject();
        w.WriteString("kind", t.Kind.ToString());
        w.WriteNumber("size", t.Size);
        w.WriteNumber("alignment", t.Alignment);
        switch (t.Kind)
        {
            case ReflectedTypeKind.Scalar:
                w.WriteString("scalar", t.Scalar.ToString());
                break;
            case ReflectedTypeKind.Vector:
                w.WriteString("scalar", t.Scalar.ToString());
                w.WriteNumber("components", t.Rows);
                break;
            case ReflectedTypeKind.Matrix:
                w.WriteString("scalar", t.Scalar.ToString());
                w.WriteNumber("rows", t.Rows);
                w.WriteNumber("columns", t.Columns);
                w.WriteNumber("matrixStride", t.MatrixStride);
                w.WriteString("packing", t.Packing.ToString());
                break;
            case ReflectedTypeKind.Array:
                w.WriteNumber("length", t.ArrayLength);
                w.WriteNumber("stride", t.ArrayStride);
                if (t.Element is not null)
                {
                    w.WritePropertyName("element");
                    Type(w, t.Element);
                }

                break;
            case ReflectedTypeKind.Struct:
                w.WriteString("name", t.Name);
                w.WriteStartArray("members");
                foreach (var m in t.Members)
                {
                    w.WriteStartObject();
                    w.WriteString("name", m.Name);
                    w.WriteNumber("offset", m.Offset);
                    w.WritePropertyName("type");
                    Type(w, m.Type);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                break;
            default:
                break;
        }

        w.WriteEndObject();
    }
}
