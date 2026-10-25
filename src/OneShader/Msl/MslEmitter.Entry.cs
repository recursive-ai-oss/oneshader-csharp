using System;
using System.Collections.Generic;
using System.Text;
using OneShader.Diagnostics;
using OneShader.IR;
using OneShader.Reflection;
using OneShader.Types;

namespace OneShader.Msl;

internal sealed partial class MslEmitter
{
    private readonly Dictionary<IrFunction, string> _fnNames = [];

    // State of the function being emitted.
    private IrFunction _fn = null!;
    private bool _isEntry;
    private readonly HashSet<Instruction> _hoisted = [];
    private HashSet<BasicBlock> _reachable = [];
    private string _outVar = "";
    private List<OutMember> _outMembers = [];

    private sealed class OutMember(GlobalVariable global, string member, int clipLength)
    {
        public GlobalVariable Global { get; } = global;

        public string Member { get; } = member;

        public int ClipLength { get; } = clipLength;
    }

    // ---- helper functions --------------------------------------------------------------------------------

    private string LiftedParam(GlobalVariable g)
    {
        string name = NameOf(g);
        if (g.GlobalKind == GlobalKind.Resource)
        {
            return _res.TryGetValue(g, out var r) ? ResourceParamType(r) + " " + name : "int " + name;
        }

        return (g.GlobalKind == GlobalKind.Workgroup ? "threadgroup " : "thread ") + T(g.ValueType) + "& " + name;
    }

    private void EmitFunction(IrFunction f, bool isEntry)
    {
        _fn = f;
        _isEntry = isEntry;
        string name = _names.Claim(f.Name, "fn");
        _fnNames[f] = name;
        var parms = new List<string>();
        foreach (var a in f.Parameters)
        {
            string pn = NameOf(a);
            if (a.Type is PointerType pt)
            {
                if (pt.Space is AddressSpace.Uniform or AddressSpace.PushConstant or AddressSpace.StorageBuffer or AddressSpace.UniformConstant)
                {
                    Unsupported("A resource is passed to '" + f.Name + "' as an argument; the function must be inlined first (mark it [forceinline]).", f.Span);
                }

                parms.Add(SpaceOf(pt.Space) + " " + T(pt.Pointee) + "& " + pn);
            }
            else
            {
                parms.Add(T(a.Type) + " " + pn);
            }
        }

        foreach (var g in _needed[f])
        {
            parms.Add(LiftedParam(g));
        }

        string sig = T(f.ReturnType) + " " + name + "(" + string.Join(", ", parms) + ")";
        _fnText.Append(sig).Append('\n').Append("{\n");
        EmitBody(f, null);
        _fnText.Append("}\n\n");
    }

    // ---- shared function body machinery ---------------------------------------------------------------

    private void EmitBody(IrFunction f, Action? prologue)
    {
        _out = _fnText;
        _indent = 1;
        _hoisted.Clear();
        _places.Clear();
        _pdom = null;
        _blockBudget = 0;
        _constructs.Clear();
        _reachable = ComputeReachable(f);
        AnalyzeUses(f);
        prologue?.Invoke();
        if (f.Entry is { } entry)
        {
            foreach (var i in entry.Instructions)
            {
                if (i.Op == Opcode.Alloca)
                {
                    W(T(((PointerType)i.Type).Pointee) + " " + NameOf(i) + ";");
                }
            }
        }

        foreach (var b in f.Blocks)
        {
            if (!_reachable.Contains(b))
            {
                continue;
            }

            foreach (var i in b.Instructions)
            {
                if (_hoisted.Contains(i))
                {
                    W(T(i.Type) + " " + NameOf(i) + ";");
                }
            }
        }

        if (f.Entry is not null)
        {
            EmitChain(f.Entry, []);
        }

        FinishBody(f);
    }

    private void FinishBody(IrFunction f)
    {
        string text = _fnText.ToString();
        int end = text.Length;
        int start = text.LastIndexOf('\n', end - 2) + 1;
        string last = text.Substring(start, end - start).TrimEnd('\n');
        if (f.ReturnType.IsVoid)
        {
            if (last == "    return;")
            {
                _fnText.Length = start;
            }

            return;
        }

        if (!last.StartsWith("    return", StringComparison.Ordinal) && !_isEntry)
        {
            W("return " + T(f.ReturnType) + "();");
        }
    }

