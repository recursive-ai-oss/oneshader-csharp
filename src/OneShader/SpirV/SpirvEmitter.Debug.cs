using System;
using System.Collections.Generic;
using OneShader.Diagnostics;
using OneShader.IR;
using OneShader.Types;

namespace OneShader.SpirV;

// Source-level debug information. Two forms, chosen by SpirvOptions.DebugInfo:
//   Lines: OpString + OpSource (with the file's text) + OpLine/OpNoLine, as DXC's -Zi writes.
//   Full:  the NonSemantic.Shader.DebugInfo.100 instruction set (Khronos SPIR-V registry), as DXC's
//          -fspv-debug=vulkan-with-source writes: compilation unit, one DebugSource per file with its text,
//          DebugFunction and DebugLexicalBlock scopes, DebugInlinedAt for inlined calls, DebugLocalVariable with
//          DebugDeclare/DebugValue, DebugType* records, DebugLine/DebugNoLine, DebugFunctionDefinition, DebugEntryPoint.
// Instruction positions are the IR's source spans (they survive every optimizer pass), so scopes are found by
// asking which source function and which brace block contain a span, not by following IR structure.
internal sealed partial class SpirvEmitter
{
    // Instruction numbers of NonSemantic.Shader.DebugInfo.100.
    private const uint DbgNone = 0, DbgCompilationUnit = 1, DbgTypeBasic = 2, DbgTypeArray = 5, DbgTypeVector = 6, DbgTypeFunction = 8,
        DbgTypeComposite = 10, DbgTypeMember = 11, DbgGlobalVariable = 18, DbgFunction = 20, DbgLexicalBlock = 21, DbgScope = 23,
        DbgInlinedAt = 25, DbgLocalVariable = 26, DbgDeclare = 28, DbgValue = 29, DbgExpression = 31, DbgSource = 35,
        DbgFunctionDefinition = 101, DbgSourceContinued = 102, DbgLine = 103, DbgNoLine = 104, DbgEntryPoint = 107, DbgTypeMatrix = 108;

    private const uint OpStringCode = 7, OpSourceContinuedCode = 2, OpLineCode = 8, OpNoLineCode = 317;
    private const uint FlagPublic = 3, FlagLocal = 4, FlagOptimized = 1u << 13, FlagUnknownLayout = 1u << 17;

    // Operands and strings never exceed this many UTF-16 units per instruction, which keeps every literal far below the 65535-word limit.
    private const int SourceChunkChars = 60000;

    private sealed record SourceIds(uint FileString, uint DebugSource);

    private bool _dbgLines;
    private bool _dbgFull;
    private uint _dbgImport;
    private uint _dbgVoid;
    private uint _dbgCu;
    private uint _dbgExpression;
    private uint _dbgMainSource;
    private readonly Dictionary<string, uint> _dbgStrings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourceIds> _dbgSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _dbgTexts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int[]> _dbgLineStarts = new(StringComparer.Ordinal);
    private readonly Dictionary<HlslType, uint> _dbgTypes = [];
    private readonly Dictionary<SourceFunction, uint> _dbgFunctions = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(string Path, int Offset), uint> _dbgBlocks = [];
    private readonly Dictionary<(string Path, int Offset), (SourceFunction? Function, uint Scope)> _dbgLocated = [];
    private readonly Dictionary<InlineSite, uint> _dbgSites = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrFunction, SourceFunction> _dbgOwners = new(ReferenceEqualityComparer.Instance);
    private SourceFunction? _dbgOwner;
    private bool _dbgPrologueDone;
    private bool _dbgScopeActive;
    private uint _dbgCurScope;
    private uint _dbgCurInline;
    private bool _dbgLineActive;
    private (string Path, int Line, int Column, int Length) _dbgCurLine;
    private int _dbgTypeDepth;
    private uint _dbgNoneId;

    private bool DebugOn => _dbgLines || _dbgFull;

    private string DebugFile(string path) => DebugPaths.Normalize(path, _o.DebugSourceRoot);

    // ---- setup -------------------------------------------------------------------------------------

