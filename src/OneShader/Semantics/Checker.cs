using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using OneShader.Diagnostics;
using OneShader.IR;
using OneShader.Reflection;
using OneShader.Syntax;
using OneShader.Types;

namespace OneShader.Semantics;

/// <summary>Settings of the front end (a subset of the public compile options).</summary>
internal sealed class FrontendOptions
{
    public IReadOnlyList<EntryPointRequest> EntryPoints { get; init; } = [];

    public LayoutOptions Layout { get; init; } = LayoutOptions.Default;

    public IReadOnlyList<CompileTarget> Targets { get; init; } = [CompileTarget.SpirV];
}

internal sealed class StructInfo
{
    public required StructDeclaration Decl { get; init; }

    public Dictionary<string, int> FieldIndex { get; } = new(StringComparer.Ordinal);

    /// <summary>Member functions by name (operators as <c>operator+</c>).</summary>
    public Dictionary<string, FunctionGroup> Methods { get; } = new(StringComparer.Ordinal);

    /// <summary>Names of the member functions in declaration order.</summary>
    public List<string> MethodOrder { get; } = [];

    /// <summary>The base struct, or null.</summary>
    public StructType? Base { get; set; }

    /// <summary>Static data members: constants and per-invocation globals named <c>Struct::name</c>.</summary>
    public Dictionary<string, VariableSymbol> Statics { get; } = new(StringComparer.Ordinal);

    /// <summary>Scope that holds the static members so later initializers can refer to earlier ones.</summary>
    public Scope? StaticScope { get; set; }
}

/// <summary>
/// Semantic analysis: resolves names and types, applies HLSL's conversion and overload rules, assigns
/// bindings and checks entry points. Results are stored on the syntax tree (types and <see cref="ExprInfo"/>)
/// and in the lists below for lowering and reflection.
/// </summary>
internal sealed partial class Checker
{
    private DiagnosticBag _diag;
    private readonly FrontendOptions _opt;
    private readonly string _file;
    private readonly CancellationToken _ct;
    private readonly Scope _global = new(null);
    private readonly Dictionary<StructType, StructInfo> _structInfos = [];
    private readonly Dictionary<StructField, SourceSpan> _fieldSpans = [];
    private readonly List<VariableSymbol> _uniformGlobals = [];
    private Scope _scope;
    private MatrixPacking _packing;
    private int _depth;
    private bool _depthReported;
    private FunctionSymbol? _fn;
    private Scope? _fnScope;
    private int _loopDepth;
    private int _switchDepth;

    public Checker(DiagnosticBag diag, FrontendOptions options, string file, CancellationToken ct)
    {
        _diag = diag;
        _opt = options;
        _file = file;
        _ct = ct;
        _scope = _global;
        _packing = options.Layout.DefaultMatrixPacking;
    }

    // ---- results

    public List<ResourceDecl> Resources { get; } = [];

    public List<CBufferSymbol> CBuffers { get; } = [];

    public List<VariableSymbol> PrivateGlobals { get; } = [];

    /// <summary>The <c>[[vk::constant_id]]</c> constants in declaration order.</summary>
    public List<VariableSymbol> SpecConstants { get; } = [];

    public List<VariableSymbol> GroupSharedGlobals { get; } = [];

    public List<FunctionSymbol> Functions { get; } = [];

    public List<EntryInfo> Entries { get; } = [];

    public CBufferSymbol? GlobalsBuffer { get; private set; }

    public LayoutOptions Layout => _opt.Layout;

    // ---- diagnostics

    private void Err(string code, string message, SourceSpan span)
    {
        if (code == SemanticCodes.NotConstant && UsesSpecConstant(span))
        {
            message += " It uses a specialization constant, whose value is only known when the pipeline is created: use a literal or a 'static const' there, or choose the value with a #define.";
        }

        if (!_diag.IsFull)
        {
            _diag.Error(code, message, span);
        }
    }

    // Where specialization constants were named, so a "must be constant" error can say why such a name does not count.
    private readonly List<SourceSpan> _specRefs = [];

    private bool UsesSpecConstant(SourceSpan span)
    {
        foreach (var r in _specRefs)
        {
            if (r.Path == span.Path && r.Offset >= span.Offset && r.Offset < span.Offset + Math.Max(1, span.Length))
            {
                return true;
            }
        }

        return false;
    }

    private void Warn(string code, string message, SourceSpan span) => _diag.Warning(code, message, span);

    private bool Enter(SourceSpan where)
    {
        if (++_depth > Parser.MaxNesting * 2)
        {
            if (!_depthReported)
            {
                _depthReported = true;
                Err(SyntaxCodes.TooDeep, "This code is nested too deeply to analyze; simplify or split it.", where);
            }

            return false;
        }

        return true;
    }

    private void Leave() => _depth--;

    // ---- entry

    public void CheckUnit(CompilationUnit unit)
    {
        foreach (var d in unit.Declarations)
        {
            _ct.ThrowIfCancellationRequested();
            DeclareTopLevel(d, _global);
        }

        FinishGlobals();
        foreach (var f in _allFns)
        {
            if (!f.HasBody && f.Definition is null && f.Called)
            {
                Err(SemanticCodes.BadFunction, "Function '" + f.Name + "' is called but never defined; add a body to its declaration.", f.Span);
            }
        }

        DetectCallCycles();
        FindEntryPoints();
        CheckGroupSyncStages();
        AssignBindings();
    }

    // ---- declarations

    private void DeclareTopLevel(Declaration d, Scope scope)
    {
        if (!Enter(d.Span))
        {
            Leave();
            return;
        }

        try
        {
            switch (d)
            {
                case PragmaDeclaration p:
                    HandlePragma(p);
                    break;
                case StructDeclaration s:
                    DeclareStruct(s, scope);
                    break;
                case EnumDeclaration e:
                    DeclareEnum(e, scope);
                    break;
                case TypedefDeclaration t:
                    DeclareTypedef(t, scope);
                    break;
                case ConstantBufferDeclaration cb:
                    DeclareConstantBuffer(cb, scope);
                    break;
                case NamespaceDeclaration ns:
                    DeclareNamespace(ns, scope);
                    break;
                case VariableDeclaration v:
                    DeclareGlobalVariable(v, scope);
                    break;
                case FunctionDeclaration f:
                    DeclareFunction(f, scope);
                    break;
            }
        }
        finally
        {
            Leave();
        }
    }

