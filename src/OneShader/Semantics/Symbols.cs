using System;
using System.Collections.Generic;
using OneShader.Diagnostics;
using OneShader.IR;
using OneShader.Reflection;
using OneShader.Syntax;
using OneShader.Types;

namespace OneShader.Semantics;

/// <summary>Diagnostic codes reported by semantic analysis and lowering.</summary>
public static class SemanticCodes
{
    /// <summary>A name is not declared.</summary>
    public const string Undeclared = "OSH0300";

    /// <summary>A name is declared twice in one scope.</summary>
    public const string Redefinition = "OSH0301";

    /// <summary>An expression has the wrong type.</summary>
    public const string TypeMismatch = "OSH0302";

    /// <summary>A call passes the wrong number of arguments.</summary>
    public const string ArgumentCount = "OSH0303";

    /// <summary>No overload accepts the arguments.</summary>
    public const string NoOverload = "OSH0304";

    /// <summary>More than one overload fits equally well.</summary>
    public const string AmbiguousCall = "OSH0305";

    /// <summary>An implicit conversion drops vector or matrix components.</summary>
    public const string ImplicitTruncation = "OSH0306";

    /// <summary>The target of an assignment cannot be assigned.</summary>
    public const string NotAssignable = "OSH0307";

    /// <summary>A member or swizzle is not valid for the type.</summary>
    public const string BadMember = "OSH0308";

    /// <summary>An operator does not apply to the operand types.</summary>
    public const string BadOperator = "OSH0309";

    /// <summary>A value must be a compile-time constant.</summary>
    public const string NotConstant = "OSH0310";

    /// <summary>A type name is not valid here.</summary>
    public const string BadType = "OSH0311";

    /// <summary>Return statement problem.</summary>
    public const string BadReturn = "OSH0312";

    /// <summary><c>break</c>, <c>continue</c> or <c>discard</c> in the wrong place.</summary>
    public const string BadJump = "OSH0313";

    /// <summary>An atomic operation or barrier is used incorrectly.</summary>
    public const string BadAtomic = "OSH0314";

    /// <summary>A function can fall off its end without returning a value.</summary>
    public const string MissingReturn = "OSH0315";

    /// <summary>An entry point is invalid.</summary>
    public const string BadEntryPoint = "OSH0320";

    /// <summary>An entry point input or output has a missing or wrong semantic.</summary>
    public const string BadSemantic = "OSH0321";

    /// <summary>Two resources share a binding.</summary>
    public const string BindingCollision = "OSH0330";

    /// <summary>A resource declaration or register is invalid.</summary>
    public const string BadResource = "OSH0331";

    /// <summary>A constant buffer layout problem.</summary>
    public const string BadLayout = "OSH0332";

    /// <summary>A push-constant block is declared in a way that cannot work, or an entry point uses two.</summary>
    public const string BadPushConstant = "OSH0333";

    /// <summary>A push-constant block is larger than the 128 bytes every Vulkan device guarantees.</summary>
    public const string PushConstantSize = "OSH0334";

    /// <summary>A <c>[[vk::constant_id]]</c> specialization constant is declared in a way that cannot work.</summary>
    public const string BadSpecConstant = "OSH0335";

    /// <summary><c>ResourceDescriptorHeap</c> or <c>SamplerDescriptorHeap</c> is used in a way that cannot work.</summary>
    public const string BadDescriptorHeap = "OSH0336";

    /// <summary>An attribute is unknown or misused.</summary>
    public const string BadAttribute = "OSH0340";

    /// <summary>A 16-bit scalar type was used without enabling 16-bit types.</summary>
    public const string SixteenBitTypesOff = "OSH0360";

    /// <summary>A double argument is narrowed to float because the intrinsic only exists for float.</summary>
    public const string DoubleNarrowed = "OSH0361";

    /// <summary>A language feature the compiler does not support yet.</summary>
    public const string Unsupported = "OSH0350";

    /// <summary>A variable is used in a way it cannot be (void, resource in a struct, ...).</summary>
    public const string BadVariable = "OSH0351";