    private void InitDebug()
    {
        _dbgFull = _o.DebugInfo == DebugInfoLevel.Full;
        _dbgLines = _o.DebugInfo == DebugInfoLevel.Lines;
        if (!DebugOn)
        {
            return;
        }

        foreach (var t in _m.Sources)
        {
            _dbgTexts[t.Path] = t.Text;
        }

        if (_dbgFull)
        {
            _s.Extensions.Add("SPV_KHR_non_semantic_info");
            _dbgImport = _s.NewId();
            SpirvModule.PutWithString(_s.ExtImports, Op.ExtInstImport, [_dbgImport], "NonSemantic.Shader.DebugInfo.100", []);
            _dbgVoid = _s.DeclType("void", Op.TypeVoid);
        }

        foreach (var t in _m.Sources)
        {
            AddSource(t.Path, t.Text);
        }

        if (_dbgFull)
        {
            string main = _m.Sources.Count > 0 ? _m.Sources[0].Path : "";
            _dbgMainSource = SourceOf(main).DebugSource;
            // Version 1 of the debug-info format, DWARF 4, language HLSL (5): what DXC writes.
            _dbgCu = Dbg(DbgCompilationUnit, UIntConst(1), UIntConst(4), _dbgMainSource, UIntConst(5));
            _dbgExpression = Dbg(DbgExpression);
        }
    }

    private uint Str(string text)
    {
        if (_dbgStrings.TryGetValue(text, out uint known))
        {
            return known;
        }

        uint id = _s.NewId();
        SpirvModule.PutWithString(_s.DebugStrings, OpStringCode, [id], text, []);
        _dbgStrings[text] = id;
        return id;
    }

    private static List<string> Chunks(string text)
    {
        var parts = new List<string>();
        int at = 0;
        do
        {
            int len = Math.Min(SourceChunkChars, text.Length - at);
            if (at + len < text.Length && char.IsHighSurrogate(text[at + len - 1]))
            {
                len--;
            }

            parts.Add(text.Substring(at, len).Replace('\0', ' '));
            at += len;
        }
        while (at < text.Length);
        return parts;
    }

    private SourceIds AddSource(string path, string? text)
    {
        string file = DebugFile(path);
        uint fileStr = Str(file);
        uint debugSource = 0;
        if (_dbgLines && text is not null)
        {
            var parts = Chunks(text);
            SpirvModule.PutWithString(_s.DebugStrings, Op.Source, [5, 600, fileStr], parts[0], []);
            for (int k = 1; k < parts.Count; k++)
            {
                SpirvModule.PutWithString(_s.DebugStrings, OpSourceContinuedCode, [], parts[k], []);
            }
        }
        else if (_dbgFull)
        {
            if (text is null)
            {
                debugSource = Dbg(DbgSource, fileStr);
            }
            else
            {
                var parts = Chunks(text);
                debugSource = Dbg(DbgSource, fileStr, Str(parts[0]));
                for (int k = 1; k < parts.Count; k++)
                {
                    Dbg(DbgSourceContinued, Str(parts[k]));
                }
            }
        }

        var ids = new SourceIds(fileStr, debugSource);
        _dbgSources[path] = ids;
        return ids;
    }

    /// <summary>The ids of a file's strings and DebugSource; a file the compiler never read (for example one named by <c>#line</c>) is described without text.</summary>
    private SourceIds SourceOf(string path)
    {
        if (_dbgSources.TryGetValue(path, out var known))
        {
            return known;
        }

        return AddSource(path, null);
    }

    // ---- record helpers ----------------------------------------------------------------------------

    /// <summary>Appends a global-section record of the debug instruction set and returns its id.</summary>
    private uint Dbg(uint inst, params uint[] operands)
    {
        uint id = _s.NewId();
        var all = new uint[operands.Length + 4];
        all[0] = _dbgVoid;
        all[1] = id;
        all[2] = _dbgImport;
        all[3] = inst;
        operands.CopyTo(all, 4);
        SpirvModule.Put(_s.DebugGlobals, Op.ExtInst, all);
        return id;
    }

    /// <summary>Appends a record inside the current block.</summary>
    private uint DbgCode(uint inst, params uint[] operands)
    {
        uint id = _s.NewId();
        var all = new uint[operands.Length + 4];
        all[0] = _dbgVoid;
        all[1] = id;
        all[2] = _dbgImport;
        all[3] = inst;
        operands.CopyTo(all, 4);
        SpirvModule.Put(_s.Code, Op.ExtInst, all);
        return id;
    }

    private uint Num(int v) => UIntConst((uint)Math.Max(v, 0));

    // ---- types -------------------------------------------------------------------------------------

    private SourceSpan OriginOf(SourceSpan s) => s.IsKnown ? s : new SourceSpan(_m.Sources.Count > 0 ? _m.Sources[0].Path : "", 0, 0);