    private void HandlePragma(PragmaDeclaration p)
    {
        string t = p.Text.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (t.StartsWith("pack_matrix(", StringComparison.Ordinal))
        {
            if (t.StartsWith("pack_matrix(row_major)", StringComparison.Ordinal))
            {
                _packing = MatrixPacking.RowMajor;
            }
            else if (t.StartsWith("pack_matrix(column_major)", StringComparison.Ordinal))
            {
                _packing = MatrixPacking.ColumnMajor;
            }
            else
            {
                Warn(SemanticCodes.BadAttribute, "pack_matrix expects row_major or column_major; the pragma is ignored.", p.Span);
            }
        }
    }

    private void DeclareNamespace(NamespaceDeclaration ns, Scope scope)
    {
        NamespaceSymbol sym;
        if (scope.LookupLocal(ns.Name.Text) is NamespaceSymbol existing)
        {
            sym = existing;
        }
        else
        {
            sym = new NamespaceSymbol(ns.Name.Text, ns.Name.Span, new Scope(scope));
            Define(scope, sym);
        }

        foreach (var m in ns.Members)
        {
            DeclareTopLevel(m, sym.Scope);
        }
    }

    private bool Define(Scope scope, Symbol sym)
    {
        var prior = scope.LookupLocal(sym.Name);
        if (prior is not null)
        {
            if (sym is FunctionGroup || prior is FunctionGroup)
            {
                // handled by the caller
            }

            Err(SemanticCodes.Redefinition, "'" + sym.Name + "' is already declared in this scope" + (prior.Span.IsKnown ? " at line " + prior.Span.Line.ToString(CultureInfo.InvariantCulture) : "") + "; rename one of them.", sym.Span);
            return false;
        }

        scope.Add(sym);
        return true;
    }

    private void DeclareEnum(EnumDeclaration e, Scope scope)
    {
        var underlying = ScalarType.Int;
        if (e.UnderlyingType is not null && ResolveType(e.UnderlyingType) is ScalarType u && u.IsInteger)
        {
            underlying = u;
        }

        var members = new List<KeyValuePair<string, long>>();
        long next = 0;
        foreach (var m in e.Members)
        {
            if (m.Value is not null)
            {
                var ct = CheckExpr(m.Value);
                var cv = m.Value.Info?.Const;
                if (cv is null || cv.Value.IsFloat)
                {
                    if (!ct.IsError)
                    {
                        Err(SemanticCodes.NotConstant, "The value of enumerator '" + m.Name.Text + "' must be an integer constant expression.", m.Value.Span);
                    }
                }
                else
                {
                    next = cv.Value.Int;
                }
            }

            members.Add(new KeyValuePair<string, long>(m.Name.Text, next));
            next++;
        }

        var et = new EnumType(e.Name?.Text ?? "", underlying, members);
        e.Resolved = et;
        if (e.Name is not null)
        {
            Define(scope, new TypeSymbol(e.Name.Text, e.Name.Span, et));
        }

        if (!e.IsScoped || e.Name is null)
        {
            foreach (var kv in members)
            {
                scope.Add(new EnumeratorSymbol(kv.Key, e.Span, et, kv.Value));
            }
        }
    }

    private void DeclareTypedef(TypedefDeclaration t, Scope scope)
    {
        var baseType = ResolveType(t.Type);
        foreach (var d in t.Declarators)
        {
            var type = ApplyDims(baseType, d.ArrayDimensions, false, d.Span);
            Define(scope, new TypeSymbol(d.Name.Text, d.Name.Span, type));
        }
    }

    // ---- attributes

    private static AttributeSyntax? FindAttribute(IReadOnlyList<AttributeSyntax> attrs, string name)
    {
        foreach (var a in attrs)
        {
            if (a.Name.ToString() == name)
            {
                return a;
            }
        }

        return null;
    }

    private int? IntAttributeArg(AttributeSyntax a, int index)
    {
        if (index >= a.Arguments.Count)
        {
            Err(SemanticCodes.BadAttribute, "The attribute '" + a.Name + "' needs " + (index + 1).ToString(CultureInfo.InvariantCulture) + " argument(s).", a.Span);
            return null;
        }

        var e = a.Arguments[index];
        CheckExpr(e);
        var c = e.Info?.Const;
        if (c is null || c.Value.IsFloat)
        {
            Err(SemanticCodes.NotConstant, "Argument " + (index + 1).ToString(CultureInfo.InvariantCulture) + " of '" + a.Name + "' must be an integer constant.", e.Span);
            return null;
        }

        return (int)c.Value.Int;
    }

    private static string? StringAttributeArg(AttributeSyntax a, int index) =>
        index < a.Arguments.Count && a.Arguments[index] is LiteralExpression { Kind: LiteralKind.String } l ? l.Text : null;

    private static readonly HashSet<string> KnownIgnoredAttributes = new(StringComparer.Ordinal)
    {
        "vk::image_format", "vk::counter_binding", "branch", "flatten", "loop", "unroll", "forcecase", "call", "fastopt",
        "allow_uav_condition", "earlydepthstencil", "domain", "partitioning", "outputtopology", "outputcontrolpoints",
        "patchconstantfunc", "maxvertexcount", "instance", "WaveSize", "noinline", "forceinline", "clipplanes", "RootSignature",
    };

    /// <summary>Warns about attributes that are not understood; the ones the compiler uses are listed in the callers.</summary>
    private void CheckAttributes(IReadOnlyList<AttributeSyntax> attrs, params string[] used)
    {
        foreach (var a in attrs)
        {
            string n = a.Name.ToString();
            if (Array.IndexOf(used, n) >= 0 || KnownIgnoredAttributes.Contains(n))
            {
                continue;
            }

            if (n is "vk::push_constant" or "vk::builtin" or "vk::input_attachment_index")
            {
                Err(SemanticCodes.Unsupported, "The attribute '" + n + "' is not supported yet.", a.Span);
                continue;
            }

            Warn(SemanticCodes.BadAttribute, "The attribute '" + n + "' is not recognized and is ignored.", a.Span);
        }
    }

    // ---- structs

    private HlslType DeclareStruct(StructDeclaration s, Scope scope) => DeclareStructCore(s, scope, null, true);