    /// <summary>A function is recursive or called but not defined.</summary>
    public const string BadFunction = "OSH0352";

    /// <summary>A resource or interface limit that the back ends cannot express.</summary>
    public const string Limit = "OSH0353";

    /// <summary>The note naming the template instantiation that an error was found in.</summary>
    public const string Instantiation = "OSH0354";

    /// <summary>An internal problem was caught; the shader may be fine.</summary>
    public const string Internal = "OSH0399";
}

internal abstract class Symbol
{
    protected Symbol(string name, SourceSpan span)
    {
        Name = name;
        Span = span;
    }

    public string Name { get; }

    public SourceSpan Span { get; }
}

internal enum VarKind
{
    Local,
    Param,
    Global,
    GroupShared,
    Resource,
    CBufferMember,

    /// <summary>A <c>[[vk::constant_id]]</c> specialization constant: a value the host sets at pipeline creation.</summary>
    Spec,
}

internal sealed class VariableSymbol : Symbol
{
    public VariableSymbol(string name, SourceSpan span, VarKind kind, HlslType type, Modifiers mods)
        : base(name, span)
    {
        Kind = kind;
        Type = type;
        Mods = mods;
    }

    public VarKind Kind { get; }

    public HlslType Type { get; set; }

    public Modifiers Mods { get; }

    public bool IsConst => (Mods & Modifiers.Const) != 0;

    public bool IsStatic => (Mods & Modifiers.Static) != 0;

    public ConstValue? Const { get; set; }

    /// <summary>For <see cref="VarKind.Spec"/>: the <c>constant_id</c> number.</summary>
    public uint SpecId { get; set; }

    /// <summary>For <see cref="VarKind.Spec"/>: the default value, converted to the declared type.</summary>
    public ConstValue SpecDefault { get; set; }

    public Declarator? Declarator { get; set; }

    public Expression? Initializer { get; set; }

    public CBufferSymbol? Buffer { get; set; }

    public int MemberIndex { get; set; }

    public ResourceDecl? Resource { get; set; }

    public ParameterDeclaration? Parameter { get; set; }

    // Lowering state.
    public IrValue? Ir { get; set; }

    public bool IsIndirect { get; set; }

    /// <summary>The name of the IR global for a static local, which differs from the source name.</summary>
    public string? IrName { get; set; }
}

/// <summary>Which SM 6.6 descriptor heap a resource variable was created for.</summary>
internal enum HeapKind
{
    None,
    Resource,
    Sampler,
}

/// <summary>A resource as declared: everything binding assignment and reflection need.</summary>
internal sealed class ResourceDecl
{
    public required string Name { get; init; }

    public required SourceSpan Span { get; init; }

    public required HlslType Type { get; init; }

    public int Count { get; init; } = 1;

    public ResourceKind Kind { get; init; }

    public ResourceTypeKind ResourceType { get; init; }

    public string HlslTypeText { get; init; } = "";

    public int? ExplicitSet { get; set; }

    public int? ExplicitBinding { get; set; }

    public bool ExplicitFromVk { get; set; }

    /// <summary>True for a <c>[[vk::push_constant]]</c> block: no descriptor, a struct laid out like a structured-buffer element.</summary>
    public bool IsPushConstant { get; set; }

    /// <summary>For a variable standing for <c>ResourceDescriptorHeap</c> or <c>SamplerDescriptorHeap</c> read as one resource type: which heap. None for ordinary resources.</summary>
    public HeapKind Heap { get; init; }

    /// <summary>Lower-case <c>[[vk::image_format]]</c> name of a storage texture, or null.</summary>
    public string? ImageFormat { get; set; }

    public int Set { get; set; }

    public int Binding { get; set; }

    public GlobalVariable? Global { get; set; }

    /// <summary>True for append and consume buffers, which carry a hidden element counter in a second buffer.</summary>
    public bool HasCounter { get; set; }

    /// <summary><c>[[vk::counter_binding(n)]]</c>, or null to assign the next free binding of the buffer's set.</summary>
    public int? ExplicitCounterBinding { get; set; }

