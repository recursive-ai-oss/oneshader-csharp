using System.Collections.Generic;
using OneShader.Diagnostics;
using OneShader.IR;
using OneShader.Types;

namespace OneShader.Wgsl;

/// <summary>
/// Lets the tests read back every buffer access the backend would write, without emitting a shader: for each node of a
/// buffer's type the text of the read. The tests parse the word indices out of that text and compare the byte offsets
/// with the reflected layout.
/// </summary>
internal sealed partial class WgslEmitter
{
    /// <summary>Prepares an emitter for <paramref name="entryPointName"/> far enough to answer <see cref="EnumerateBufferAccessors"/>; null when the entry point does not exist.</summary>
    internal static WgslEmitter? ForAccessorInspection(IrModule module, string entryPointName, DiagnosticBag diagnostics)
    {
        var emitter = new WgslEmitter(module, new WgslOptions(), diagnostics);
        foreach (var e in module.EntryPoints)
        {
            if (e.Function.Name == entryPointName)
            {
                emitter._ep = e;
                emitter.Scan();
                emitter.RegisterNames();
                emitter.PrepareBuffers();
                return emitter;
            }
        }

        return null;
    }

    /// <summary>The element indices of an array the inspection visits: the first two and the last.</summary>
    internal static IEnumerable<int> SampleIndices(int length)
    {
        for (int k = 0; k < length && k < 2; k++)
        {
            yield return k;
        }

        if (length > 2)
        {
            yield return length - 1;
        }
    }

    /// <summary>
    /// Every scalar, vector and matrix inside every buffer the entry point uses, as (buffer name, path, type, read expression).
    /// Paths use <c>.member</c>, <c>[index]</c> and, for a matrix element, <c>[row][column]</c>; arrays are sampled with <see cref="SampleIndices"/>
    /// and a structured buffer's elements are 0 and 3.
    /// </summary>
    internal List<(string Buffer, string Path, HlslType Type, string Accessor)> EnumerateBufferAccessors()
    {
        var list = new List<(string, string, HlslType, string)>();
        foreach (var g in _m.Globals)
        {
            if (!_bufs.TryGetValue(g, out var info) || info.ByteAddress)
            {
                continue;
            }

            var root = GlobalMem(g) as BufMem;
            if (root is null)
            {
                continue;
            }

            if (info.Uniform)
            {
                Walk(list, info, root.Cursor, "", 0);
            }
            else
            {
                foreach (int e in new[] { 0, 3 })
                {
                    var element = StepBuf(root, Constant.Int(e));
                    Walk(list, info, element.Cursor, "[" + Inv(e) + "]", 0);
                }
            }
        }

        return list;
    }

    private void Walk(List<(string, string, HlslType, string)> list, BufInfo info, Cur c, string path, int depth)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        switch (c.Type)
        {
            case StructType st:
                for (int k = 0; k < st.Fields.Count; k++)
                {
                    Walk(list, info, Child(c, k), path + "." + st.Fields[k].Name, depth + 1);
                }

                break;
            case ArrayType a:
                foreach (int k in SampleIndices(a.Length))
                {
                    Walk(list, info, Child(c, k), path + "[" + Inv(k) + "]", depth + 1);
                }

                break;
            default:
                list.Add((info.Name, path, c.Type, ReadBuf(info, c)));
                break;
        }
    }
}