    /// <summary>
    /// Declares a struct. A template instance passes its mangled name and <paramref name="define"/> false: it is reached
    /// through the template, not by name, and sees its own name as the instance.
    /// </summary>
    private HlslType DeclareStructCore(StructDeclaration s, Scope scope, string? nameOverride, bool define, StructTemplateSymbol? instanceOf = null)
    {
        string name = nameOverride ?? s.Name?.Text ?? "";
        if (s.TemplateParameters.Count > 0 && instanceOf is null)
        {
            if (s.Name is null || s.Source is null)
            {
                Err(SemanticCodes.Unsupported, "A struct template needs a name.", s.Span);
                return HlslType.Error;
            }

            Define(scope, new StructTemplateSymbol(s.Name.Text, s.Name.Span, s, scope));
            return HlslType.Error;
        }

        var st = new StructType(name) { DeclaredAt = s.Name?.Span ?? s.Span };
        s.Resolved = st;
        var info = new StructInfo { Decl = s };
        _structInfos[st] = info;
        if (instanceOf is not null)
        {
            instanceOf.Instances[name] = st;
            _templateOf[st] = instanceOf;
        }

        if (!define && s.Name is not null)
        {
            scope.Add(new TypeSymbol(s.Name.Text, s.Name.Span, st));
        }

        var fields = new List<StructField>();
        var methods = new List<FunctionDeclaration>();
        if (s.BaseTypes.Count > 1)
        {
            Err(SemanticCodes.Unsupported, "A struct can have only one base struct; HLSL does not allow multiple inheritance. Make the second base a member instead.", s.BaseTypes[1].Span);
        }

        if (s.BaseTypes.Count > 0)
        {
            var bt = ResolveType(s.BaseTypes[0]);
            if (bt is StructType bst)
            {
                // The base is the first member, as DXC lays it out, so a derived struct has the same bytes as one declaring a base member.
                st.BaseStruct = bst;
                info.Base = bst;
                var bf = new StructField(BaseFieldName(bst), bst) { MatrixPacking = ContainsMatrix(bst) ? _packing : null };
                info.FieldIndex[bf.Name] = 0;
                _fieldSpans[bf] = s.BaseTypes[0].Span;
                fields.Add(bf);
            }
            else if (!bt.IsError)
            {
                Err(SemanticCodes.BadType, "A struct can only inherit from another struct, not '" + bt + "'.", s.BaseTypes[0].Span);
            }
        }

        foreach (var m in s.Members)
        {
            switch (m)
            {
                case VariableDeclaration v:
                    DeclareFields(v, fields, info, st.Name, scope);
                    break;
                case FunctionDeclaration f:
                    methods.Add(f);
                    break;
                case TypedefDeclaration t:
                    DeclareTypedef(t, scope);
                    break;
                case StructDeclaration inner:
                    DeclareStruct(inner, scope);
                    break;
                case EnumDeclaration en:
                    DeclareEnum(en, scope);
                    break;
                case PragmaDeclaration p:
                    HandlePragma(p);
                    break;
            }
        }

        st.SetFields(fields);
        if (define && s.Name is not null)
        {
            Define(scope, new TypeSymbol(name, s.Name.Span, st));
        }

        DeclareMethods(st, info, methods, scope);
        return st;
    }

    private static string BaseFieldName(StructType b) => "__base_" + b.Name;

    private void DeclareMethods(StructType st, StructInfo info, List<FunctionDeclaration> decls, Scope scope)
    {
        var syms = new List<FunctionSymbol>();
        foreach (var f in decls)
        {
            string name = f.Name.Last.Text;
            if (f.Name.Parts.Count > 1)
            {
                Err(SemanticCodes.Unsupported, "Define member function '" + name + "' inside the struct.", f.Name.Span);
                continue;
            }

            if (name.StartsWith("operator", StringComparison.Ordinal) && !ValidateOperator(f, name))
            {
                continue;
            }

            if (!info.Methods.TryGetValue(name, out var group))
            {
                group = new FunctionGroup(name, f.Name.Span);
                info.Methods[name] = group;
                info.MethodOrder.Add(name);
            }

            if (f.TemplateParameters.Count > 0)
            {
                if (f.Source is null)
                {
                    Err(SemanticCodes.Unsupported, "This member function template cannot be instantiated: its declaration is incomplete.", f.Name.Span);
                    continue;
                }

                group.Templates.Add(new FunctionTemplate { Decl = f, Scope = scope, Owner = st });
                continue;
            }

            var sym = BuildFunctionSymbol(f, scope, st, null);
            if (AddToGroup(group, sym, f))
            {
                _allFns.Add(sym);
                syms.Add(sym);
            }
        }

        // Bodies after every signature, so member functions can call each other in any order.
        foreach (var sym in syms)
        {
            if (sym.HasBody)
            {
                Functions.Add(sym);
                CheckFunctionBody(sym, scope);
            }
        }
    }

    /// <summary>HLSL 2021 (as DXC implements it) overloads binary arithmetic, bitwise and comparison operators, [] and () as members only.</summary>
    private bool ValidateOperator(FunctionDeclaration f, string name)
    {
        string op = name.Substring("operator".Length);
        int n = f.Parameters.Count;
        if ((f.Modifiers & Modifiers.Static) != 0)
        {
            Err(SemanticCodes.BadOperator, "An operator overload cannot be static; it works on the object to its left.", f.Name.Span);
            return false;
        }

        switch (op)
        {
            case "+" or "-" or "*" or "/" or "%" or "&" or "|" or "^" or "&&" or "||" or "==" or "!=" or "<" or "<=" or ">" or ">=" or "[]":
                if (n == 1)
                {
                    return true;
                }

                Err(SemanticCodes.BadOperator, n == 0
                    ? "Unary operators cannot be overloaded in HLSL 2021; 'operator" + op + "' must take the right operand as its one parameter."
                    : "'operator" + op + "' takes exactly one parameter, the right operand, but this declares " + Num(n) + ".", f.Name.Span);
                return false;
            case "()":
                return true;
            default:
                Err(SemanticCodes.BadOperator, "'operator" + op + "' cannot be overloaded: HLSL 2021 allows only the arithmetic, bitwise (not shifts), logical and comparison operators, [] and (). Overload the plain operator and write the assignment out, for example a = a + b.", f.Name.Span);
                return false;
        }
    }