    /// <summary>The counter's binding in the buffer's own set once bindings are assigned.</summary>
    public int CounterBinding { get; set; }

    /// <summary>The IR global of the counter.</summary>
    public GlobalVariable? CounterGlobal { get; set; }

    public TypeLayout? Layout { get; set; }

    public CBufferSymbol? Buffer { get; init; }

    public VariableSymbol? Variable { get; init; }

    public ShaderStages Stages { get; set; }
}

internal sealed class CBufferSymbol : Symbol
{
    public CBufferSymbol(string name, SourceSpan span, StructType type)
        : base(name, span)
    {
        Struct = type;
    }

    public StructType Struct { get; }

    public List<VariableSymbol> Members { get; } = [];

    public ResourceDecl? Resource { get; set; }
}

internal sealed class ParamInfo
{
    public required string Name { get; init; }

    public required HlslType Type { get; init; }

    public Modifiers Mods { get; init; }

    public bool IsOut => (Mods & Modifiers.Out) != 0;

    public bool IsIn => (Mods & Modifiers.In) != 0 || (Mods & Modifiers.Out) == 0;

    public Expression? Default { get; init; }

    public ParameterDeclaration? Decl { get; init; }

    public VariableSymbol? Symbol { get; set; }
}

internal sealed class FunctionSymbol : Symbol
{
    public FunctionSymbol(string name, SourceSpan span, FunctionDeclaration decl)
        : base(name, span)
    {
        Decl = decl;
    }

    public FunctionDeclaration Decl { get; }

    public List<ParamInfo> Params { get; } = [];

    public HlslType ReturnType { get; set; } = HlslType.Void;

    public bool HasBody => Decl.Body is not null;

    public bool Checked { get; set; }

    public bool InProgress { get; set; }

    public IrFunction? Ir { get; set; }

    public List<FunctionSymbol> Callees { get; } = [];

    public HashSet<ResourceDecl> UsedResources { get; } = [];

    public bool HasResourceParams { get; set; }

    public bool Discards { get; set; }

    /// <summary>The definition, when this symbol is a forward declaration that a later declaration completed.</summary>
    public FunctionSymbol? Definition { get; set; }

    public bool Called { get; set; }

    public ShaderStage? EntryStage { get; set; }

    /// <summary>The struct a member function belongs to; null for free functions.</summary>
    public StructType? Owner { get; set; }

    public bool IsStatic { get; set; }

    /// <summary>Declared <c>const</c> after the parameter list: <c>this</c> is read-only.</summary>
    public bool IsConstMember { get; set; }

    /// <summary>True for a non-static member function, whose first parameter is the hidden <c>this</c>.</summary>
    public bool HasThis => Owner is not null && !IsStatic;

    public VariableSymbol? ThisSymbol { get; set; }

    /// <summary>The name in the IR and in the output, when it differs from <see cref="Symbol.Name"/>.</summary>
    public string? IrName { get; set; }

    /// <summary>Where the function calls a barrier that waits for the thread group, for the stage check.</summary>
    public SourceSpan? GroupSyncSpan { get; set; }

    public string GroupSyncName { get; set; } = "";
}

/// <summary>A function template: instantiated by the call that names it.</summary>
internal sealed class FunctionTemplate
{
    public required FunctionDeclaration Decl { get; init; }

    public required Scope Scope { get; init; }

    public StructType? Owner { get; init; }

    public Dictionary<string, FunctionSymbol> Instances { get; } = new(StringComparer.Ordinal);
}

/// <summary>A struct template: instantiated wherever it is named with arguments.</summary>
internal sealed class StructTemplateSymbol : Symbol
{
    public StructTemplateSymbol(string name, SourceSpan span, StructDeclaration decl, Scope scope)
        : base(name, span)
    {
        Decl = decl;
        Scope = scope;
    }

    public StructDeclaration Decl { get; }

    public Scope Scope { get; }

    public Dictionary<string, StructType> Instances { get; } = new(StringComparer.Ordinal);
}

internal sealed class FunctionGroup : Symbol
{
    public FunctionGroup(string name, SourceSpan span)
        : base(name, span)
    {
    }

