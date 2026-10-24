using System;
using System.Collections.Generic;
using OneShader.Diagnostics;
using OneShader.Types;

namespace OneShader.Syntax;

/// <summary>Base of every syntax tree node. Each node knows the source range it covers, in the original file.</summary>
public abstract class SyntaxNode
{
    private protected SyntaxNode(SourceSpan span)
    {
        Span = span;
    }

    /// <summary>The text range of the node (<see cref="SourceSpan.Length"/> covers the whole node).</summary>
    public SourceSpan Span { get; }
}

/// <summary>An identifier with its position.</summary>
public sealed class Identifier(SourceSpan span, string text) : SyntaxNode(span)
{
    /// <summary>The identifier text.</summary>
    public string Text { get; } = text;

    /// <inheritdoc/>
    public override string ToString() => Text;
}

/// <summary>A possibly qualified name such as <c>vk::binding</c> or <c>Lighting::Light</c>.</summary>
public sealed class QualifiedName(SourceSpan span, IReadOnlyList<Identifier> parts) : SyntaxNode(span)
{
    /// <summary>The parts between <c>::</c>, at least one.</summary>
    public IReadOnlyList<Identifier> Parts { get; } = parts;

    /// <summary>True when written as <c>::name</c> (anchored at the global namespace).</summary>
    public bool IsGlobal { get; init; }

    /// <summary>The last part.</summary>
    public Identifier Last => Parts[^1];

    /// <inheritdoc/>
    public override string ToString() => (IsGlobal ? "::" : "") + string.Join("::", Parts);
}

/// <summary>The unit of parsing: one source file after preprocessing.</summary>
public sealed class CompilationUnit(SourceSpan span, IReadOnlyList<Declaration> declarations) : SyntaxNode(span)
{
    /// <summary>Top-level declarations in source order.</summary>
    public IReadOnlyList<Declaration> Declarations { get; } = declarations;
}

// ---------------------------------------------------------------- Types

/// <summary>A type as written in source.</summary>
public abstract class TypeSyntax(SourceSpan span) : SyntaxNode(span)
{
    /// <summary>The resolved type; set by semantic analysis.</summary>
    public HlslType? Resolved { get; set; }
}

/// <summary>
/// A named type, optionally with template arguments: <c>float3</c>, <c>Texture2D&lt;float4&gt;</c>,
/// <c>vector&lt;float, 3&gt;</c>, <c>ConstantBuffer&lt;Light&gt;</c>, <c>Ns::Foo&lt;int&gt;</c>. Built-in type
/// names are ordinary names here; semantic analysis knows them.
/// </summary>
public sealed class NamedTypeSyntax(SourceSpan span, QualifiedName name, IReadOnlyList<TemplateArgument> arguments) : TypeSyntax(span)
{
    /// <summary>The type name.</summary>
    public QualifiedName Name { get; } = name;

    /// <summary>Template arguments; empty when none were written.</summary>
    public IReadOnlyList<TemplateArgument> Arguments { get; } = arguments;
}

/// <summary>A struct, class, or enum declared in place of a type: <c>struct { float a; } s;</c>.</summary>
public sealed class DeclaredTypeSyntax(SourceSpan span, Declaration declaration) : TypeSyntax(span)
{
    /// <summary>The struct or enum declaration.</summary>
    public Declaration Declaration { get; } = declaration;
}

/// <summary>A template argument: a type or a constant expression (exactly one is set).</summary>
public sealed class TemplateArgument(SourceSpan span, TypeSyntax? type, Expression? value) : SyntaxNode(span)
{
    /// <summary>The type argument, or null.</summary>
    public TypeSyntax? Type { get; } = type;

    /// <summary>The value argument, or null.</summary>
    public Expression? Value { get; } = value;
}

// ---------------------------------------------------------------- Attributes, modifiers, annotations