    private void DeclareFields(VariableDeclaration v, List<StructField> fields, StructInfo info, string structName, Scope? scope = null)
    {
        CheckAttributes(v.Attributes, "vk::offset", "vk::location", "oneshader::format");
        if ((v.Modifiers & Modifiers.Static) != 0)
        {
            if (scope is null)
            {
                Err(SemanticCodes.Unsupported, "Static members are only supported in structs, not in constant buffers; declare the constant outside the cbuffer.", v.Span);
                return;
            }

            DeclareStaticMember(v, info, structName, scope);
            return;
        }

        var baseType = ResolveType(v.Type);
        foreach (var d in v.Declarators)
        {
            var type = ApplyDims(baseType, d.ArrayDimensions, false, d.Span);
            if (!type.IsError && !TypeOps.IsValueType(type))
            {
                Err(SemanticCodes.BadVariable, "The member '" + d.Name.Text + "' has type '" + type + "', which cannot be a struct member; declare resources at global scope.", d.Span);
                type = HlslType.Error;
            }

            string? semantic = null;
            int? offset = null;
            foreach (var a in d.Annotations)
            {
                if (a is SemanticAnnotation sa)
                {
                    semantic = sa.Text;
                }
                else if (a is PackOffsetAnnotation po)
                {
                    offset = (int)po.ByteOffset;
                }
                else if (a is RegisterAnnotation ra)
                {
                    Err(SemanticCodes.BadResource, "register(...) cannot be used on a struct member.", ra.Span);
                }
            }

            var vkOffset = FindAttribute(v.Attributes, "vk::offset");
            if (vkOffset is not null)
            {
                offset = IntAttributeArg(vkOffset, 0) ?? offset;
            }

            int? location = null;
            var vkLoc = FindAttribute(v.Attributes, "vk::location");
            if (vkLoc is not null)
            {
                location = IntAttributeArg(vkLoc, 0);
            }

            string? format = null;
            var fmt = FindAttribute(v.Attributes, "oneshader::format");
            if (fmt is not null)
            {
                format = StringAttributeArg(fmt, 0);
                if (format is null)
                {
                    Err(SemanticCodes.BadAttribute, "oneshader::format needs a string such as \"unorm8x4\".", fmt.Span);
                }
            }

            MatrixPacking? packing = (v.Modifiers & Modifiers.RowMajor) != 0 ? MatrixPacking.RowMajor
                : (v.Modifiers & Modifiers.ColumnMajor) != 0 ? MatrixPacking.ColumnMajor
                : ContainsMatrix(type) ? _packing : null;
            var field = new StructField(d.Name.Text, type)
            {
                Semantic = semantic,
                Interpolation = InterpolationOf(v.Modifiers),
                MatrixPacking = packing,
                ExplicitOffset = offset,
                Location = location,
                VertexFormat = format,
            };
            if (info.FieldIndex.ContainsKey(d.Name.Text))
            {
                Err(SemanticCodes.Redefinition, "The struct '" + structName + "' already has a member named '" + d.Name.Text + "'.", d.Name.Span);
                continue;
            }

            info.FieldIndex[d.Name.Text] = fields.Count;
            _fieldSpans[field] = d.Name.Span;
            fields.Add(field);
            if (d.Initializer is not null)
            {
                Err(SemanticCodes.Unsupported, "Default values for struct members are not supported; assign them where the struct is used.", d.Initializer.Span);
            }
        }
    }

    /// <summary>A static data member: a global of its own (a compile-time constant when const with a value), reached as <c>Struct::name</c> or by its bare name inside the struct's functions.</summary>
    private void DeclareStaticMember(VariableDeclaration v, StructInfo info, string structName, Scope scope)
    {
        var holder = info.StaticScope ??= new Scope(scope);
        var saved = _scope;
        _scope = holder;
        try
        {
            var baseType = ResolveType(v.Type);
            foreach (var d in v.Declarators)
            {
                var type = ApplyDims(baseType, d.ArrayDimensions, d.Initializer is not null, d.Span);
                if (d.Initializer is InitializerListExpression il && type is ArrayType { Length: 0 } ua)
                {
                    type = HlslType.Array(ua.Element, InferListLength(il, ua.Element));
                }

                if (IsResourceLike(type))
                {
                    Err(SemanticCodes.Unsupported, "A static member cannot be a resource; declare '" + d.Name.Text + "' at global scope.", d.Name.Span);
                    continue;
                }

                DeclareOneGlobal(v, d, type, holder);
                if (holder.LookupLocal(d.Name.Text) is VariableSymbol vs)
                {
                    vs.IrName = structName + "." + d.Name.Text;
                    info.Statics[d.Name.Text] = vs;
                }
            }
        }
        finally
        {
            _scope = saved;
        }
    }

    private VariableSymbol? FindStatic(StructType st, string name)
    {
        for (var s = st; s is not null; s = s.BaseStruct)
        {
            if (_structInfos.TryGetValue(s, out var si) && si.Statics.TryGetValue(name, out var vs))
            {
                return vs;
            }
        }

        return null;
    }

    private static bool ContainsMatrix(HlslType t) => t switch
    {
        MatrixType => true,
        ArrayType a => ContainsMatrix(a.Element),
        StructType => true,
        _ => false,
    };

    private static Interpolation InterpolationOf(Modifiers m) =>
        (m & Modifiers.NoInterpolation) != 0 ? Interpolation.Flat
        : (m & Modifiers.NoPerspective) != 0 ? Interpolation.NoPerspective
        : (m & Modifiers.Centroid) != 0 ? Interpolation.Centroid
        : (m & Modifiers.Sample) != 0 ? Interpolation.Sample
        : Interpolation.Default;

    // ---- constant buffers