    public List<FunctionSymbol> Overloads { get; } = [];

    public List<FunctionTemplate> Templates { get; } = [];
}

internal sealed class TypeSymbol : Symbol
{
    public TypeSymbol(string name, SourceSpan span, HlslType type)
        : base(name, span)
    {
        Type = type;
    }

    public HlslType Type { get; }
}

internal sealed class EnumeratorSymbol : Symbol
{
    public EnumeratorSymbol(string name, SourceSpan span, EnumType type, long value)
        : base(name, span)
    {
        Type = type;
        Value = value;
    }

    public EnumType Type { get; }

    public long Value { get; }
}

internal sealed class NamespaceSymbol : Symbol
{
    public NamespaceSymbol(string name, SourceSpan span, Scope scope)
        : base(name, span)
    {
        Scope = scope;
    }

    public Scope Scope { get; }
}

internal sealed class Scope
{
    private readonly Dictionary<string, Symbol> _map = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    public Scope(Scope? parent)
    {
        Parent = parent;
    }

    public Scope? Parent { get; }

    public IReadOnlyList<string> Names => _order;

    public Symbol? LookupLocal(string name) => _map.TryGetValue(name, out var s) ? s : null;

    /// <summary>Looks the name up from this scope outwards, stopping after <paramref name="last"/>.</summary>
    public Symbol? LookupUpTo(string name, Scope last)
    {
        for (var s = this; s is not null; s = s.Parent)
        {
            if (s._map.TryGetValue(name, out var sym))
            {
                return sym;
            }

            if (ReferenceEquals(s, last))
            {
                break;
            }
        }

        return null;
    }

    public Symbol? Lookup(string name)
    {
        for (var s = this; s is not null; s = s.Parent)
        {
            if (s._map.TryGetValue(name, out var sym))
            {
                return sym;
            }
        }

        return null;
    }

    public void Add(Symbol symbol)
    {
        if (_map.TryAdd(symbol.Name, symbol))
        {
            _order.Add(symbol.Name);
        }
        else
        {
            _map[symbol.Name] = symbol;
        }
    }
}

/// <summary>What semantic analysis learned about an expression, for lowering.</summary>
internal sealed class ExprInfo
{
    public Symbol? Symbol { get; set; }

    public ConstValue? Const { get; set; }

    /// <summary>
    /// The exact value of an expression built only from unsuffixed integer literals. HLSL evaluates such
    /// expressions in wide arithmetic and converts the result where it is used, so <c>(1 &lt;&lt; 31) &gt;&gt; 3</c> is 0x10000000.
    /// </summary>
    public long? LiteralValue { get; set; }

    public bool IsLValue { get; set; }

    /// <summary>For operators: the type both operands are converted to before the operation.</summary>
    public HlslType? OperandType { get; set; }

    /// <summary>For a call to a user function.</summary>
    public FunctionSymbol? Function { get; set; }

    /// <summary>For a call to a built-in function or method.</summary>
    public BuiltinCall? Builtin { get; set; }

    /// <summary>For a member access: struct field index, or -1.</summary>
    public int FieldIndex { get; set; } = -1;

    /// <summary>Types the arguments are converted to.</summary>
    public HlslType[]? ParamTypes { get; set; }

    /// <summary>For constructors and casts: the target type.</summary>
    public HlslType? TargetType { get; set; }

    /// <summary>The expression this one stands for (an inherited member reached through the base, or a member named without <c>this.</c>); lowering uses it instead.</summary>
    public Expression? Rewritten { get; set; }

    /// <summary>For a call of a member function or an overloaded operator: the object it is called on.</summary>
    public Expression? ThisArg { get; set; }

    /// <summary>For an operator or subscript that calls a user function: the arguments after the object.</summary>
    public IReadOnlyList<Expression>? Args { get; set; }

    /// <summary>For <c>ResourceDescriptorHeap[i]</c> / <c>SamplerDescriptorHeap[i]</c> once it is given a resource type: the heap variable of that type.</summary>
    public ResourceDecl? HeapDecl { get; set; }
}