/// <summary>Modifiers that precede a type or follow a parameter list.</summary>
[Flags]
public enum Modifiers
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary><c>static</c>.</summary>
    Static = 1 << 0,

    /// <summary><c>const</c>.</summary>
    Const = 1 << 1,

    /// <summary><c>uniform</c>.</summary>
    Uniform = 1 << 2,

    /// <summary><c>volatile</c>.</summary>
    Volatile = 1 << 3,

    /// <summary><c>extern</c>.</summary>
    Extern = 1 << 4,

    /// <summary><c>shared</c>.</summary>
    Shared = 1 << 5,

    /// <summary><c>groupshared</c>.</summary>
    GroupShared = 1 << 6,

    /// <summary><c>precise</c>.</summary>
    Precise = 1 << 7,

    /// <summary><c>inline</c>.</summary>
    Inline = 1 << 8,

    /// <summary><c>row_major</c>.</summary>
    RowMajor = 1 << 9,

    /// <summary><c>column_major</c>.</summary>
    ColumnMajor = 1 << 10,

    /// <summary><c>nointerpolation</c>.</summary>
    NoInterpolation = 1 << 11,

    /// <summary><c>linear</c>.</summary>
    Linear = 1 << 12,

    /// <summary><c>centroid</c>.</summary>
    Centroid = 1 << 13,

    /// <summary><c>sample</c>.</summary>
    Sample = 1 << 14,

    /// <summary><c>noperspective</c>.</summary>
    NoPerspective = 1 << 15,

    /// <summary><c>in</c>.</summary>
    In = 1 << 16,

    /// <summary><c>out</c>.</summary>
    Out = 1 << 17,

    /// <summary><c>inout</c> (both <c>In</c> and <c>Out</c> are set).</summary>
    InOut = In | Out,

    /// <summary><c>unorm</c>.</summary>
    Unorm = 1 << 18,

    /// <summary><c>snorm</c>.</summary>
    Snorm = 1 << 19,

    /// <summary><c>globallycoherent</c>.</summary>
    GloballyCoherent = 1 << 20,

    /// <summary><c>export</c>.</summary>
    Export = 1 << 21,

    /// <summary><c>const</c> after a member function's parameter list.</summary>
    ConstMember = 1 << 22,
}

/// <summary>
/// An attribute: <c>[numthreads(8, 8, 1)]</c>, <c>[unroll]</c>, <c>[shader("vertex")]</c>, or the
/// double-bracket form <c>[[vk::binding(0, 1)]]</c>.
/// </summary>
public sealed class AttributeSyntax(SourceSpan span, QualifiedName name, IReadOnlyList<Expression> arguments, bool isDoubleBracket) : SyntaxNode(span)
{
    /// <summary>The attribute name; <c>vk::binding</c> has two parts.</summary>
    public QualifiedName Name { get; } = name;

    /// <summary>Arguments (string literals are <see cref="LiteralExpression"/>s of kind String).</summary>
    public IReadOnlyList<Expression> Arguments { get; } = arguments;

    /// <summary>True for <c>[[...]]</c>.</summary>
    public bool IsDoubleBracket { get; } = isDoubleBracket;
}

/// <summary>Something written after a colon on a declaration.</summary>
public abstract class Annotation(SourceSpan span) : SyntaxNode(span)
{
}

/// <summary>A semantic: <c>: SV_Target1</c>.</summary>
public sealed class SemanticAnnotation(SourceSpan span, string text) : Annotation(span)
{
    /// <summary>The full semantic text, for example <c>TEXCOORD1</c>.</summary>
    public string Text { get; } = text;
}

/// <summary><c>: register(t3, space1)</c> or <c>register(ps, b0)</c>.</summary>
public sealed class RegisterAnnotation(SourceSpan span, string? profile, char registerClass, uint index, uint space) : Annotation(span)
{
    /// <summary>The shader profile prefix (<c>ps</c>), or null.</summary>
    public string? Profile { get; } = profile;

    /// <summary>The register class letter: <c>b</c>, <c>t</c>, <c>s</c> or <c>u</c> (others are diagnosed by semantic analysis).</summary>
    public char RegisterClass { get; } = registerClass;

    /// <summary>The register number.</summary>
    public uint Index { get; } = index;

    /// <summary>The register space (<c>spaceN</c>), 0 if omitted.</summary>
    public uint Space { get; } = space;
}

/// <summary><c>: packoffset(c2.y)</c>.</summary>
public sealed class PackOffsetAnnotation(SourceSpan span, uint register, uint component) : Annotation(span)
{
    /// <summary>The 16-byte register number (the N of cN).</summary>
    public uint Register { get; } = register;

    /// <summary>The component 0 to 3 (x, y, z, w).</summary>
    public uint Component { get; } = component;

    /// <summary>The byte offset the annotation denotes.</summary>
    public uint ByteOffset => Register * 16 + Component * 4;
}

// ---------------------------------------------------------------- Expressions

/// <summary>The kinds of literal.</summary>
public enum LiteralKind
{
    /// <summary>An integer; <see cref="LiteralExpression.IntegerValue"/> holds it.</summary>
    Integer,

