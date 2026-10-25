using System;
using System.Collections.Generic;
using System.Text;
using OneShader.Diagnostics;
using OneShader.IR;
using OneShader.Reflection;
using OneShader.Types;

namespace OneShader.Wgsl;

internal sealed partial class WgslEmitter
{
    private sealed record IoLeaf(string Path, HlslType Type, int Location, string Name);

    private (string Attr, HlslType Type)? BuiltInInfo(GlobalVariable g)
    {
        var f4 = HlslType.Vector(ScalarType.Float, 4);
        var u3 = HlslType.Vector(ScalarType.UInt, 3);
        switch (g.BuiltIn)
        {
            case BuiltIn.Position or BuiltIn.FragCoord:
                return ("position", f4);
            case BuiltIn.VertexIndex:
                return ("vertex_index", ScalarType.UInt);
            case BuiltIn.InstanceIndex:
                return ("instance_index", ScalarType.UInt);
            case BuiltIn.FrontFacing:
                return ("front_facing", ScalarType.Bool);
            case BuiltIn.FragDepth:
                return ("frag_depth", ScalarType.Float);
            case BuiltIn.SampleIndex:
                return ("sample_index", ScalarType.UInt);
            case BuiltIn.SampleMask:
                return ("sample_mask", ScalarType.UInt);
            case BuiltIn.GlobalInvocationId:
                return ("global_invocation_id", u3);
            case BuiltIn.LocalInvocationId:
                return ("local_invocation_id", u3);
            case BuiltIn.WorkgroupId:
                return ("workgroup_id", u3);
            case BuiltIn.LocalInvocationIndex:
                return ("local_invocation_index", ScalarType.UInt);
            case BuiltIn.ClipDistance or BuiltIn.CullDistance:
                Unsupported("SV_ClipDistance and SV_CullDistance need WebGPU's optional clip-distances feature, which this backend does not use; clip in the pixel shader with clip() or discard instead.", g.Span);
                return null;
            default:
                Unsupported("The system value " + (g.Semantic ?? g.BuiltIn.ToString()) + " has no WGSL built-in (WebGPU has no " + g.BuiltIn + ").", g.Span);
                return null;
        }
    }

    private static int LocationCount(HlslType t) => t switch
    {
        ArrayType a => a.Length * LocationCount(a.Element),
        MatrixType m => m.Rows == 1 || m.Columns == 1 ? 1 : m.Rows,
        StructType st => StructLocations(st),
        _ => 1,
    };

    private static int StructLocations(StructType st)
    {
        int n = 0;
        foreach (var f in st.Fields)
        {
            n += LocationCount(f.Type);
        }

        return n;
    }

    private void Flatten(HlslType t, string path, string name, ref int location, List<IoLeaf> leaves, int depth)
    {
        if (depth > MaxDepth)
        {
            Fail(WgslCodes.MalformedIr, "A stage variable is nested too deeply.");
            return;
        }

        switch (t)
        {
            case ArrayType a:
                for (int k = 0; k < a.Length; k++)
                {
                    Flatten(a.Element, path + "[" + Inv(k) + "]", name + "_" + Inv(k), ref location, leaves, depth + 1);
                }

                break;
            case MatrixType m when m.Rows > 1 && m.Columns > 1:
            {
                // One location per row, as the SPIR-V target lays them out.
                var row = HlslType.Vector(m.Element, m.Columns);
                for (int r = 0; r < m.Rows; r++)
                {
                    leaves.Add(new IoLeaf(path + "[" + Inv(r) + "]", row, location++, name + "_" + Inv(r)));
                }

                break;
            }

            case StructType st:
                for (int k = 0; k < st.Fields.Count; k++)
                {
                    Flatten(st.Fields[k].Type, path + "." + FieldName(st, k), name + "_" + st.Fields[k].Name, ref location, leaves, depth + 1);
                }

                break;
            default:
                leaves.Add(new IoLeaf(path, t is MatrixType mm ? HlslType.Vector(mm.Element, mm.Rows * mm.Columns) : t, location++, name));
                break;
        }
    }

    private List<IoLeaf> Leaves(GlobalVariable g)
    {
        var leaves = new List<IoLeaf>();
        if (g.BuiltIn != BuiltIn.None)
        {
            leaves.Add(new IoLeaf("", g.ValueType, 0, g.Name ?? "io"));
            return leaves;
        }

        if (g.Location is not { } loc)
        {
            Fail(WgslCodes.MalformedIr, "The stage variable '" + g.Name + "' has neither a location nor a built-in; give it a semantic.", g.Span);
            return leaves;
        }

        Flatten(g.ValueType, "", g.Name ?? "io", ref loc, leaves, 0);
        return leaves;
    }