    private uint DbgNoneId()
    {
        if (_dbgNoneId == 0)
        {
            _dbgNoneId = Dbg(DbgNone);
        }

        return _dbgNoneId;
    }

    private uint DbgType(HlslType t)
    {
        if (_dbgTypes.TryGetValue(t, out uint known))
        {
            return known;
        }

        if (_dbgTypeDepth > MaxDepth)
        {
            return DbgNoneId();
        }

        _dbgTypeDepth++;
        uint id = MakeDbgType(t);
        _dbgTypeDepth--;
        _dbgTypes[t] = id;
        return id;
    }

    private uint MakeDbgType(HlslType t)
    {
        switch (t)
        {
            case ScalarType s:
            {
                uint encoding = s.ScalarKind == ScalarKind.Bool ? 2u : s.IsFloatingPoint ? 3u : s.IsSignedInteger ? 4u : 6u;
                int bits = s.ScalarKind == ScalarKind.Bool ? 32 : BitWidth(s);
                return Dbg(DbgTypeBasic, Str(s.ToString()), Num(bits), UIntConst(encoding), UIntConst(0));
            }

            case VectorType v:
            {
                uint e = DbgType(v.Element);
                return v.Count == 1 ? e : Dbg(DbgTypeVector, e, Num(v.Count));
            }

            case MatrixType m:
            {
                uint e = DbgType(m.Element);
                if (m.Rows == 1 || m.Columns == 1)
                {
                    int n = m.Rows * m.Columns;
                    return n == 1 ? e : Dbg(DbgTypeVector, e, Num(n));
                }

                // As in the SPIR-V type: one column per HLSL row.
                uint col = Dbg(DbgTypeVector, e, Num(m.Columns));
                return Dbg(DbgTypeMatrix, col, Num(m.Rows), ConstId(Constant.Bool(true)));
            }

            case ArrayType a:
                return Dbg(DbgTypeArray, DbgType(a.Element), Num(a.Length));
            case StructType st:
                return MakeDbgStruct(st);
            case EnumType en:
                return DbgType(en.Underlying);
            default:
            {
                // Resources, samplers and pointers have no layout the debugger could show: an opaque composite named after the type.
                var origin = SourceOf(OriginOf(default).Path);
                return Dbg(DbgTypeComposite, Str(t.ToString()), UIntConst(1), origin.DebugSource, UIntConst(0), UIntConst(0), _dbgCu, Str(t.ToString()), DbgNoneId(), UIntConst(FlagPublic));
            }
        }
    }

    private uint MakeDbgStruct(StructType st)
    {
        var at = OriginOf(st.DeclaredAt);
        uint src = SourceOf(at.Path).DebugSource;
        uint line = Num(at.Line);
        uint col = Num(at.Column);
        uint flags = UIntConst(FlagPublic | FlagUnknownLayout);
        var members = new List<uint>();
        long offset = 0;
        long maxAlign = 8;
        foreach (var f in st.Fields)
        {
            var (size, align) = NaturalBits(f.Type, 0);
            offset = (offset + align - 1) / align * align;
            maxAlign = Math.Max(maxAlign, align);
            members.Add(Dbg(DbgTypeMember, Str(f.Name), DbgType(f.Type), src, line, col, Num((int)Math.Min(offset, int.MaxValue)), Num((int)Math.Min(size, int.MaxValue)), flags));
            offset += size;
        }

        long total = (offset + maxAlign - 1) / maxAlign * maxAlign;
        var ops = new List<uint> { Str(st.Name), UIntConst(1), src, line, col, _dbgCu, Str(st.Name), Num((int)Math.Min(total, int.MaxValue)), flags };
        ops.AddRange(members);
        return Dbg(DbgTypeComposite, ops.ToArray());
    }