    /// <summary>A floating-point number; <see cref="LiteralExpression.FloatValue"/> holds it.</summary>
    Float,

    /// <summary><c>true</c> or <c>false</c>.</summary>
    Bool,

    /// <summary>A string literal (attribute arguments, <c>register</c> profiles).</summary>
    String,
}

/// <summary>Base of expressions. Semantic analysis fills <see cref="Type"/>.</summary>
public abstract class Expression(SourceSpan span) : SyntaxNode(span)
{
    /// <summary>The type, set by semantic analysis; null before it ran.</summary>
    public HlslType? Type { get; set; }

    /// <summary>What semantic analysis resolved about this expression, for lowering.</summary>
    internal Semantics.ExprInfo? Info { get; set; }
}

/// <summary>A literal.</summary>
public sealed class LiteralExpression(SourceSpan span, LiteralKind kind, string text) : Expression(span)
{
    /// <summary>Which literal.</summary>
    public LiteralKind Kind { get; } = kind;

    /// <summary>The source text, suffix included (<c>1.0f</c>, <c>0xFFu</c>), or the unquoted string contents.</summary>
    public string Text { get; } = text;

    /// <summary>The value of an integer literal.</summary>
    public ulong IntegerValue { get; init; }

    /// <summary>The value of a float literal.</summary>
    public double FloatValue { get; init; }

    /// <summary>The value of a bool literal.</summary>
    public bool BoolValue { get; init; }

    /// <summary>The suffix scalar the literal asks for (<c>u</c> uint, <c>f</c> float, <c>h</c> half, <c>l</c> 64-bit / double); null for none.</summary>
    public ScalarKind? SuffixType { get; init; }
}

/// <summary>A (possibly qualified) name used as a value: variable, parameter, enumerator, function.</summary>
public sealed class NameExpression(SourceSpan span, QualifiedName name) : Expression(span)
{
    /// <summary>The name.</summary>
    public QualifiedName Name { get; } = name;

    /// <summary>The declaration it refers to; set by semantic analysis.</summary>
    public SyntaxNode? Declaration { get; set; }
}

/// <summary><c>this</c> inside a member function.</summary>
public sealed class ThisExpression(SourceSpan span) : Expression(span)
{
}

/// <summary>Parentheses kept in the tree: <c>(a + b)</c>.</summary>
public sealed class ParenthesizedExpression(SourceSpan span, Expression inner) : Expression(span)
{
    /// <summary>The inner expression.</summary>
    public Expression Inner { get; } = inner;
}

/// <summary>Prefix unary operators.</summary>
public enum UnaryOperator
{
    /// <summary><c>+x</c>.</summary>
    Plus,

    /// <summary><c>-x</c>.</summary>
    Minus,

    /// <summary><c>!x</c>.</summary>
    LogicalNot,

    /// <summary><c>~x</c>.</summary>
    BitwiseNot,

    /// <summary><c>++x</c>.</summary>
    PreIncrement,

    /// <summary><c>--x</c>.</summary>
    PreDecrement,

    /// <summary><c>x++</c>.</summary>
    PostIncrement,

    /// <summary><c>x--</c>.</summary>
    PostDecrement,
}

/// <summary>A unary expression, prefix or postfix (see the operator).</summary>
public sealed class UnaryExpression(SourceSpan span, UnaryOperator op, Expression operand) : Expression(span)
{
    /// <summary>The operator.</summary>
    public UnaryOperator Operator { get; } = op;

    /// <summary>The operand.</summary>
    public Expression Operand { get; } = operand;
}

/// <summary>Binary operators other than assignment.</summary>
public enum BinaryOperator
{
    /// <summary><c>+</c>.</summary>
    Add,

    /// <summary><c>-</c>.</summary>
    Subtract,

    /// <summary><c>*</c>.</summary>
    Multiply,

    /// <summary><c>/</c>.</summary>
    Divide,

    /// <summary><c>%</c>.</summary>
    Modulo,

    /// <summary><c>&lt;&lt;</c>.</summary>
    ShiftLeft,

    /// <summary><c>&gt;&gt;</c>.</summary>
    ShiftRight,

    /// <summary><c>&amp;</c>.</summary>
    BitwiseAnd,

    /// <summary><c>|</c>.</summary>
    BitwiseOr,

    /// <summary><c>^</c>.</summary>
    BitwiseXor,

    /// <summary><c>&amp;&amp;</c> (HLSL 2021 evaluates both sides only for non-scalar operands; use <c>and()</c> for explicit non-short-circuit).</summary>
    LogicalAnd,

