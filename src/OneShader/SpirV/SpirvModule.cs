using System;
using System.Collections.Generic;
using System.Text;

namespace OneShader.SpirV;

/// <summary>What the emitter remembers about a SPIR-V type so access chains can be walked without re-reading words.</summary>
internal sealed class TypeInfo(TypeInfoKind kind, uint[] children)
{
    public TypeInfoKind Kind { get; } = kind;

    /// <summary>Struct: member types. Array, vector, matrix: one element type.</summary>
    public uint[] Children { get; } = children;
}

internal enum TypeInfoKind
{
    Other,
    Struct,
    Array,
    Vector,
    Matrix,
}

/// <summary>
/// Collects a SPIR-V module in its logical sections and serializes it. Ids are handed out in the
/// order they are requested, which is deterministic because the emitter walks the IR in a fixed order.
/// </summary>
internal sealed class SpirvModule
{
    private uint _bound = 1;
    private readonly Dictionary<string, uint> _typeKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, TypeInfo> _typeInfo = [];

    public SortedSet<uint> Capabilities { get; } = [];

    public List<uint> ExtImports { get; } = [];

    /// <summary><c>OpExtension</c> lines; empty unless debug information needs one.</summary>
    public List<uint> Extensions { get; } = [];

    /// <summary><c>OpString</c>, <c>OpSource</c> and <c>OpSourceContinued</c>: they precede the names in the debug section.</summary>
    public List<uint> DebugStrings { get; } = [];

    /// <summary>Debug-info records that live among the global declarations, after every type and constant they refer to.</summary>
    public List<uint> DebugGlobals { get; } = [];

    public List<uint> EntryPoints { get; } = [];

    public List<uint> ExecutionModes { get; } = [];

    public List<uint> Debug { get; } = [];

    public List<uint> Annotations { get; } = [];

    public List<uint> Types { get; } = [];

    public List<uint> Code { get; } = [];

    public uint NewId() => _bound++;

    /// <summary>Appends one instruction to a section.</summary>
    public static void Put(List<uint> section, uint op, params uint[] operands)
    {
        section.Add(((uint)(operands.Length + 1) << 16) | op);
        section.AddRange(operands);
    }

    /// <summary>Appends an instruction whose operands are some ids, a string, then more words.</summary>
    public static void PutWithString(List<uint> section, uint op, ReadOnlySpan<uint> before, string text, ReadOnlySpan<uint> after)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        int strWords = bytes.Length / 4 + 1;
        section.Add(((uint)(1 + before.Length + strWords + after.Length) << 16) | op);
        foreach (var w in before)
        {
            section.Add(w);
        }

        for (int i = 0; i < strWords; i++)
        {
            uint w = 0;
            for (int k = 0; k < 4; k++)
            {
                int at = i * 4 + k;
                if (at < bytes.Length)
                {
                    w |= (uint)bytes[at] << (8 * k);
                }
            }

            section.Add(w);
        }

        foreach (var w in after)
        {
            section.Add(w);
        }
    }

    /// <summary>Declares a type once per key and returns its id.</summary>
    public uint DeclType(string key, uint op, params uint[] operands)
    {
        if (_typeKeys.TryGetValue(key, out var existing))
        {
            return existing;
        }

        uint id = NewId();
        var all = new uint[operands.Length + 1];
        all[0] = id;
        Array.Copy(operands, 0, all, 1, operands.Length);
        Put(Types, op, all);
        _typeKeys[key] = id;
        return id;
    }

    public bool TryGetKey(string key, out uint id) => _typeKeys.TryGetValue(key, out id);

    public void SetKey(string key, uint id) => _typeKeys[key] = id;

    public void SetInfo(uint type, TypeInfoKind kind, params uint[] children) => _typeInfo[type] = new TypeInfo(kind, children);

    public TypeInfo? Info(uint type) => _typeInfo.TryGetValue(type, out var i) ? i : null;

    public void Decorate(uint target, uint decoration, params uint[] args)
    {
        var all = new uint[args.Length + 2];
        all[0] = target;
        all[1] = decoration;
        Array.Copy(args, 0, all, 2, args.Length);
        Put(Annotations, Op.Decorate, all);
    }

    public void MemberDecorate(uint structType, uint member, uint decoration, params uint[] args)
    {
        var all = new uint[args.Length + 3];
        all[0] = structType;
        all[1] = member;
        all[2] = decoration;
        Array.Copy(args, 0, all, 3, args.Length);
        Put(Annotations, Op.MemberDecorate, all);
    }

    public void Name(uint target, string name) => PutWithString(Debug, Op.Name, [target], name, []);

    public void MemberName(uint target, uint member, string name) => PutWithString(Debug, Op.MemberName, [target, member], name, []);

    /// <summary>Serializes the module as little-endian words.</summary>
    public byte[] ToBytes()
    {
        var words = new List<uint>(Types.Count + Code.Count + 64)
        {
            0x07230203u,
            0x00010500u, // SPIR-V 1.5, the version the Vulkan 1.2 environment accepts.
            0u,
            _bound,
            0u,
        };
        foreach (var c in Capabilities)
        {
            Put(words, Op.Capability, c);
        }

        words.AddRange(Extensions);
        words.AddRange(ExtImports);

        // OpMemoryModel Logical GLSL450
        Put(words, Op.MemoryModel, 0, 1);
        words.AddRange(EntryPoints);
        words.AddRange(ExecutionModes);
        words.AddRange(DebugStrings);
        words.AddRange(Debug);
        words.AddRange(Annotations);
        words.AddRange(Types);
        words.AddRange(DebugGlobals);
        words.AddRange(Code);

        var bytes = new byte[words.Count * 4];
        for (int i = 0; i < words.Count; i++)
        {
            uint w = words[i];
            bytes[i * 4] = (byte)w;
            bytes[i * 4 + 1] = (byte)(w >> 8);
            bytes[i * 4 + 2] = (byte)(w >> 16);
            bytes[i * 4 + 3] = (byte)(w >> 24);
        }

        return bytes;
    }
}