    private string? InterpolationAttribute(GlobalVariable g, HlslType leaf, bool inputSide)
    {
        bool stageOutput = !inputSide && _ep.Stage == ShaderStage.Vertex;
        bool stageInput = inputSide && _ep.Stage == ShaderStage.Pixel;
        if (!stageOutput && !stageInput)
        {
            return null;
        }

        // Integer inputs of a fragment shader must be flat (WGSL 13.3.1.1), and the vertex output has to agree.
        bool integer = leaf.ScalarElement is { IsInteger: true };
        if (g.Interpolation == Interpolation.Flat || integer)
        {
            return "@interpolate(flat)";
        }

        return g.Interpolation switch
        {
            Interpolation.NoPerspective => "@interpolate(linear)",
            Interpolation.Centroid => "@interpolate(perspective, centroid)",
            Interpolation.Sample => "@interpolate(perspective, sample)",
            _ => null,
        };
    }

    private string EntrySetup()
    {
        var ps = new List<string>();
        foreach (var g in _ep.Inputs)
        {
            string global = _globalNames[g];
            foreach (var leaf in Leaves(g))
            {
                string attr;
                HlslType wireType = leaf.Type;
                if (g.BuiltIn != BuiltIn.None)
                {
                    if (BuiltInInfo(g) is not { } info)
                    {
                        continue;
                    }

                    attr = "@builtin(" + info.Attr + ")";
                    wireType = info.Type;
                }
                else
                {
                    if (leaf.Type.ScalarElement is { ScalarKind: ScalarKind.Bool })
                    {
                        Unsupported("The stage input '" + g.Name + "' is a bool; WGSL stage inputs must be numeric. Use an int and compare it.", g.Span);
                        continue;
                    }

                    attr = "@location(" + Inv(leaf.Location) + ")";
                    if (InterpolationAttribute(g, leaf.Type, true) is { } interp)
                    {
                        attr += " " + interp;
                    }
                }

                string pname = NewLocal("in_" + leaf.Name);
                ps.Add(attr + " " + pname + ": " + TypeName(wireType));
                Put(global + leaf.Path + " = " + ConvertExpr(pname, wireType, leaf.Type) + ";");
            }
        }

        var fields = new List<string>();
        var args = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in _ep.Outputs)
        {
            string global = _globalNames[g];
            foreach (var leaf in Leaves(g))
            {
                string attr;
                HlslType wireType = leaf.Type;
                if (g.BuiltIn != BuiltIn.None)
                {
                    if (BuiltInInfo(g) is not { } info)
                    {
                        continue;
                    }

                    attr = "@builtin(" + info.Attr + ")";
                    wireType = info.Type;
                }
                else
                {
                    if (leaf.Type.ScalarElement is { ScalarKind: ScalarKind.Bool })
                    {
                        Unsupported("The stage output '" + g.Name + "' is a bool; WGSL stage outputs must be numeric. Use an int.", g.Span);
                        continue;
                    }

                    attr = "@location(" + Inv(leaf.Location) + ")";
                    if (InterpolationAttribute(g, leaf.Type, false) is { } interp)
                    {
                        attr += " " + interp;
                    }
                }

                string field = WgslNames.Unique(used, WgslNames.Sanitize(leaf.Name, "out"));
                fields.Add("  " + attr + " " + field + ": " + TypeName(wireType) + ",\n");
                args.Add(ConvertExpr(global + leaf.Path, leaf.Type, wireType));
            }
        }

        var header = new StringBuilder();
        string entry = _fnNames[_ep.Function];
        string? returnType = null;
        if (fields.Count > 0)
        {
            returnType = WgslNames.Unique(_moduleNames, entry + "_Output");
            _structDecls.Add("struct " + returnType + " {\n" + string.Concat(fields) + "}\n");
            _entryReturnExpr = returnType + "(" + string.Join(", ", args) + ")";
        }

        switch (_ep.Stage)
        {
            case ShaderStage.Vertex:
                header.Append("@vertex\n");
                break;
            case ShaderStage.Pixel:
                header.Append("@fragment\n");
                break;
            default:
            {
                var (x, y, z) = _ep.WorkgroupSize;
                if (x == 0 || y == 0 || z == 0)
                {
                    (x, y, z) = (1, 1, 1);
                }

                header.Append("@compute @workgroup_size(").Append(Inv(x)).Append(", ").Append(Inv(y)).Append(", ").Append(Inv(z)).Append(")\n");
                break;
            }
        }

        header.Append("fn ").Append(entry).Append('(').Append(string.Join(", ", ps)).Append(')');
        if (returnType is not null)
        {
            header.Append(" -> ").Append(returnType);
        }

        return header.ToString();
    }
}