    /// <summary><c>||</c>.</summary>
    LogicalOr,

    /// <summary><c>==</c>.</summary>
    Equal,

    /// <summary><c>!=</c>.</summary>
    NotEqual,

    /// <summary><c>&lt;</c>.</summary>
    Less,

    /// <summary><c>&lt;=</c>.</summary>
    LessEqual,

    /// <summary><c>&gt;</c>.</summary>
    Greater,

    /// <summary><c>&gt;=</c>.</summary>
    GreaterEqual,
}

/// <summary>A binary expression.</summary>
public sealed class BinaryExpression(SourceSpan span, BinaryOperator op, Expression left, Expression right) : Expression(span)
{
    /// <summary>The operator.</summary>
    public BinaryOperator Operator { get; } = op;

    /// <summary>The left operand.</summary>
    public Expression Left { get; } = left;

    /// <summary>The right operand.</summary>
    public Expression Right { get; } = right;
}

/// <summary>Assignment: <c>=</c> when <see cref="Operator"/> is null, otherwise the compound operator (<c>+=</c> is Add).</summary>
public sealed class AssignmentExpression(SourceSpan span, BinaryOperator? op, Expression target, Expression value) : Expression(span)
{
    /// <summary>The operator of a compound assignment; null for plain <c>=</c>.</summary>
    public BinaryOperator? Operator { get; } = op;

    /// <summary>The assigned-to expression.</summary>
    public Expression Target { get; } = target;

    /// <summary>The value.</summary>
    public Expression Value { get; } = value;
}

/// <summary><c>c ? a : b</c>.</summary>
public sealed class ConditionalExpression(SourceSpan span, Expression condition, Expression whenTrue, Expression whenFalse) : Expression(span)
{
    /// <summary>The condition.</summary>
    public Expression Condition { get; } = condition;

    /// <summary>Value when true.</summary>
    public Expression WhenTrue { get; } = whenTrue;

    /// <summary>Value when false.</summary>
    public Expression WhenFalse { get; } = whenFalse;
}

/// <summary>The comma operator: <c>a, b</c>.</summary>
public sealed class CommaExpression(SourceSpan span, Expression left, Expression right) : Expression(span)
{
    /// <summary>Evaluated first, value discarded.</summary>
    public Expression Left { get; } = left;

    /// <summary>The value of the expression.</summary>
    public Expression Right { get; } = right;
}

/// <summary>
/// A call: a function (<c>saturate(x)</c>), a method (<c>tex.Sample(s, uv)</c>, callee is a
/// <see cref="MemberAccessExpression"/>), or a template function with explicit arguments.
/// </summary>
public sealed class CallExpression(SourceSpan span, Expression callee, IReadOnlyList<TemplateArgument> templateArguments, IReadOnlyList<Expression> arguments) : Expression(span)
{
    /// <summary>What is called.</summary>
    public Expression Callee { get; } = callee;

    /// <summary>Explicit template arguments (<c>Load&lt;float4&gt;(i)</c>); usually empty.</summary>
    public IReadOnlyList<TemplateArgument> TemplateArguments { get; } = templateArguments;

    /// <summary>The arguments.</summary>
    public IReadOnlyList<Expression> Arguments { get; } = arguments;

    /// <summary>The user function chosen by overload resolution; set by semantic analysis. Null for built-ins and constructors.</summary>
    public FunctionDeclaration? ResolvedFunction { get; set; }
}

/// <summary>A constructor call spelled with a type: <c>float3(1, 2, 3)</c>, <c>float4x4(...)</c>, <c>Light(...)</c>.</summary>
public sealed class ConstructExpression(SourceSpan span, TypeSyntax type, IReadOnlyList<Expression> arguments) : Expression(span)
{
    /// <summary>The constructed type.</summary>
    public TypeSyntax ConstructedType { get; } = type;

    /// <summary>The arguments.</summary>
    public IReadOnlyList<Expression> Arguments { get; } = arguments;
}

/// <summary>A C-style cast: <c>(float3)x</c>.</summary>
public sealed class CastExpression(SourceSpan span, TypeSyntax targetType, Expression operand) : Expression(span)
{
    /// <summary>The target type.</summary>
    public TypeSyntax TargetType { get; } = targetType;

    /// <summary>The converted expression.</summary>
    public Expression Operand { get; } = operand;
}

