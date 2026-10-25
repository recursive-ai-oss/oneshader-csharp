using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using OneShader.Diagnostics;
using OneShader.Reflection;
using OneShader.Semantics;
using OneShader.Types;
using OneShader.Wgsl;
using Xunit;

namespace OneShader.Tests;

/// <summary>
/// The WGSL target reads every buffer as raw 32-bit words, so a layout mistake would silently move data. These tests take
/// every scalar, vector and matrix in every constant and structured buffer of the corpus, parse the word indices out of the
/// text the backend writes to read it, and require the byte offsets to equal the reflected layout, which is also what
/// the SPIR-V target decorates.
/// </summary>
public sealed partial class WgslBackendTests
{
    private static readonly Regex WordRef = new(@"[A-Za-z_]\w*\[(?<a>\d+)u\](?:\[(?<b>\d+)u\]|\.(?<s>[xyzw]+))?", RegexOptions.CultureInvariant);

    internal static List<int> OffsetsIn(string accessor, bool uniform)
    {
        var offsets = new List<int>();
        foreach (Match m in WordRef.Matches(accessor))
        {
            int a = int.Parse(m.Groups["a"].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (!uniform)
            {
                offsets.Add(a * 4);
            }
            else if (m.Groups["b"].Success)
            {
                offsets.Add((a * 16) + (int.Parse(m.Groups["b"].Value, System.Globalization.CultureInfo.InvariantCulture) * 4));
            }
            else
            {
                foreach (char c in m.Groups["s"].Value)
                {
                    offsets.Add((a * 16) + ("xyzw".IndexOf(c, StringComparison.Ordinal) * 4));
                }
            }
        }

        return offsets;
    }

    private static void ExpectedOffsets(ReflectedType t, string path, int offset, Dictionary<string, int[]> into)
    {
        switch (t.Kind)
        {
            case ReflectedTypeKind.Struct:
                foreach (var m in t.Members)
                {
                    ExpectedOffsets(m.Type, path + "." + m.Name, offset + (int)m.Offset, into);
                }

                break;
            case ReflectedTypeKind.Array:
                foreach (int k in WgslEmitterAccess.SampleIndices((int)t.ArrayLength))
                {
                    ExpectedOffsets(t.Element!, path + "[" + k + "]", offset + (k * (int)t.ArrayStride), into);
                }

                break;
            case ReflectedTypeKind.Vector:
                into[path] = Enumerable.Range(0, (int)t.Rows).Select(c => offset + (c * 4)).ToArray();
                break;
            case ReflectedTypeKind.Matrix:
            {
                var list = new List<int>();
                for (int i = 0; i < t.Rows; i++)
                {
                    for (int j = 0; j < t.Columns; j++)
                    {
                        // Element (i, j): a column-major matrix stores columns, a row-major one rows.
                        list.Add(offset + (t.Packing == MatrixPacking.ColumnMajor ? (j * (int)t.MatrixStride) + (i * 4) : (i * (int)t.MatrixStride) + (j * 4)));
                    }
                }

                into[path] = list.ToArray();
                break;
            }

            default:
                into[path] = [offset];
                break;
        }
    }

    [Theory]
    [MemberData(nameof(CorpusFileNames))]
    public void BufferAccessorsReadTheReflectedOffsets(string path)
    {
        var options = OptionsFor(path);
        var fe = Frontend.Run(File.ReadAllText(path), Path.GetFileName(path), options, TestContext.Current.CancellationToken);
        if (fe.Module is null || fe.Reflection is null)
        {
            return; // The front end rejects this file; nothing reaches the backend.
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        int checkedCount = 0;
        foreach (var entry in fe.Module.EntryPoints)
        {
            var emitter = WgslEmitterAccess.Inspect(fe.Module, entry.Function.Name);
            Assert.NotNull(emitter);
            foreach (var (buffer, p, type, accessor) in emitter!.Accessors())
            {
                string key = buffer + p;
                if (!seen.Add(key))
                {
                    continue;
                }

                var resource = fe.Reflection.Resources.Single(r => WgslEmitterAccess.Sanitize(r.Name) == buffer);
                Assert.NotNull(resource.Layout);
                bool uniform = resource.Kind == ResourceKind.ConstantBuffer;
                var expected = new Dictionary<string, int[]>();
                if (uniform)
                {
                    ExpectedOffsets(resource.Layout!, "", 0, expected);
                }
                else
                {
                    int stride = (int)Math.Max(resource.Layout!.Size, 1);
                    int align = (int)Math.Max(resource.Layout.Alignment, 1);
                    stride = (stride + align - 1) / align * align;
                    foreach (int e in new[] { 0, 3 })
                    {
                        ExpectedOffsets(resource.Layout, "[" + e + "]", e * stride, expected);
                    }
                }

                Assert.True(expected.TryGetValue(p, out var want), Path.GetFileName(path) + ": " + buffer + p + " is not in the reflected layout");
                var got = OffsetsIn(accessor, uniform);
                Assert.True(want!.SequenceEqual(got), Path.GetFileName(path) + ": " + buffer + p + " (" + type + ") reads bytes [" + string.Join(",", got) + "] but the layout says [" + string.Join(",", want) + "]\n" + accessor);
                checkedCount++;
            }
        }

        if (fe.Reflection.Resources.Any(r => r.Kind == ResourceKind.ConstantBuffer || r.ResourceType is ResourceTypeKind.StructuredBuffer or ResourceTypeKind.RWStructuredBuffer))
        {
            Assert.True(checkedCount > 0, Path.GetFileName(path) + " has buffers but no accessor was checked");
        }
    }
}

/// <summary>Reaches the backend's inspection hooks.</summary>
internal sealed class WgslEmitterAccess
{
    private readonly WgslEmitter _emitter;

    private WgslEmitterAccess(WgslEmitter e) => _emitter = e;

    public static IEnumerable<int> SampleIndices(int length) => WgslEmitter.SampleIndices(length);

    public static string Sanitize(string name) => WgslNames.Sanitize(name, "g");

    public static WgslEmitterAccess? Inspect(OneShader.IR.IrModule module, string entry) =>
        WgslEmitter.ForAccessorInspection(module, entry, new DiagnosticBag()) is { } e ? new WgslEmitterAccess(e) : null;

    public IEnumerable<(string Buffer, string Path, HlslType Type, string Accessor)> Accessors() => _emitter.EnumerateBufferAccessors();
}