    private void DeclareConstantBuffer(ConstantBufferDeclaration cb, Scope scope)
    {
        CheckAttributes(cb.Attributes, "vk::binding", "vk::push_constant");
        if (FindAttribute(cb.Attributes, "vk::push_constant") is { } cbPush)
        {
            Err(SemanticCodes.BadPushConstant, "[[vk::push_constant]] cannot be put on a cbuffer. Declare the members as a struct and write '[[vk::push_constant]] ConstantBuffer<" + cb.Name.Text + "Data> " + cb.Name.Text + ";' (or a global of the struct type); it is treated as a plain constant buffer for now.", cbPush.Span);
        }

        if (cb.IsTextureBuffer)
        {
            Err(SemanticCodes.Unsupported, "tbuffer is not supported; use a StructuredBuffer or a cbuffer.", cb.Name.Span);
            return;
        }

        var fields = new List<StructField>();
        var info = new StructInfo { Decl = new StructDeclaration(cb.Span, [], false, cb.Name, [], [], []) };
        var st = new StructType(cb.Name.Text);
        _structInfos[st] = info;
        var sym = new CBufferSymbol(cb.Name.Text, cb.Name.Span, st);
        var memberNames = new List<(VariableSymbol?, StructField)>();
        foreach (var m in cb.Members)
        {
            int before = fields.Count;
            DeclareFields(m, fields, info, cb.Name.Text);
            for (int i = before; i < fields.Count; i++)
            {
                var f = fields[i];
                var vs = new VariableSymbol(f.Name, _fieldSpans[f], VarKind.CBufferMember, f.Type, m.Modifiers | Modifiers.Uniform)
                {
                    Buffer = sym,
                    MemberIndex = i,
                };
                sym.Members.Add(vs);
            }
        }

        _ = memberNames;
        st.SetFields(fields);

        // A member may reuse the buffer's own name (cbuffer Lights { Light Lights[8]; }); the member then wins, because
        // the buffer name is only ever used to explain a mistake.
        bool memberTakesName = false;
        foreach (var vs in sym.Members)
        {
            memberTakesName |= vs.Name == sym.Name;
        }

        if (!memberTakesName && !Define(scope, sym))
        {
            return;
        }

        foreach (var vs in sym.Members)
        {
            if (scope.LookupLocal(vs.Name) is not null)
            {
                Err(SemanticCodes.Redefinition, "'" + vs.Name + "' is already declared in this scope; constant buffer members share the global namespace.", vs.Span);
                continue;
            }

            scope.Add(vs);
        }

        var rt = new ResourceType(ResourceTypeKind.ConstantBuffer, st);
        var res = new ResourceDecl
        {
            Name = cb.Name.Text,
            Span = cb.Name.Span,
            Type = rt,
            Kind = ResourceKind.ConstantBuffer,
            ResourceType = ResourceTypeKind.ConstantBuffer,
            Buffer = sym,
        };
        ApplyBindingAnnotations(res, cb.Attributes, cb.Annotations, 'b');
        sym.Resource = res;
        ComputeBufferLayout(res, st, BufferLayoutRules.ConstantBuffer, cb.Name.Span);
        CBuffers.Add(sym);
        Resources.Add(res);
    }

    private void ComputeBufferLayout(ResourceDecl res, HlslType elementType, BufferLayoutRules rules, SourceSpan where)
    {
        if (elementType.IsError)
        {
            return;
        }

        if (!BufferLayout.TryCompute(elementType, rules, _opt.Layout, out var layout, out string? error))
        {
            Err(SemanticCodes.BadLayout, "'" + res.Name + "' cannot be laid out in a buffer: " + error + ".", where);
            return;
        }

        // An element of a structured buffer is strided by its size rounded up to its alignment, so a bare float3
        // takes 16 bytes (a struct already carries that padding). Buffer<float3> texels are not padded.
        if (rules == BufferLayoutRules.StorageBuffer && layout!.Alignment > 1 && layout.Size % layout.Alignment != 0)
        {
            layout = layout.WithSize((layout.Size + layout.Alignment - 1) / layout.Alignment * layout.Alignment);
        }

        res.Layout = layout;
        CheckMemberOverlap(res, layout!, where);
    }

    /// <summary>Reports members whose explicit offsets (packoffset or [[vk::offset]]) make them share bytes; automatic placement cannot overlap.</summary>
    private void CheckMemberOverlap(ResourceDecl res, TypeLayout layout, SourceSpan where)
    {
        if (layout.Type is not StructType || layout.Members.Count < 2)
        {
            return;
        }

        bool explicitOffsets = false;
        foreach (var m in layout.Members)
        {
            explicitOffsets |= m.Field.ExplicitOffset is not null;
        }

        if (!explicitOffsets)
        {
            return;
        }

        // Sweep the members in offset order, remembering the one that reaches furthest; a member that starts before
        // that point overlaps it. The later-declared of the two is the one reported, as DXC does. Sizes include the
        // padding after arrays and matrices (an array of float takes whole registers), which is what the module's
        // ArrayStride and MatrixStride decorations make of it and what DXC counts.
        var order = new List<int>(layout.Members.Count);
        for (int i = 0; i < layout.Members.Count; i++)
        {
            int at = order.Count;
            while (at > 0 && layout.Members[order[at - 1]].Offset > layout.Members[i].Offset)
            {
                at--;
            }

            order.Insert(at, i);
        }

        int reach = -1;
        int reachOwner = -1;
        foreach (int i in order)
        {
            var m = layout.Members[i];
            int used = m.Layout.Size;
            if (reachOwner >= 0 && m.Offset < reach && used > 0)
            {
                int a = Math.Max(i, reachOwner);
                int b = Math.Min(i, reachOwner);
                var later = layout.Members[a];
                var earlier = layout.Members[b];
                var span = _fieldSpans.TryGetValue(later.Field, out var fs) ? fs : where;
                Err(SemanticCodes.BadLayout, "packoffset overlap between '" + later.Name + "', '" + earlier.Name + "': '" + later.Name + "' occupies bytes "
                    + Num(later.Offset) + " to " + Num(later.Offset + later.Layout.Size - 1) + " of '" + res.Name + "', which '" + earlier.Name + "' already uses (bytes "
                    + Num(earlier.Offset) + " to " + Num(earlier.Offset + earlier.Layout.Size - 1) + "). Move '" + later.Name + "' to a register or component that '" + earlier.Name + "' does not use.", span);
            }

            if (m.Offset + used > reach)
            {
                reach = m.Offset + used;
                reachOwner = i;
            }
        }
    }

    /// <summary>Largest binding, set or register number accepted; far above any device limit, and small enough that a backend can add to it without overflow.</summary>
    private const int MaxBindingNumber = 65535;