/// <summary>Member selection: a field, a method name, or a swizzle (<c>v.xyz</c>, <c>m._m00</c>).</summary>
public sealed class MemberAccessExpression(SourceSpan span, Expression target, Identifier member) : Expression(span)
{
    /// <summary>The expression selected from.</summary>
    public Expression Target { get; } = target;

    /// <summary>The member name.</summary>
    public Identifier Member { get; } = member;

    /// <summary>For a swizzle: the selected component indices (0 to 3), set by semantic analysis; null for other members.</summary>
    public IReadOnlyList<int>? SwizzleComponents { get; set; }

    /// <summary>The struct member declaration for a field access; set by semantic analysis.</summary>
    public SyntaxNode? Declaration { get; set; }
}

/// <summary>Subscript: <c>a[i]</c>, <c>tex[uv]</c>, or several indices <c>m[i][j]</c> as nested nodes.</summary>
public sealed class IndexExpression(SourceSpan span, Expression target, Expression index) : Expression(span)
{
    /// <summary>The indexed expression.</summary>
    public Expression Target { get; } = target;

    /// <summary>The index.</summary>
    public Expression Index { get; } = index;
}

/// <summary>A brace initializer list: <c>{ 1, 2, 3 }</c>.</summary>
public sealed class InitializerListExpression(SourceSpan span, IReadOnlyList<Expression> elements) : Expression(span)
{
    /// <summary>The elements (themselves possibly lists).</summary>
    public IReadOnlyList<Expression> Elements { get; } = elements;
}

/// <summary><c>sizeof(T)</c> or <c>sizeof expr</c> (HLSL 2021).</summary>
public sealed class SizeOfExpression(SourceSpan span, TypeSyntax? type, Expression? operand) : Expression(span)
{
    /// <summary>The type operand, or null.</summary>
    public TypeSyntax? TypeOperand { get; } = type;

    /// <summary>The expression operand, or null.</summary>
    public Expression? ExpressionOperand { get; } = operand;
}

/// <summary>Stands where an expression was expected but the parser could not read one. The error is already reported.</summary>
public sealed class BadExpression(SourceSpan span) : Expression(span)
{
}

// ---------------------------------------------------------------- Statements

/// <summary>Base of statements. Attributes such as <c>[unroll]</c> precede the statement.</summary>
public abstract class Statement(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes) : SyntaxNode(span)
{
    /// <summary>Attributes in front of the statement.</summary>
    public IReadOnlyList<AttributeSyntax> Attributes { get; } = attributes;
}

/// <summary>A braced block.</summary>
public sealed class BlockStatement(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes, IReadOnlyList<Statement> statements) : Statement(span, attributes)
{
    /// <summary>The statements.</summary>
    public IReadOnlyList<Statement> Statements { get; } = statements;
}

/// <summary>An expression used as a statement.</summary>
public sealed class ExpressionStatement(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes, Expression expression) : Statement(span, attributes)
{
    /// <summary>The expression.</summary>
    public Expression Expression { get; } = expression;
}

/// <summary>A local declaration, or a typedef, struct, or enum declared in a function.</summary>
public sealed class DeclarationStatement(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes, Declaration declaration) : Statement(span, attributes)
{
    /// <summary>The declaration.</summary>
    public Declaration Declaration { get; } = declaration;
}

/// <summary><c>if</c> / <c>else</c>.</summary>
public sealed class IfStatement(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes, Expression condition, Statement then, Statement? @else) : Statement(span, attributes)
{
    /// <summary>The condition.</summary>
    public Expression Condition { get; } = condition;

    /// <summary>The statement run when true.</summary>
    public Statement Then { get; } = then;

    /// <summary>The <c>else</c> statement, or null.</summary>
    public Statement? Else { get; } = @else;
}

/// <summary><c>for (init; condition; step) body</c>. The initializer is a declaration statement or an expression statement.</summary>
public sealed class ForStatement(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes, Statement? initializer, Expression? condition, Expression? step, Statement body) : Statement(span, attributes)
{
    /// <summary>The initializer, or null.</summary>
    public Statement? Initializer { get; } = initializer;

    /// <summary>The condition, or null for none.</summary>
    public Expression? Condition { get; } = condition;

    /// <summary>The step expression, or null.</summary>
    public Expression? Step { get; } = step;

    /// <summary>The loop body.</summary>
    public Statement Body { get; } = body;
}

/// <summary><c>while (condition) body</c>.</summary>
public sealed class WhileStatement(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes, Expression condition, Statement body) : Statement(span, attributes)
{
    /// <summary>The condition.</summary>
    public Expression Condition { get; } = condition;

