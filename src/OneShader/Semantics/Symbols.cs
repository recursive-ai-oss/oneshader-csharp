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

    /// <summary>An attribute is unknown or misused.</summary>
    public const string BadAttribute = "OSH0340";

    /// <summary>A language feature the compiler does not support yet.</summary>
    public const string Unsupported = "OSH0350";

    /// <summary>A variable is used in a way it cannot be (void, resource in a struct, ...).</summary>
    public const string BadVariable = "OSH0351";

    /// <summary>A function is recursive or called but not defined.</summary>
    public const string BadFunction = "OSH0352";

    /// <summary>A resource or interface limit that the back ends cannot express.</summary>
    public const string Limit = "OSH0353";

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

    public Declarator? Declarator { get; set; }

    public Expression? Initializer { get; set; }

    public CBufferSymbol? Buffer { get; set; }

    public int MemberIndex { get; set; }

    public ResourceDecl? Resource { get; set; }

    public ParameterDeclaration? Parameter { get; set; }

    // Lowering state.
    public IrValue? Ir { get; set; }

    public bool IsIndirect { get; set; }
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

    public int Set { get; set; }

    public int Binding { get; set; }

    public GlobalVariable? Global { get; set; }

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
}

internal sealed class FunctionGroup : Symbol
{
    public FunctionGroup(string name, SourceSpan span)
        : base(name, span)
    {
    }

    public List<FunctionSymbol> Overloads { get; } = [];
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
}