    private static HashSet<BasicBlock> ComputeReachable(IrFunction f)
    {
        var seen = new HashSet<BasicBlock>();
        if (f.Entry is null)
        {
            return seen;
        }

        var stack = new Stack<BasicBlock>();
        stack.Push(f.Entry);
        seen.Add(f.Entry);
        while (stack.Count > 0)
        {
            var b = stack.Pop();
            foreach (var s in b.Successors)
            {
                if (seen.Add(s))
                {
                    stack.Push(s);
                }
            }
        }

        return seen;
    }

    /// <summary>
    /// Finds the values that need a declaration at function scope: phis, and any value used outside its defining block
    /// (including through the text of a pointer expression, which is rebuilt at every use).
    /// </summary>
    private void AnalyzeUses(IrFunction f)
    {
        var work = new Stack<IrValue>();
        foreach (var b in f.Blocks)
        {
            if (!_reachable.Contains(b))
            {
                continue;
            }

            foreach (var i in b.Instructions)
            {
                if (i.Op == Opcode.Phi)
                {
                    _hoisted.Add(i);
                }

                for (int k = 0; k < i.Operands.Count; k++)
                {
                    var useBlock = i.Op == Opcode.Phi && k < i.Targets.Count ? i.Targets[k] : b;
                    work.Push(i.Operands[k]);
                    while (work.Count > 0)
                    {
                        var v = work.Pop();
                        if (v is not Instruction d)
                        {
                            continue;
                        }

                        if (d.Op == Opcode.AccessChain)
                        {
                            foreach (var op in d.Operands)
                            {
                                work.Push(op);
                            }

                            continue;
                        }

                        if (d.Type is PointerType || d.IsVoid)
                        {
                            continue;
                        }

                        if (d.Parent != useBlock)
                        {
                            _hoisted.Add(d);
                        }
                    }
                }
            }
        }
    }

    // ---- entry point --------------------------------------------------------------------------------------