    /// <summary>The body.</summary>
    public Statement Body { get; } = body;
}

/// <summary><c>do body while (condition);</c>.</summary>
public sealed class DoWhileStatement(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes, Statement body, Expression condition) : Statement(span, attributes)
{
    /// <summary>The body.</summary>
    public Statement Body { get; } = body;

    /// <summary>The condition.</summary>
    public Expression Condition { get; } = condition;
}

/// <summary>One label of a switch section: <c>case expr:</c> or <c>default:</c>.</summary>
public sealed class SwitchLabel(SourceSpan span, Expression? value) : SyntaxNode(span)
{
    /// <summary>The case value; null for <c>default</c>.</summary>
    public Expression? Value { get; } = value;

    /// <summary>True for <c>default</c>.</summary>
    public bool IsDefault => Value is null;
}

/// <summary>The labels of a switch with the statements that follow them (fall-through is explicit by sharing a section).</summary>
public sealed class SwitchSection(SourceSpan span, IReadOnlyList<SwitchLabel> labels, IReadOnlyList<Statement> statements) : SyntaxNode(span)
{
    /// <summary>One or more labels.</summary>
    public IReadOnlyList<SwitchLabel> Labels { get; } = labels;

    /// <summary>The statements until the next label.</summary>
    public IReadOnlyList<Statement> Statements { get; } = statements;
}

/// <summary><c>switch (selector) { ... }</c>.</summary>
public sealed class SwitchStatement(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes, Expression selector, IReadOnlyList<SwitchSection> sections) : Statement(span, attributes)
{
    /// <summary>The selector.</summary>
    public Expression Selector { get; } = selector;

    /// <summary>The sections in source order.</summary>
    public IReadOnlyList<SwitchSection> Sections { get; } = sections;
}

/// <summary><c>break;</c>.</summary>
public sealed class BreakStatement(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes) : Statement(span, attributes)
{
}

/// <summary><c>continue;</c>.</summary>
public sealed class ContinueStatement(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes) : Statement(span, attributes)
{
}

/// <summary><c>discard;</c>.</summary>
public sealed class DiscardStatement(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes) : Statement(span, attributes)
{
}

/// <summary><c>return;</c> or <c>return value;</c>.</summary>
public sealed class ReturnStatement(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes, Expression? value) : Statement(span, attributes)
{
    /// <summary>The returned value, or null.</summary>
    public Expression? Value { get; } = value;
}

/// <summary>A lone <c>;</c>.</summary>
public sealed class EmptyStatement(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes) : Statement(span, attributes)
{
}

/// <summary>Stands where a statement was expected but could not be parsed. The error is already reported.</summary>
public sealed class BadStatement(SourceSpan span) : Statement(span, [])
{
}

// ---------------------------------------------------------------- Declarations

/// <summary>Base of declarations. Attributes precede the declaration.</summary>
public abstract class Declaration(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes) : SyntaxNode(span)
{
    /// <summary>Attributes in front of the declaration (<c>[shader("pixel")]</c>, <c>[numthreads(...)]</c>).</summary>
    public IReadOnlyList<AttributeSyntax> Attributes { get; } = attributes;
}

/// <summary>One declared variable inside a <see cref="VariableDeclaration"/>: name, array dimensions, annotations and initializer.</summary>
public sealed class Declarator(SourceSpan span, Identifier name, IReadOnlyList<Expression?> arrayDimensions, IReadOnlyList<Annotation> annotations, Expression? initializer) : SyntaxNode(span)
{
    /// <summary>The variable name.</summary>
    public Identifier Name { get; } = name;

    /// <summary>Array dimensions in order; a null entry is an unsized <c>[]</c>.</summary>
    public IReadOnlyList<Expression?> ArrayDimensions { get; } = arrayDimensions;

    /// <summary>Semantic, register and packoffset annotations.</summary>
    public IReadOnlyList<Annotation> Annotations { get; } = annotations;

    /// <summary>The initializer after <c>=</c>, or null.</summary>
    public Expression? Initializer { get; } = initializer;

    /// <summary>The declared type including array dimensions; set by semantic analysis.</summary>
    public HlslType? Type { get; set; }
}

/// <summary>A variable declaration with one or more declarators: <c>static const float a = 1, b[2];</c>. Also a struct or cbuffer member, or a global.</summary>
public sealed class VariableDeclaration(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes, Modifiers modifiers, TypeSyntax type, IReadOnlyList<Declarator> declarators) : Declaration(span, attributes)
{
    /// <summary>The modifiers.</summary>
    public Modifiers Modifiers { get; } = modifiers;