    private void ApplyBindingAnnotations(ResourceDecl res, IReadOnlyList<AttributeSyntax> attrs, IReadOnlyList<Annotation> annotations, char expectedClass)
    {
        var counter = FindAttribute(attrs, "vk::counter_binding");
        if (counter is not null)
        {
            int? cb = IntAttributeArg(counter, 0);
            if (cb is null or < 0 or > MaxBindingNumber)
            {
                Err(SemanticCodes.BadAttribute, "vk::counter_binding needs a non-negative binding number, for example [[vk::counter_binding(3)]].", counter.Span);
            }
            else
            {
                res.ExplicitCounterBinding = cb;
            }
        }

        var vk = FindAttribute(attrs, "vk::binding");
        if (vk is not null)
        {
            int? b = IntAttributeArg(vk, 0);
            int set = vk.Arguments.Count > 1 ? IntAttributeArg(vk, 1) ?? 0 : 0;
            if (b is not null)
            {
                res.ExplicitBinding = b;
                res.ExplicitSet = set;
                res.ExplicitFromVk = true;
                if (b < 0 || set < 0 || b > MaxBindingNumber || set > MaxBindingNumber)
                {
                    Err(SemanticCodes.BadResource, "vk::binding values must be between 0 and " + Num(MaxBindingNumber) + ".", vk.Span);
                }
            }

            return;
        }

        foreach (var a in annotations)
        {
            if (a is RegisterAnnotation r)
            {
                if (r.RegisterClass is not ('b' or 't' or 's' or 'u'))
                {
                    Err(SemanticCodes.BadResource, "The register class '" + r.RegisterClass + "' cannot bind a resource; use b (constant buffer), t (texture or read-only buffer), s (sampler) or u (read-write resource).", r.Span);
                    continue;
                }

                _ = expectedClass;
                if (r.Index > MaxBindingNumber || r.Space > MaxBindingNumber)
                {
                    Err(SemanticCodes.BadResource, "Register numbers and spaces must be at most " + Num(MaxBindingNumber) + ".", r.Span);
                    continue;
                }

                res.ExplicitBinding = (int)r.Index;
                res.ExplicitSet = (int)r.Space;
            }
        }
    }

    // ---- global variables

    private void DeclareGlobalVariable(VariableDeclaration v, Scope scope)
    {
        CheckAttributes(v.Attributes, "vk::binding", "vk::counter_binding", "vk::offset", "vk::push_constant", "vk::constant_id");
        var baseType = ResolveType(v.Type);
        foreach (var d in v.Declarators)
        {
            bool unsizedOk = d.Initializer is not null || IsResourceLike(baseType);
            var type = ApplyDims(baseType, d.ArrayDimensions, unsizedOk, d.Span);
            if (d.Initializer is InitializerListExpression il && type is ArrayType { Length: 0 } ua)
            {
                type = HlslType.Array(ua.Element, InferListLength(il, ua.Element));
            }

            DeclareOneGlobal(v, d, type, scope);
        }
    }

    private static bool IsResourceLike(HlslType t)
    {
        while (t is ArrayType a)
        {
            t = a.Element;
        }

        return t is ResourceType or SamplerType;
    }

    private void DeclareOneGlobal(VariableDeclaration v, Declarator d, HlslType type, Scope scope)
    {
        var mods = v.Modifiers;
        var name = d.Name;
        d.Type = type;
        if (type.IsError)
        {
            Define(scope, new VariableSymbol(name.Text, name.Span, VarKind.Global, type, mods));
            return;
        }

        if (type.IsVoid)
        {
            Err(SemanticCodes.BadVariable, "A variable cannot have type void.", name.Span);
            return;
        }

        if (FindAttribute(v.Attributes, "vk::push_constant") is { } pushAttr)
        {
            HlslType? block = type is ResourceType { ResourceKind: ResourceTypeKind.ConstantBuffer, Element: StructType } ? type
                : type is StructType ? new ResourceType(ResourceTypeKind.ConstantBuffer, type) : null;
            if (block is null)
            {
                Err(SemanticCodes.BadPushConstant, "[[vk::push_constant]] applies to a global of struct type or ConstantBuffer<T> with a struct T; '" + name.Text + "' has type '" + type + "'. Wrap the values in a struct.", pushAttr.Span);
                return;
            }

            d.Type = block;
            DeclareResourceVariable(v, d, block, scope, true);
            return;
        }

        if (FindAttribute(v.Attributes, "vk::constant_id") is { } specAttr)
        {
            DeclareSpecConstant(v, d, type, scope, specAttr);
            return;
        }

        if (IsResourceLike(type))
        {
            DeclareResourceVariable(v, d, type, scope);
            return;
        }

        if ((mods & Modifiers.GroupShared) != 0)
        {
            var gs = new VariableSymbol(name.Text, name.Span, VarKind.GroupShared, type, mods) { Declarator = d };
            if (d.Initializer is not null)
            {
                Err(SemanticCodes.BadVariable, "A groupshared variable cannot have an initializer; assign it in the shader instead.", d.Initializer.Span);
            }

            if (Define(scope, gs))
            {
                GroupSharedGlobals.Add(gs);
            }

            return;
        }

        bool isStatic = (mods & Modifiers.Static) != 0;
        bool isConst = (mods & Modifiers.Const) != 0;
        bool uniform = !isStatic && ((mods & (Modifiers.Uniform | Modifiers.Extern)) != 0 || !isConst || d.Initializer is null);
        if (!uniform)
        {
            // static variables, and const variables with a value (compile-time constants).
            var sv = new VariableSymbol(name.Text, name.Span, VarKind.Global, type, mods | Modifiers.Static) { Declarator = d, Initializer = d.Initializer };
            if (d.Initializer is not null)
            {
                CheckInitializer(d.Initializer, type, isConst);
                if (isConst && d.Initializer.Info?.Const is { } cv && type is ScalarType st)
                {
                    sv.Const = cv.ConvertTo(st);
                }
            }
            else if (isConst)
            {
                Err(SemanticCodes.BadVariable, "The constant '" + name.Text + "' needs a value: write '= value' after the name.", name.Span);
            }

            if (Define(scope, sv))
            {
                PrivateGlobals.Add(sv);
            }

            return;
        }

        // A plain global is a uniform: it lives in the implicit constants buffer.
        var us = new VariableSymbol(name.Text, name.Span, VarKind.CBufferMember, type, mods | Modifiers.Uniform) { Declarator = d };
        if (!TypeOps.IsValueType(type))
        {
            Err(SemanticCodes.BadVariable, "The global '" + name.Text + "' has type '" + type + "', which cannot be a uniform.", name.Span);
            return;
        }

        if (d.Initializer is not null)
        {
            Warn(SemanticCodes.BadVariable, "The initializer of the uniform '" + name.Text + "' is ignored: its value comes from the constant buffer. Use 'static const' for a compile-time constant.", d.Initializer.Span);
        }

        if (Define(scope, us))
        {
            _uniformGlobals.Add(us);
        }
    }