    private void EmitEntryFunction()
    {
        var ep = _ep;
        var f = ep.Function;
        _fn = f;
        _isEntry = true;
        var parms = new List<string>();
        var prologueLines = new List<string>();
        string returnType = "void";
        string stageWord = ep.Stage switch { ShaderStage.Vertex => "vertex", ShaderStage.Pixel => "fragment", _ => "kernel" };

        // Resources first, in a stable order, then stage variables.
        var stageIn = new List<(GlobalVariable G, string Member, string Attr)>();
        var builtinParams = new List<string>();
        foreach (var g in ep.Inputs)
        {
            string local = NameOf(g);
            if (g.BuiltIn == BuiltIn.None)
            {
                if (ep.Stage == ShaderStage.Compute)
                {
                    Unsupported("The compute input '" + (g.Semantic ?? g.Name) + "' is not a system value; compute entry points take only SV_DispatchThreadID, SV_GroupThreadID, SV_GroupID and SV_GroupIndex.", g.Span);
                    continue;
                }

                if (g.Location is not { } loc)
                {
                    Malformed("The stage variable '" + g.Name + "' has neither a location nor a built-in; give it a semantic.", g.Span);
                    continue;
                }

                string attr = ep.Stage == ShaderStage.Vertex ? "[[attribute(" + Inv(loc) + ")]]" : "[[user(locn" + Inv(loc) + ")]]" + InterpolationAttr(g);
                stageIn.Add((g, UniqueInName(local, stageIn), attr));
                continue;
            }

            InputBuiltIn(ep.Stage, g, local, builtinParams, prologueLines);
        }

        string? inStruct = null;
        if (stageIn.Count > 0)
        {
            inStruct = _names.Claim(_entryName + "_in", "stage_in_t");
            var sb = new StringBuilder("struct ").Append(inStruct).Append("\n{\n");
            foreach (var (g, member, attr) in stageIn)
            {
                sb.Append("    ").Append(T(g.ValueType)).Append(' ').Append(member).Append(' ').Append(attr).Append(";\n");
            }

            sb.Append("};\n\n");
            _typeText.Append(sb);
            string inName = _names.ClaimExact("stage_in");
            parms.Add(inStruct + " " + inName + " [[stage_in]]");
            foreach (var (g, member, _) in stageIn)
            {
                prologueLines.Add(T(g.ValueType) + " " + NameOf(g) + " = " + inName + "." + member + ";");
            }
        }

        _outMembers = [];
        string? outStruct = null;
        if (ep.Stage != ShaderStage.Compute && ep.Outputs.Count > 0)
        {
            var sb = new StringBuilder();
            var taken = new HashSet<string>(StringComparer.Ordinal);
            bool any = false;
            outStruct = _names.Claim(_entryName + "_out", "stage_out_t");
            sb.Append("struct ").Append(outStruct).Append("\n{\n");
            foreach (var g in ep.Outputs)
            {
                string? decl = OutputDecl(ep.Stage, g, taken, out var member, out int clipLength);
                if (decl is null)
                {
                    continue;
                }

                any = true;
                sb.Append("    ").Append(decl).Append('\n');
                _outMembers.Add(new OutMember(g, member, clipLength));
            }

            sb.Append("};\n\n");
            if (any)
            {
                _typeText.Append(sb);
                returnType = outStruct;
            }
            else
            {
                outStruct = null;
            }
        }
        else if (ep.Stage == ShaderStage.Compute && ep.Outputs.Count > 0)
        {
            Unsupported("A compute entry point cannot have stage outputs.", f.Span);
        }

        parms.AddRange(builtinParams);

        // Resources, in declaration order.
        var resourceParams = new List<string>();
        foreach (var g in _needed[f])
        {
            if (g.GlobalKind == GlobalKind.Resource && _res.TryGetValue(g, out var r))
            {
                if (IsBufferArray(r))
                {
                    // Metal rejects arrays of buffers as arguments (array<T*, N> and T* x[N] both fail), so each element is
                    // an argument at its own index and the shader sees them gathered back into an array.
                    string one = ResourceElementParamType(r);
                    var members = new List<string>();
                    for (int k = 0; k < r.Count; k++)
                    {
                        string member = _names.Claim(NameOf(g) + "_" + Inv(k), "res");
                        members.Add(member);
                        resourceParams.Add(one + " " + member + " [[buffer(" + Inv(r.Index + k) + ")]]");
                    }

                    prologueLines.Add(ResourceParamType(r) + " " + NameOf(g) + " = { " + string.Join(", ", members) + " };");
                    continue;
                }

                resourceParams.Add(ResourceParamType(r) + " " + NameOf(g) + " " + ResourceAttribute(r));
            }
        }

        parms.AddRange(resourceParams);
        _outVar = outStruct is null ? "" : _names.ClaimExact("stage_out");
        string attrs = ep.Stage == ShaderStage.Compute && ep.WorkgroupSize is var (x, y, z) && x > 0 && y > 0 && z > 0
            ? "[[max_total_threads_per_threadgroup(" + Inv((long)x * y * z) + ")]] "
            : string.Empty;
        _fnNames[f] = _entryName;
        var head = new StringBuilder();
        if (ep.Stage == ShaderStage.Compute)
        {
            var (wx, wy, wz) = ep.WorkgroupSize;
            head.Append("// threadgroup size: ").Append(Inv(wx)).Append(' ').Append(Inv(wy)).Append(' ').Append(Inv(wz)).Append('\n');
        }

        head.Append(attrs).Append(stageWord).Append(' ').Append(returnType).Append(' ').Append(_entryName).Append("(\n");
        for (int i = 0; i < parms.Count; i++)
        {
            head.Append("    ").Append(parms[i]).Append(i + 1 < parms.Count ? ",\n" : ")\n");
        }

        if (parms.Count == 0)
        {
            head.Length -= 2;
            head.Append("()\n");
        }

        _fnText.Append(head).Append("{\n");
        EmitBody(f, () =>
        {
            foreach (var line in prologueLines)
            {
                W(line);
            }

            foreach (var g in _needed[f])
            {
                switch (g.GlobalKind)
                {
                    case GlobalKind.Private:
                        W(T(g.ValueType) + " " + NameOf(g) + " = " + (g.Initializer is null ? T(g.ValueType) + "()" : ConstExpr(g.Initializer, 0)) + ";");
                        break;
                    case GlobalKind.Workgroup:
                        W("threadgroup " + T(g.ValueType) + " " + NameOf(g) + ";");
                        break;
                    case GlobalKind.Output:
                        W(T(g.ValueType) + " " + NameOf(g) + " = " + T(g.ValueType) + "();");
                        break;
                    default:
                        break;
                }
            }

            if (_outVar.Length > 0)
            {
                W(returnType + " " + _outVar + ";");
            }
        });
        _fnText.Append("}\n");
    }