    /// <summary>Size and alignment in bits when members are packed naturally; debug records only, not a buffer layout.</summary>
    private (long Size, long Align) NaturalBits(HlslType t, int depth)
    {
        if (depth > MaxDepth)
        {
            return (0, 8);
        }

        switch (t)
        {
            case ScalarType s:
            {
                long bits = s.ScalarKind == ScalarKind.Bool ? 32 : BitWidth(s);
                return (bits, bits);
            }

            case VectorType v:
            {
                var (e, a) = NaturalBits(v.Element, depth + 1);
                return (e * v.Count, a);
            }

            case MatrixType m:
            {
                var (e, a) = NaturalBits(m.Element, depth + 1);
                return (e * m.Rows * m.Columns, a);
            }

            case ArrayType arr:
            {
                var (e, a) = NaturalBits(arr.Element, depth + 1);
                return (e * arr.Length, a);
            }

            case StructType st:
            {
                long off = 0;
                long maxAlign = 8;
                foreach (var f in st.Fields)
                {
                    var (s2, a2) = NaturalBits(f.Type, depth + 1);
                    off = (off + a2 - 1) / a2 * a2 + s2;
                    maxAlign = Math.Max(maxAlign, a2);
                }

                return ((off + maxAlign - 1) / maxAlign * maxAlign, maxAlign);
            }

            default:
                return (0, 8);
        }
    }

    // ---- functions, blocks, inlining ---------------------------------------------------------------

    private SourceFunction OwnerOf(IrFunction f)
    {
        if (f.Source is { } known)
        {
            return known;
        }

        if (!_dbgOwners.TryGetValue(f, out var made))
        {
            made = new SourceFunction(f.Name, f.Span, f.Span, f.ReturnType, []);
            _dbgOwners[f] = made;
        }

        return made;
    }

    private uint FunctionRecord(SourceFunction sf)
    {
        if (_dbgFunctions.TryGetValue(sf, out uint known))
        {
            return known;
        }

        var at = OriginOf(sf.NameSpan);
        var parts = new List<uint> { UIntConst(0), sf.ReturnType.IsVoid ? _dbgVoid : DbgType(sf.ReturnType) };
        foreach (var p in sf.Parameters)
        {
            parts.Add(DbgType(p.Type));
        }

        uint type = Dbg(DbgTypeFunction, parts.ToArray());
        uint flags = UIntConst(FlagPublic | (_o.Optimized ? FlagOptimized : 0));
        uint id = Dbg(DbgFunction, Str(sf.Name), type, SourceOf(at.Path).DebugSource, Num(at.Line), Num(at.Column), _dbgCu, Str(sf.Name), flags, Num(at.Line));
        _dbgFunctions[sf] = id;
        return id;
    }

    /// <summary>The source function whose text contains <paramref name="s"/>, and the innermost lexical scope of that function at the position.</summary>
    private (SourceFunction? Function, uint Scope) Locate(SourceSpan s)
    {
        if (!s.IsKnown)
        {
            return (null, 0);
        }

        var key = (s.Path, s.Offset);
        if (_dbgLocated.TryGetValue(key, out var cached))
        {
            return cached;
        }

        SourceFunction? found = null;
        foreach (var sf in _m.SourceFunctions)
        {
            var e = sf.Extent;
            if (string.Equals(e.Path, s.Path, StringComparison.Ordinal) && s.Offset >= e.Offset && s.Offset < e.Offset + Math.Max(e.Length, 1))
            {
                found = sf;
                break;
            }
        }

        var result = (found, found is null ? 0u : LexicalScope(found, s));
        _dbgLocated[key] = result;
        return result;
    }

    private uint LexicalScope(SourceFunction sf, SourceSpan at)
    {
        var chain = new List<SourceSpan>();
        foreach (var b in sf.Blocks)
        {
            if (string.Equals(b.Path, at.Path, StringComparison.Ordinal) && at.Offset >= b.Offset && at.Offset < b.Offset + b.Length)
            {
                chain.Add(b);
            }
        }

        chain.Sort(static (x, y) => x.Offset.CompareTo(y.Offset));
        uint parent = FunctionRecord(sf);
        foreach (var b in chain)
        {
            var key = (b.Path, b.Offset);
            if (!_dbgBlocks.TryGetValue(key, out uint id))
            {
                id = Dbg(DbgLexicalBlock, SourceOf(b.Path).DebugSource, Num(b.Line), Num(b.Column), parent);
                _dbgBlocks[key] = id;
            }

            parent = id;
        }

        return parent;
    }

    /// <summary>The scope and inlined-at record for an instruction (inlinedAt is 0 when the instruction is not inlined code).</summary>
    private (uint Scope, uint InlinedAt) ScopeOf(Instruction i)
    {
        var owner = _dbgOwner!;
        var (sf, scope) = Locate(i.Span);
        if (sf is null)
        {
            return (FunctionRecord(owner), 0);
        }

        if (ReferenceEquals(sf, owner))
        {
            return (scope, 0);
        }

        if (i.InlinedAt is null && string.Equals(sf.Extent.Path, owner.Extent.Path, StringComparison.Ordinal) && sf.Extent.Offset == owner.Extent.Offset)
        {
            // Another instance of the same template function: same text, so the function scope will do.
            return (FunctionRecord(owner), 0);
        }

        if (i.InlinedAt is { } site)
        {
            return (scope, SiteId(site));
        }

        // Code that moved here without an inlining record (the entry wrapper copying arguments, say) belongs to the function that holds it.
        return (FunctionRecord(owner), 0);
    }