    /// <summary>The declared type, without array dimensions.</summary>
    public TypeSyntax Type { get; } = type;

    /// <summary>The declared variables.</summary>
    public IReadOnlyList<Declarator> Declarators { get; } = declarators;
}

/// <summary>A function parameter.</summary>
public sealed class ParameterDeclaration(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes, Modifiers modifiers, TypeSyntax type, Identifier? name, IReadOnlyList<Expression?> arrayDimensions, IReadOnlyList<Annotation> annotations, Expression? defaultValue) : Declaration(span, attributes)
{
    /// <summary>The modifiers, including <c>in</c>, <c>out</c>, <c>inout</c> and interpolation.</summary>
    public Modifiers Modifiers { get; } = modifiers;

    /// <summary>The type.</summary>
    public TypeSyntax Type { get; } = type;

    /// <summary>The name; null for an unnamed parameter in a prototype.</summary>
    public Identifier? Name { get; } = name;

    /// <summary>Array dimensions.</summary>
    public IReadOnlyList<Expression?> ArrayDimensions { get; } = arrayDimensions;

    /// <summary>Semantic annotations.</summary>
    public IReadOnlyList<Annotation> Annotations { get; } = annotations;

    /// <summary>Default argument, or null.</summary>
    public Expression? DefaultValue { get; } = defaultValue;

    /// <summary>The parameter type including arrays; set by semantic analysis.</summary>
    public HlslType? ResolvedType { get; set; }
}

/// <summary>A template parameter: <c>typename T</c>, <c>uint N</c>, optionally with a default.</summary>
public sealed class TemplateParameter(SourceSpan span, Identifier name, TypeSyntax? valueType, TemplateArgument? defaultArgument) : SyntaxNode(span)
{
    /// <summary>The parameter name.</summary>
    public Identifier Name { get; } = name;

    /// <summary>For a value parameter its type; null for a <c>typename</c>/<c>class</c> parameter.</summary>
    public TypeSyntax? ValueType { get; } = valueType;

    /// <summary>The default, or null.</summary>
    public TemplateArgument? Default { get; } = defaultArgument;

    /// <summary>True for a type parameter.</summary>
    public bool IsTypeParameter => ValueType is null;
}

/// <summary>A function or member function (prototype when <see cref="Body"/> is null), including operator overloads.</summary>
public sealed class FunctionDeclaration(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes, Modifiers modifiers, TypeSyntax returnType, QualifiedName name, IReadOnlyList<TemplateParameter> templateParameters, IReadOnlyList<ParameterDeclaration> parameters, IReadOnlyList<Annotation> returnAnnotations, BlockStatement? body) : Declaration(span, attributes)
{
    /// <summary>The modifiers (<c>static</c>, <c>inline</c>, <c>export</c>, const member).</summary>
    public Modifiers Modifiers { get; } = modifiers;

    /// <summary>The return type.</summary>
    public TypeSyntax ReturnType { get; } = returnType;

    /// <summary>The name; for operator overloads <c>operator+</c>; qualified when defined outside its struct.</summary>
    public QualifiedName Name { get; } = name;

    /// <summary>Template parameters; empty for a plain function.</summary>
    public IReadOnlyList<TemplateParameter> TemplateParameters { get; } = templateParameters;

    /// <summary>The parameters.</summary>
    public IReadOnlyList<ParameterDeclaration> Parameters { get; } = parameters;

    /// <summary>Semantic annotation of the return value (<c>float4 main() : SV_Target</c>).</summary>
    public IReadOnlyList<Annotation> ReturnAnnotations { get; } = returnAnnotations;

    /// <summary>The body, or null for a prototype.</summary>
    public BlockStatement? Body { get; } = body;

    /// <summary>The return type; set by semantic analysis.</summary>
    public HlslType? ResolvedReturnType { get; set; }
}

/// <summary>A struct or class, possibly with base types and member functions.</summary>
public sealed class StructDeclaration(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes, bool isClass, Identifier? name, IReadOnlyList<TemplateParameter> templateParameters, IReadOnlyList<NamedTypeSyntax> baseTypes, IReadOnlyList<Declaration> members) : Declaration(span, attributes)
{
    /// <summary>True for <c>class</c>.</summary>
    public bool IsClass { get; } = isClass;

    /// <summary>The name, or null for an anonymous struct.</summary>
    public Identifier? Name { get; } = name;