    private static string UniqueInName(string local, List<(GlobalVariable G, string Member, string Attr)> existing)
    {
        string baseName = MslNames.Sanitize(local, "in");
        string name = baseName;
        for (int n = 1; ; n++)
        {
            bool clash = false;
            foreach (var e in existing)
            {
                if (e.Member == name)
                {
                    clash = true;
                    break;
                }
            }

            if (!clash)
            {
                return name;
            }

            name = baseName + "_" + Inv(n);
        }
    }

    private static string InterpolationAttr(GlobalVariable g)
    {
        var scalar = g.ValueType.ScalarElement;
        bool integerLike = scalar is not null && (scalar.IsInteger || scalar.ScalarKind == ScalarKind.Bool);
        if (g.Interpolation == Interpolation.Flat || integerLike)
        {
            return " [[flat]]";
        }

        return g.Interpolation switch
        {
            Interpolation.NoPerspective => " [[center_no_perspective]]",
            Interpolation.Centroid => " [[centroid_perspective]]",
            Interpolation.Sample => " [[sample_perspective]]",
            _ => string.Empty,
        };
    }

    private void InputBuiltIn(ShaderStage stage, GlobalVariable g, string local, List<string> parms, List<string> prologue)
    {
        string? type = null;
        string? attr = null;
        string? expr = null;
        switch (g.BuiltIn)
        {
            case BuiltIn.VertexIndex when stage == ShaderStage.Vertex:
                // Metal's vertex_id already includes the draw's base vertex (checked on a GPU: indexed draws report index + baseVertex),
                // which is what Vulkan's VertexIndex is, so adding [[base_vertex]] would count it twice.
                type = "uint";
                attr = "[[vertex_id]]";
                break;
            case BuiltIn.InstanceIndex when stage == ShaderStage.Vertex:
                // Likewise instance_id includes the base instance, as Vulkan's InstanceIndex does.
                type = "uint";
                attr = "[[instance_id]]";
                break;

            case BuiltIn.FragCoord when stage == ShaderStage.Pixel:
                type = "float4";
                attr = "[[position]]";
                break;
            case BuiltIn.FrontFacing when stage == ShaderStage.Pixel:
                type = "bool";
                attr = "[[front_facing]]";
                break;
            case BuiltIn.SampleIndex when stage == ShaderStage.Pixel:
                type = "uint";
                attr = "[[sample_id]]";
                break;
            case BuiltIn.SampleMask when stage == ShaderStage.Pixel:
                type = "uint";
                attr = "[[sample_mask]]";
                break;
            case BuiltIn.PrimitiveIndex when stage == ShaderStage.Pixel:
                type = "uint";
                attr = "[[primitive_id]]";
                break;
            case BuiltIn.Layer when stage == ShaderStage.Pixel:
                type = "uint";
                attr = "[[render_target_array_index]]";
                break;
            case BuiltIn.ViewportIndex when stage == ShaderStage.Pixel:
                type = "uint";
                attr = "[[viewport_array_index]]";
                break;
            case BuiltIn.GlobalInvocationId when stage == ShaderStage.Compute:
                type = "uint3";
                attr = "[[thread_position_in_grid]]";
                break;
            case BuiltIn.LocalInvocationId when stage == ShaderStage.Compute:
                type = "uint3";
                attr = "[[thread_position_in_threadgroup]]";
                break;
            case BuiltIn.WorkgroupId when stage == ShaderStage.Compute:
                type = "uint3";
                attr = "[[threadgroup_position_in_grid]]";
                break;
            case BuiltIn.LocalInvocationIndex when stage == ShaderStage.Compute:
                type = "uint";
                attr = "[[thread_index_in_threadgroup]]";
                break;
            default:
                Unsupported("The system value " + (g.Semantic ?? g.BuiltIn.ToString()) + " cannot be an input of a " + stage + " shader in MSL.", g.Span);
                return;
        }

        if (expr is null)
        {
            string pname = _names.ClaimExact(BuiltInParamHint(g.BuiltIn));
            parms.Add(type + " " + pname + " " + attr);
            expr = pname;
        }

        string localType = T(g.ValueType);
        prologue.Add(localType + " " + local + " = " + (localType == type ? expr : localType + "(" + expr + ")") + ";");
    }