    private uint SiteId(InlineSite leaf)
    {
        var chain = new List<InlineSite>();
        for (var s = leaf; s is not null && !_dbgSites.ContainsKey(s); s = s.Outer)
        {
            chain.Add(s);
        }

        for (int k = chain.Count - 1; k >= 0; k--)
        {
            var s = chain[k];
            var (sf, scope) = Locate(s.Call);
            uint where = sf is not null && (s.Outer is not null || ReferenceEquals(sf, _dbgOwner)) ? scope : FunctionRecord(_dbgOwner!);
            uint id = s.Outer is { } outer
                ? Dbg(DbgInlinedAt, Num(s.Call.Line), where, _dbgSites[outer])
                : Dbg(DbgInlinedAt, Num(s.Call.Line), where);
            _dbgSites[s] = id;
        }

        return _dbgSites[leaf];
    }

    // ---- per-function and per-instruction output ----------------------------------------------------

    private void DebugBeginFunction(IrFunction f)
    {
        _dbgOwner = DebugOn ? OwnerOf(f) : null;
        _dbgPrologueDone = false;
    }

    private void DebugBeginBlock()
    {
        _dbgScopeActive = false;
        _dbgLineActive = false;
    }

    /// <summary>Emits the scope and line records an instruction needs; called before the instruction (and before a block's merge instruction).</summary>
    private void DebugBefore(Instruction i, bool inEntryBlock)
    {
        if (!DebugOn || i.Op is Opcode.Alloca or Opcode.Phi)
        {
            return;
        }

        if (_dbgFull)
        {
            if (inEntryBlock && !_dbgPrologueDone)
            {
                _dbgPrologueDone = true;
                DebugPrologue();
            }

            var (scope, inlined) = ScopeOf(i);
            SetScope(scope, inlined);
        }

        EmitLine(i.Span);
    }

    private void SetScope(uint scope, uint inlined)
    {
        if (_dbgScopeActive && _dbgCurScope == scope && _dbgCurInline == inlined)
        {
            return;
        }

        if (inlined == 0)
        {
            DbgCode(DbgScope, scope);
        }
        else
        {
            DbgCode(DbgScope, scope, inlined);
        }

        _dbgScopeActive = true;
        _dbgCurScope = scope;
        _dbgCurInline = inlined;
    }

    private void DebugPrologue()
    {
        var owner = _dbgOwner!;
        var fnRecord = FunctionRecord(owner);
        DbgCode(DbgFunctionDefinition, fnRecord, _fnIds[_fn]);

        // Parameters that still arrive as values (the optimizer promoted their stack slots): DebugValue at function entry.
        var declared = new HashSet<string>(StringComparer.Ordinal);
        if (_fn.Entry is { } entry)
        {
            foreach (var a in entry.Instructions)
            {
                if (a.Op == Opcode.Alloca && a.DebugVariable is { ArgumentNumber: > 0 } pv && ReferenceEquals(Locate(a.Span).Function, owner))
                {
                    declared.Add(pv.Name);
                }
            }
        }

        for (int k = 0; k < _fn.Parameters.Count && owner.Parameters.Count > 0; k++)
        {
            var p = _fn.Parameters[k];
            if (p.Type is PointerType || string.IsNullOrEmpty(p.Name) || declared.Contains(p.Name))
            {
                continue;
            }

            int at = -1;
            for (int q = 0; q < owner.Parameters.Count; q++)
            {
                if (owner.Parameters[q].Name == p.Name)
                {
                    at = q;
                    break;
                }
            }

            if (at < 0)
            {
                continue;
            }

            var sp = owner.Parameters[at];
            var loc = OriginOf(sp.Span);
            uint v = Dbg(DbgLocalVariable, Str(sp.Name), DbgType(sp.Type), SourceOf(loc.Path).DebugSource, Num(loc.Line), Num(loc.Column), fnRecord, UIntConst(FlagLocal), Num(at + 1));
            SetScope(fnRecord, 0);
            EmitLine(loc);
            DbgCode(DbgValue, v, Id(p), _dbgExpression);
        }

        if (_fn.Entry is { } e2)
        {
            foreach (var a in e2.Instructions)
            {
                if (a.Op != Opcode.Alloca || a.DebugVariable is not { } dv || string.IsNullOrEmpty(dv.Name))
                {
                    continue;
                }

                var (scope, inlined) = ScopeOf(a);
                var loc = OriginOf(dv.Span);
                var ops = new List<uint>
                {
                    Str(dv.Name), DbgType(dv.Type), SourceOf(loc.Path).DebugSource, Num(loc.Line), Num(loc.Column), scope, UIntConst(FlagLocal),
                };
                if (dv.ArgumentNumber > 0)
                {
                    ops.Add(Num(dv.ArgumentNumber));
                }

                uint v = Dbg(DbgLocalVariable, ops.ToArray());
                SetScope(scope, inlined);
                EmitLine(dv.Span);
                DbgCode(DbgDeclare, v, Id(a), _dbgExpression);
            }
        }
    }