    /// <summary>Template parameters of a struct template.</summary>
    public IReadOnlyList<TemplateParameter> TemplateParameters { get; } = templateParameters;

    /// <summary>Base types (HLSL 2021 inheritance).</summary>
    public IReadOnlyList<NamedTypeSyntax> BaseTypes { get; } = baseTypes;

    /// <summary>Fields (<see cref="VariableDeclaration"/>), methods (<see cref="FunctionDeclaration"/>), nested typedefs.</summary>
    public IReadOnlyList<Declaration> Members { get; } = members;

    /// <summary>The struct type; set by semantic analysis.</summary>
    public StructType? Resolved { get; set; }
}

/// <summary>One enumerator.</summary>
public sealed class EnumMember(SourceSpan span, Identifier name, Expression? value) : SyntaxNode(span)
{
    /// <summary>The name.</summary>
    public Identifier Name { get; } = name;

    /// <summary>The explicit value, or null.</summary>
    public Expression? Value { get; } = value;
}

/// <summary><c>enum</c> or <c>enum class</c>, optionally with an underlying type.</summary>
public sealed class EnumDeclaration(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes, bool isScoped, Identifier? name, TypeSyntax? underlyingType, IReadOnlyList<EnumMember> members) : Declaration(span, attributes)
{
    /// <summary>True for <c>enum class</c>.</summary>
    public bool IsScoped { get; } = isScoped;

    /// <summary>The name, or null for an anonymous enum.</summary>
    public Identifier? Name { get; } = name;

    /// <summary>The underlying type after the colon, or null.</summary>
    public TypeSyntax? UnderlyingType { get; } = underlyingType;

    /// <summary>The enumerators.</summary>
    public IReadOnlyList<EnumMember> Members { get; } = members;

    /// <summary>The enum type; set by semantic analysis.</summary>
    public EnumType? Resolved { get; set; }
}

/// <summary><c>typedef type name;</c> (and C++-style <c>using name = type;</c>).</summary>
public sealed class TypedefDeclaration(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes, Modifiers modifiers, TypeSyntax type, IReadOnlyList<Declarator> declarators) : Declaration(span, attributes)
{
    /// <summary>Modifiers such as <c>row_major</c>.</summary>
    public Modifiers Modifiers { get; } = modifiers;

    /// <summary>The aliased type.</summary>
    public TypeSyntax Type { get; } = type;

    /// <summary>The new names (a declarator may carry array dimensions).</summary>
    public IReadOnlyList<Declarator> Declarators { get; } = declarators;
}

/// <summary><c>cbuffer Name : register(b0) { ... }</c> (or <c>tbuffer</c>).</summary>
public sealed class ConstantBufferDeclaration(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes, bool isTextureBuffer, Identifier name, IReadOnlyList<Annotation> annotations, IReadOnlyList<VariableDeclaration> members) : Declaration(span, attributes)
{
    /// <summary>True for <c>tbuffer</c>.</summary>
    public bool IsTextureBuffer { get; } = isTextureBuffer;

    /// <summary>The buffer name.</summary>
    public Identifier Name { get; } = name;

    /// <summary>The register annotation, if any.</summary>
    public IReadOnlyList<Annotation> Annotations { get; } = annotations;

    /// <summary>The members; with <c>packoffset</c> their declarators carry <see cref="PackOffsetAnnotation"/>.</summary>
    public IReadOnlyList<VariableDeclaration> Members { get; } = members;
}

/// <summary><c>namespace Name { ... }</c>.</summary>
public sealed class NamespaceDeclaration(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes, Identifier name, IReadOnlyList<Declaration> members) : Declaration(span, attributes)
{
    /// <summary>The namespace name.</summary>
    public Identifier Name { get; } = name;

    /// <summary>The declarations inside.</summary>
    public IReadOnlyList<Declaration> Members { get; } = members;
}

/// <summary>
/// A <c>#pragma</c> that the preprocessor passes on because it affects meaning, for example
/// <c>pack_matrix(row_major)</c>. Its position in the tree is where it takes effect.
/// </summary>
public sealed class PragmaDeclaration(SourceSpan span, string text) : Declaration(span, [])
{
    /// <summary>The text after <c>#pragma</c>.</summary>
    public string Text { get; } = text;
}

/// <summary>Stands where a declaration was expected but could not be parsed. The error is already reported.</summary>
public sealed class BadDeclaration(SourceSpan span) : Declaration(span, [])
{
}