    /// <summary>
    /// <c>[[vk::constant_id(N)]] const T Name = default;</c>: a scalar the host sets when it creates the pipeline. The same rules as DXC:
    /// bool, int, uint or float, not static, with a default that is a constant expression.
    /// </summary>
    private void DeclareSpecConstant(VariableDeclaration v, Declarator d, HlslType type, Scope scope, AttributeSyntax attr)
    {
        var name = d.Name;
        if (type is not ScalarType { ScalarKind: ScalarKind.Bool or ScalarKind.Int or ScalarKind.UInt or ScalarKind.Float } st)
        {
            Err(SemanticCodes.BadSpecConstant, "[[vk::constant_id]] applies to a scalar bool, int, uint or float; '" + name.Text + "' has type '" + type + "'. Declare one constant per component, or pass the value in a constant buffer.", attr.Span);
            Define(scope, new VariableSymbol(name.Text, name.Span, VarKind.Global, HlslType.Error, v.Modifiers));
            return;
        }

        if ((v.Modifiers & Modifiers.Static) != 0)
        {
            Err(SemanticCodes.BadSpecConstant, "A specialization constant must be visible to the host: remove 'static' from '" + name.Text + "'.", attr.Span);
            Define(scope, new VariableSymbol(name.Text, name.Span, VarKind.Global, HlslType.Error, v.Modifiers));
            return;
        }

        long id = -1;
        if (IntAttributeArg(attr, 0) is { } rawId)
        {
            id = (uint)rawId;
            if (rawId < 0)
            {
                Err(SemanticCodes.BadSpecConstant, "The id of '" + name.Text + "' must not be negative: write [[vk::constant_id(0)]] or a larger number.", attr.Arguments[0].Span);
                id = -1;
            }
        }

        bool ok = id >= 0;
        if (ok && _specIds.TryGetValue((uint)id, out var other))
        {
            Err(SemanticCodes.BadSpecConstant, "constant_id " + id.ToString(CultureInfo.InvariantCulture) + " is already used by '" + other + "'. Give '" + name.Text + "' a different number: every specialization constant needs its own id.", attr.Span);
            ok = false;
        }

        ConstValue? def = null;
        if (d.Initializer is null)
        {
            Err(SemanticCodes.BadSpecConstant, "The specialization constant '" + name.Text + "' needs a default value: write '= value' after the name.", name.Span);
            ok = false;
        }
        else
        {
            CheckInitializer(d.Initializer, type, true);
            if (d.Initializer.Info?.Const is { } cv)
            {
                def = cv.ConvertTo(st);
            }
            else
            {
                if (d.Initializer.Type is { IsError: false })
                {
                    Err(SemanticCodes.BadSpecConstant, "The default of the specialization constant '" + name.Text + "' must be a literal or a constant expression of literals and 'static const' values; it cannot depend on another specialization constant or a variable.", d.Initializer.Span);
                }

                ok = false;
            }
        }

        if (!ok || def is null)
        {
            Define(scope, new VariableSymbol(name.Text, name.Span, VarKind.Global, HlslType.Error, v.Modifiers));
            return;
        }

        _specIds[(uint)id] = name.Text;
        var sym = new VariableSymbol(name.Text, name.Span, VarKind.Spec, type, v.Modifiers | Modifiers.Const) { Declarator = d, SpecId = (uint)id, SpecDefault = def.Value };
        if (Define(scope, sym))
        {
            SpecConstants.Add(sym);
        }
    }

    private readonly Dictionary<uint, string> _specIds = [];

    private void DeclareResourceVariable(VariableDeclaration v, Declarator d, HlslType type, Scope scope, bool push = false)
    {
        var name = d.Name;
        var element = type;
        int count = 1;
        if (type is ArrayType at)
        {
            if (at.Element is ArrayType)
            {
                Err(SemanticCodes.BadResource, "Multi-dimensional resource arrays are not supported; flatten '" + name.Text + "' to one dimension.", name.Span);
                return;
            }

            if (at.Length == 0)
            {
                Err(SemanticCodes.Unsupported, "'" + name.Text + "' is an unbounded resource array, which is not supported. Give it a size, for example '" + name.Text + "[16]'.", name.Span);
                Define(scope, new VariableSymbol(name.Text, name.Span, VarKind.Global, HlslType.Error, v.Modifiers));
                return;
            }

            element = at.Element;
            count = at.Length;
        }

        if ((v.Modifiers & Modifiers.Static) != 0)
        {
            Err(SemanticCodes.Unsupported, "Static resource variables are not supported; declare '" + name.Text + "' without 'static' and bind it.", name.Span);
            return;
        }

        var rd = new ResourceDecl
        {
            Name = name.Text,
            Span = name.Span,
            Type = type,
            Count = count,
            Kind = KindOf(element),
            ResourceType = (element as ResourceType)?.ResourceKind ?? ResourceTypeKind.Texture2D,
            HlslTypeText = element.ToString(),
        };
        rd = CopyWithVariable(rd, name.Text, type, v.Modifiers, scope, out var vs);
        if (vs is null)
        {
            return;
        }

        if (push)
        {
            rd.IsPushConstant = true;
        }
        else
        {
            ApplyBindingAnnotations(rd, v.Attributes, d.Annotations, '\0');
        }

        ApplyImageFormat(rd, v.Attributes, element);
        if (element is ResourceType rt)
        {
            if (rt.IsStorageBuffer && rt.ResourceKind != ResourceTypeKind.ByteAddressBuffer && rt.ResourceKind != ResourceTypeKind.RWByteAddressBuffer)
            {
                if (rt.Element is null || rt.Element.IsError)
                {
                    Err(SemanticCodes.BadType, "'" + rt.ResourceKind + "' needs an element type, for example '" + rt.ResourceKind + "<float4>'.", name.Span);
                }
                else
                {
                    ComputeBufferLayout(rd, rt.Element, BufferLayoutRules.StorageBuffer, name.Span);
                }
            }
            else if (rt.ResourceKind == ResourceTypeKind.ConstantBuffer)
            {
                if (rt.Element is StructType)
                {
                    // DXC lays a push-constant block out like a structured-buffer element (vector-relaxed std430), not like a cbuffer.
                    ComputeBufferLayout(rd, rt.Element, push ? BufferLayoutRules.StorageBuffer : BufferLayoutRules.ConstantBuffer, name.Span);
                    if (push && rd.Layout is { } pl && pl.Size > 128)
                    {
                        Warn(SemanticCodes.PushConstantSize, "The push-constant block '" + name.Text + "' is " + Num((int)pl.Size) + " bytes; Vulkan only guarantees maxPushConstantsSize of 128 bytes. Shrink it or move the rest into a constant buffer.", name.Span);
                    }
                }
                else
                {
                    Err(SemanticCodes.BadType, "ConstantBuffer needs a struct type, for example 'ConstantBuffer<MyData>'.", name.Span);
                }
            }
        }

        if (element is ResourceType { ResourceKind: ResourceTypeKind.AppendStructuredBuffer or ResourceTypeKind.ConsumeStructuredBuffer })
        {
            if (count != 1)
            {
                Err(SemanticCodes.Unsupported, "An array of append or consume buffers is not supported: each buffer needs its own counter. Declare '" + name.Text + "' as separate variables.", name.Span);
                return;
            }

            rd.HasCounter = true;
        }

        Resources.Add(rd);
    }