    private void EmitLine(SourceSpan s)
    {
        if (!s.IsKnown)
        {
            if (_dbgLineActive)
            {
                if (_dbgFull)
                {
                    DbgCode(DbgNoLine);
                }
                else
                {
                    SpirvModule.Put(_s.Code, OpNoLineCode);
                }

                _dbgLineActive = false;
            }

            return;
        }

        var key = (s.Path, s.Line, s.Column, s.Length);
        if (_dbgLineActive && _dbgCurLine == key)
        {
            return;
        }

        _dbgCurLine = key;
        _dbgLineActive = true;
        if (_dbgLines)
        {
            SpirvModule.Put(_s.Code, OpLineCode, SourceOf(s.Path).FileString, (uint)s.Line, (uint)Math.Max(s.Column, 0));
            return;
        }

        // The record covers the first line of the span only: debuggers highlight one line, and a statement that opens a block would otherwise cover the whole block.
        int endColumn = LineEndColumn(s);
        DbgCode(DbgLine, SourceOf(s.Path).DebugSource, Num(s.Line), Num(s.Line), Num(s.Column), Num(endColumn));
    }

    /// <summary>Column just after the span on its first line (the span is cut at the end of that line); the start column when the text is unknown.</summary>
    private int LineEndColumn(SourceSpan s)
    {
        int start = Math.Max(s.Column, 1);
        if (s.Length <= 0 || !_dbgTexts.TryGetValue(s.Path, out string? text))
        {
            return start;
        }

        int end = Math.Min(start + s.Length, int.MaxValue);
        if (s.Offset < 0 || s.Offset > text.Length)
        {
            return end;
        }

        int stop = Math.Min(text.Length, s.Offset + s.Length);
        for (int k = s.Offset; k < stop; k++)
        {
            if (text[k] is '\n' or '\r')
            {
                return start + (k - s.Offset);
            }
        }

        return end;
    }

    private void DebugGlobalVariables(IEnumerable<GlobalVariable> globals)
    {
        if (!_dbgFull)
        {
            return;
        }

        foreach (var g in globals)
        {
            if (g.GlobalKind is not (GlobalKind.Private or GlobalKind.Workgroup or GlobalKind.Resource) || !g.Span.IsKnown || !_globalIds.TryGetValue(g, out uint id))
            {
                continue;
            }

            // A constant buffer is shown as the struct it holds; other resources as opaque types named after themselves.
            var shown = g.ValueType is ResourceType { Element: StructType element, ResourceKind: ResourceTypeKind.ConstantBuffer } ? element : g.ValueType;
            var at = g.Span;
            Dbg(DbgGlobalVariable, Str(g.Name ?? ""), DbgType(shown), SourceOf(at.Path).DebugSource, Num(at.Line), Num(at.Column), _dbgCu, Str(g.Name ?? ""), id, UIntConst(FlagPublic));
        }
    }

    private void DebugEntry()
    {
        if (!_dbgFull)
        {
            return;
        }

        var owner = OwnerOf(_ep.Function);
        string stage = _ep.Stage switch { ShaderStage.Vertex => "vs", ShaderStage.Pixel => "ps", _ => "cs" };
        string args = "-E " + _ep.Function.Name + " -T " + stage + " -spirv" + (_o.Optimized ? "" : " -Od");
        Dbg(DbgEntryPoint, FunctionRecord(owner), _dbgCu, Str("OneShader"), Str(args));
    }
}