    private static string BuiltInParamHint(BuiltIn b) => b switch
    {
        BuiltIn.FragCoord => "frag_coord",
        BuiltIn.FrontFacing => "front_facing",
        BuiltIn.SampleIndex => "sample_id",
        BuiltIn.SampleMask => "sample_mask_in",
        BuiltIn.PrimitiveIndex => "primitive_id",
        BuiltIn.Layer => "render_target_array_index",
        BuiltIn.ViewportIndex => "viewport_array_index",
        BuiltIn.GlobalInvocationId => "thread_position_in_grid",
        BuiltIn.LocalInvocationId => "thread_position_in_threadgroup",
        BuiltIn.WorkgroupId => "threadgroup_position_in_grid",
        _ => "thread_index_in_threadgroup",
    };

    /// <summary>The declaration line of an output struct member, or null (after reporting) when the output cannot be expressed.</summary>
    private string? OutputDecl(ShaderStage stage, GlobalVariable g, HashSet<string> taken, out string member, out int clipLength)
    {
        member = UniqueMember(NameOf(g), "out", taken);
        clipLength = 0;
        string type = T(g.ValueType);
        if (g.BuiltIn == BuiltIn.None)
        {
            if (g.Location is not { } loc)
            {
                Malformed("The stage variable '" + g.Name + "' has neither a location nor a built-in; give it a semantic.", g.Span);
                return null;
            }

            return stage == ShaderStage.Vertex
                ? type + " " + member + " [[user(locn" + Inv(loc) + ")]];"
                : type + " " + member + " [[color(" + Inv(loc) + ")]];";
        }

        switch (g.BuiltIn)
        {
            case BuiltIn.Position when stage == ShaderStage.Vertex:
                return type + " " + member + " [[position]];";
            case BuiltIn.Layer when stage == ShaderStage.Vertex:
                return "uint " + member + " [[render_target_array_index]];";
            case BuiltIn.ViewportIndex when stage == ShaderStage.Vertex:
                return "uint " + member + " [[viewport_array_index]];";
            case BuiltIn.ClipDistance when stage == ShaderStage.Vertex && g.ValueType is ArrayType ca:
                clipLength = ca.Length;
                return "float " + member + " [[clip_distance]] [" + Inv(ca.Length) + "];";
            case BuiltIn.FragDepth when stage == ShaderStage.Pixel:
                return "float " + member + " [[depth(any)]];";
            case BuiltIn.SampleMask when stage == ShaderStage.Pixel:
                return "uint " + member + " [[sample_mask]];";
            case BuiltIn.CullDistance:
                Unsupported("MSL has no cull distance output; remove SV_CullDistance or clip with SV_ClipDistance.", g.Span);
                return null;
            default:
                Unsupported("The system value " + (g.Semantic ?? g.BuiltIn.ToString()) + " cannot be an output of a " + stage + " shader in MSL.", g.Span);
                return null;
        }
    }

    /// <summary>Copies the output variables into the result struct and returns it (entry point <c>ret</c> and <c>discard</c>).</summary>
    private void EmitEntryReturn()
    {
        if (_outVar.Length == 0)
        {
            W("return;");
            return;
        }

        foreach (var o in _outMembers)
        {
            string local = NameOf(o.Global);
            if (o.ClipLength > 0)
            {
                string k = Tmp("i");
                W("for (int " + k + " = 0; " + k + " < " + Inv(o.ClipLength) + "; ++" + k + ")");
                W("{");
                _indent++;
                W(_outVar + "." + o.Member + "[" + k + "] = " + local + "[" + k + "];");
                _indent--;
                W("}");
            }
            else
            {
                W(_outVar + "." + o.Member + " = " + local + ";");
            }
        }

        W("return " + _outVar + ";");
    }
}