    private void ApplyImageFormat(ResourceDecl rd, IReadOnlyList<AttributeSyntax> attrs, HlslType element)
    {
        var a = FindAttribute(attrs, "vk::image_format");
        if (a is null)
        {
            return;
        }

        string? name = StringAttributeArg(a, 0);
        int format = name is null ? 0 : ImageFormats.Find(name);
        if (format == 0)
        {
            Err(SemanticCodes.BadAttribute, "vk::image_format needs a format name in quotes such as \"rgba8\", \"rgba16f\", \"r32f\" or \"r32ui\".", a.Span);
            return;
        }

        if (element is not ResourceType { IsTexture: true, IsReadWrite: true } rt)
        {
            Warn(SemanticCodes.BadAttribute, "vk::image_format only applies to read-write textures (RWTexture1D/2D/3D); it is ignored here.", a.Span);
            return;
        }

        char cls = ImageFormats.Class(format);
        var sc = rt.Element?.ScalarElement;
        bool matches = sc is null || (cls == 'f' ? sc.IsFloatingPoint : cls == 'i' ? sc == ScalarType.Int : sc == ScalarType.UInt);
        if (!matches)
        {
            Err(SemanticCodes.BadAttribute, "The format \"" + name + "\" holds " + (cls == 'f' ? "float" : cls == 'i' ? "int" : "uint") + " texels but the texture's element type is '" + rt.Element + "'. Change the element type or pick a matching format.", a.Span);
            return;
        }

        rd.ImageFormat = name;
    }

    private ResourceDecl CopyWithVariable(ResourceDecl rd, string name, HlslType type, Modifiers mods, Scope scope, out VariableSymbol? vs)
    {
        vs = new VariableSymbol(name, rd.Span, VarKind.Resource, type, mods);
        var full = new ResourceDecl
        {
            Name = rd.Name,
            Span = rd.Span,
            Type = rd.Type,
            Count = rd.Count,
            Kind = rd.Kind,
            ResourceType = rd.ResourceType,
            HlslTypeText = rd.HlslTypeText,
            Variable = vs,
            IsPushConstant = rd.IsPushConstant,
        };
        vs.Resource = full;
        if (!Define(scope, vs))
        {
            vs = null;
        }

        return full;
    }

    private static ResourceKind KindOf(HlslType t)
    {
        if (t is SamplerType)
        {
            return ResourceKind.Sampler;
        }

        if (t is not ResourceType r)
        {
            return ResourceKind.ConstantBuffer;
        }

        return r.ResourceKind switch
        {
            ResourceTypeKind.ConstantBuffer => ResourceKind.ConstantBuffer,
            ResourceTypeKind.RWTexture1D or ResourceTypeKind.RWTexture1DArray or ResourceTypeKind.RWTexture2D
                or ResourceTypeKind.RWTexture2DArray or ResourceTypeKind.RWTexture3D => ResourceKind.StorageTexture,
            ResourceTypeKind.Buffer => ResourceKind.UniformTexelBuffer,
            ResourceTypeKind.RWBuffer => ResourceKind.StorageTexelBuffer,
            ResourceTypeKind.StructuredBuffer or ResourceTypeKind.ByteAddressBuffer => ResourceKind.ReadOnlyStorageBuffer,
            ResourceTypeKind.RWStructuredBuffer or ResourceTypeKind.RWByteAddressBuffer or ResourceTypeKind.AppendStructuredBuffer
                or ResourceTypeKind.ConsumeStructuredBuffer => ResourceKind.StorageBuffer,
            _ => ResourceKind.SampledTexture,
        };
    }

    /// <summary>Creates the implicit constants buffer for plain globals once every declaration is known.</summary>
    private void FinishGlobals()
    {
        if (_uniformGlobals.Count == 0)
        {
            return;
        }

        string name = "Globals";
        while (_global.LookupLocal(name) is not null)
        {
            name = "_" + name;
        }

        var st = new StructType(name);
        var fields = new List<StructField>();
        var sym = new CBufferSymbol(name, _uniformGlobals[0].Span, st);
        foreach (var u in _uniformGlobals)
        {
            var d = u.Declarator!;
            int? offset = null;
            foreach (var a in d.Annotations)
            {
                if (a is PackOffsetAnnotation po)
                {
                    offset = (int)po.ByteOffset;
                }
            }

            var f = new StructField(u.Name, u.Type)
            {
                ExplicitOffset = offset,
                MatrixPacking = (u.Mods & Modifiers.RowMajor) != 0 ? MatrixPacking.RowMajor
                    : (u.Mods & Modifiers.ColumnMajor) != 0 ? MatrixPacking.ColumnMajor
                    : ContainsMatrix(u.Type) ? _packing : null,
            };
            u.Buffer = sym;
            u.MemberIndex = fields.Count;
            sym.Members.Add(u);
            fields.Add(f);
        }

        st.SetFields(fields);
        var res = new ResourceDecl
        {
            Name = name,
            Span = sym.Span,
            Type = new ResourceType(ResourceTypeKind.ConstantBuffer, st),
            Kind = ResourceKind.ConstantBuffer,
            ResourceType = ResourceTypeKind.ConstantBuffer,
            Buffer = sym,
        };
        sym.Resource = res;
        GlobalsBuffer = sym;
        ComputeBufferLayout(res, st, BufferLayoutRules.ConstantBuffer, sym.Span);
        CBuffers.Add(sym);
        Resources.Add(res);
    }
}
