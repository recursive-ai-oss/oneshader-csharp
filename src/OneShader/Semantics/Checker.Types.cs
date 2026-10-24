using System;
using System.Collections.Generic;
using System.Globalization;
using OneShader.Diagnostics;
using OneShader.Syntax;
using OneShader.Types;

namespace OneShader.Semantics;

internal sealed partial class Checker
{
    private ScalarType Canon(ScalarType s)
    {
        if (_opt.Layout.Enable16BitTypes)
        {
            return s;
        }

        return s.ScalarKind switch
        {
            ScalarKind.Half or ScalarKind.Min16Float or ScalarKind.Min10Float => ScalarType.Float,
            ScalarKind.Min16Int => ScalarType.Int,
            ScalarKind.Min16UInt => ScalarType.UInt,
            _ => s,
        };
    }

    private HlslType CanonType(HlslType t) => t switch
    {
        ScalarType s => Canon(s),
        VectorType v => HlslType.Vector(Canon(v.Element), v.Count),
        MatrixType m => HlslType.Matrix(Canon(m.Element), m.Rows, m.Columns),
        _ => t,
    };

    public HlslType ResolveType(TypeSyntax t)
    {
        var r = ResolveTypeCore(t);
        t.Resolved = r;
        return r;
    }

    private HlslType ResolveTypeCore(TypeSyntax t)
    {
        switch (t)
        {
            case DeclaredTypeSyntax d:
                switch (d.Declaration)
                {
                    case StructDeclaration sd:
                        return DeclareStruct(sd, _scope);
                    case EnumDeclaration ed:
                        DeclareEnum(ed, _scope);
                        return ed.Resolved?.Underlying ?? HlslType.Error;
                    default:
                        return HlslType.Error;
                }

            case NamedTypeSyntax n:
                return ResolveNamedType(n);
            default:
                return HlslType.Error;
        }
    }

    private Symbol? LookupQualified(QualifiedName name, out bool failedInNamespace)
    {
        failedInNamespace = false;
        Scope? scope = name.IsGlobal ? _global : _scope;
        Symbol? sym = null;
        for (int i = 0; i < name.Parts.Count; i++)
        {
            string part = name.Parts[i].Text;
            if (i == 0)
            {
                sym = scope.Lookup(part);
            }
            else if (sym is NamespaceSymbol ns)
            {
                sym = ns.Scope.LookupLocal(part);
            }
            else if (sym is TypeSymbol { Type: EnumType et })
            {
                sym = null;
                foreach (var kv in et.Members)
                {
                    if (kv.Key == part)
                    {
                        sym = new EnumeratorSymbol(part, name.Span, et, kv.Value);
                        break;
                    }
                }
            }
            else
            {
                sym = null;
            }

            if (sym is null)
            {
                failedInNamespace = i > 0;
                return null;
            }
        }

        return sym;
    }

    private HlslType ResolveNamedType(NamedTypeSyntax n)
    {
        var name = n.Name;
        string last = name.Last.Text;
        if (name.Parts.Count == 1 || name.IsGlobal)
        {
            var sym = LookupQualified(name, out _);
            if (sym is TypeSymbol ts)
            {
                if (n.Arguments.Count > 0)
                {
                    Err(SemanticCodes.BadType, "The type '" + last + "' takes no template arguments.", n.Span);
                }

                return ts.Type is EnumType en ? en.Underlying : ts.Type;
            }

            if (sym is not null && name.Parts.Count == 1)
            {
                Err(SemanticCodes.BadType, "'" + last + "' is not a type; it is a " + Describe(sym) + ".", name.Span);
                return HlslType.Error;
            }
        }
        else
        {
            var sym = LookupQualified(name, out _);
            if (sym is TypeSymbol ts)
            {
                return ts.Type is EnumType en ? en.Underlying : ts.Type;
            }
        }

        if (name.Parts.Count == 1)
        {
            return ResolveBuiltinType(n, last);
        }

        Err(SemanticCodes.Undeclared, "The type '" + name + "' is not declared.", name.Span);
        return HlslType.Error;
    }

    private static string Describe(Symbol s) => s switch
    {
        VariableSymbol => "variable",
        FunctionGroup or FunctionSymbol => "function",
        NamespaceSymbol => "namespace",
        CBufferSymbol => "constant buffer",
        EnumeratorSymbol => "enumerator",
        _ => "name",
    };

    private int? TemplateInt(TemplateArgument a, string what)
    {
        Expression? value = a.Value;
        if (value is null && a.Type is NamedTypeSyntax { Name.Parts.Count: 1, Arguments.Count: 0 } nt)
        {
            // A constant name parses like a type name.
            value = new NameExpression(nt.Span, nt.Name);
        }

        if (value is null)
        {
            Err(SemanticCodes.BadType, "Expected " + what + " (an integer constant) but found a type.", a.Span);
            return null;
        }

        CheckExpr(value);
        var c = value.Info?.Const;
        if (c is null || c.Value.IsFloat)
        {
            if (value.Type is { IsError: false })
            {
                Err(SemanticCodes.NotConstant, "Expected " + what + " to be an integer constant expression.", a.Span);
            }

            return null;
        }

        return (int)c.Value.Int;
    }

    private HlslType ResolveBuiltinType(NamedTypeSyntax n, string name)
    {
        if (name is "vector" or "matrix")
        {
            ScalarType elem = ScalarType.Float;
            int a = 4;
            int b = 4;
            if (n.Arguments.Count > 0)
            {
                var et = n.Arguments[0].Type is { } ts ? ResolveType(ts) : null;
                if (et is ScalarType s)
                {
                    elem = Canon(s);
                }
                else
                {
                    if (et is not null && !et.IsError)
                    {
                        Err(SemanticCodes.BadType, "The element type of '" + name + "' must be a scalar type.", n.Arguments[0].Span);
                    }

                    return HlslType.Error;
                }
            }

            int expected = name == "vector" ? 2 : 3;
            if (n.Arguments.Count != 0 && n.Arguments.Count != expected)
            {
                Err(SemanticCodes.BadType, "'" + name + "' needs " + (name == "vector" ? "<type, size>" : "<type, rows, columns>") + ".", n.Span);
                return HlslType.Error;
            }

            if (n.Arguments.Count == expected)
            {
                var x = TemplateInt(n.Arguments[1], "the size");
                var y = name == "matrix" ? TemplateInt(n.Arguments[2], "the column count") : x;
                if (x is null || y is null)
                {
                    return HlslType.Error;
                }

                a = x.Value;
                b = y.Value;
                if (a is < 1 or > 4 || b is < 1 or > 4)
                {
                    Err(SemanticCodes.BadType, "Vector and matrix dimensions must be between 1 and 4.", n.Span);
                    return HlslType.Error;
                }
            }

            return name == "vector" ? HlslType.Vector(elem, a) : HlslType.Matrix(elem, a, b);
        }

        if (BuiltinTypes.TryGetResource(name, out var rk))
        {
            return ResolveResourceType(n, name, rk);
        }

        if (BuiltinTypes.TryGet(name, out var bt))
        {
            if (n.Arguments.Count > 0)
            {
                Err(SemanticCodes.BadType, "The type '" + name + "' takes no template arguments.", n.Span);
            }

            return CanonType(bt);
        }

        if (name == "string")
        {
            Err(SemanticCodes.Unsupported, "The string type is not supported in shaders.", n.Span);
            return HlslType.Error;
        }

        string? hint = Suggest(name, TypeNames());
        Err(SemanticCodes.Undeclared, "The type '" + name + "' is not declared." + (hint is null ? " Declare it with 'struct', 'typedef' or 'enum' before use." : " Did you mean '" + hint + "'?"), n.Name.Span);
        return HlslType.Error;
    }

    private IEnumerable<string> TypeNames()
    {
        for (var s = _scope; s is not null; s = s.Parent)
        {
            foreach (var nm in s.Names)
            {
                if (s.LookupLocal(nm) is TypeSymbol)
                {
                    yield return nm;
                }
            }
        }

        foreach (var b in new[] { "float", "float2", "float3", "float4", "float4x4", "int", "uint", "bool", "half", "double", "Texture2D", "SamplerState", "StructuredBuffer", "RWStructuredBuffer", "ByteAddressBuffer" })
        {
            yield return b;
        }
    }

    private HlslType ResolveResourceType(NamedTypeSyntax n, string name, ResourceTypeKind rk)
    {
        HlslType? element = null;
        int samples = 0;
        bool hasArgs = n.Arguments.Count > 0;
        if (hasArgs)
        {
            if (n.Arguments[0].Type is { } ts)
            {
                element = ResolveType(ts);
            }
            else
            {
                Err(SemanticCodes.BadType, "The first template argument of '" + name + "' must be a type.", n.Arguments[0].Span);
                return HlslType.Error;
            }

            if (n.Arguments.Count > 1)
            {
                if (rk is ResourceTypeKind.Texture2DMS or ResourceTypeKind.Texture2DMSArray)
                {
                    samples = TemplateInt(n.Arguments[1], "the sample count") ?? 0;
                }
                else
                {
                    Err(SemanticCodes.BadType, "'" + name + "' takes only one template argument.", n.Arguments[1].Span);
                }
            }
        }
        else
        {
            element = BuiltinTypes.DefaultElement(rk);
        }

        if (element is not null && element.IsError)
        {
            return HlslType.Error;
        }

        bool isTexture = rk <= ResourceTypeKind.RWTexture3D;
        if (isTexture || rk is ResourceTypeKind.Buffer or ResourceTypeKind.RWBuffer)
        {
            if (element is not (ScalarType or VectorType))
            {
                Err(SemanticCodes.BadType, "The element type of '" + name + "' must be a scalar or vector type such as float4, not '" + element + "'.", n.Span);
                return HlslType.Error;
            }

            if (element.ScalarElement!.ScalarKind == ScalarKind.Bool)
            {
                Err(SemanticCodes.BadType, "The element type of '" + name + "' cannot be bool.", n.Span);
                return HlslType.Error;
            }
        }
        else if (rk is ResourceTypeKind.StructuredBuffer or ResourceTypeKind.RWStructuredBuffer or ResourceTypeKind.AppendStructuredBuffer or ResourceTypeKind.ConsumeStructuredBuffer or ResourceTypeKind.ConstantBuffer)
        {
            if (!hasArgs)
            {
                Err(SemanticCodes.BadType, "'" + name + "' needs an element type: write '" + name + "<Type>'.", n.Span);
                return HlslType.Error;
            }

            if (!TypeOps.IsValueType(element!))
            {
                Err(SemanticCodes.BadType, "'" + element + "' cannot be the element type of '" + name + "'.", n.Span);
                return HlslType.Error;
            }
        }
        else if (hasArgs)
        {
            Err(SemanticCodes.BadType, "'" + name + "' takes no template arguments.", n.Span);
            return HlslType.Error;
        }

        return new ResourceType(rk, element is null ? null : CanonType(element), samples);
    }

    public HlslType ApplyDims(HlslType baseType, IReadOnlyList<Expression?> dims, bool allowUnsized, SourceSpan where)
    {
        if (dims.Count == 0 || baseType.IsError)
        {
            return baseType;
        }

        var t = baseType;
        for (int i = dims.Count - 1; i >= 0; i--)
        {
            var dim = dims[i];
            int len;
            if (dim is null)
            {
                if (!allowUnsized || i != 0)
                {
                    Err(SemanticCodes.BadType, "An array needs a size: write the length between the brackets.", where);
                    return HlslType.Error;
                }

                len = 0;
            }
            else
            {
                CheckExpr(dim);
                var c = dim.Info?.Const;
                if (c is null || c.Value.IsFloat || c.Value.Int <= 0 || c.Value.Int > 65536)
                {
                    if (dim.Type is { IsError: false })
                    {
                        Err(SemanticCodes.NotConstant, "An array size must be a constant integer between 1 and 65536.", dim.Span);
                    }

                    return HlslType.Error;
                }

                len = (int)c.Value.Int;
            }

            t = HlslType.Array(t, len);
        }

        return t;
    }

    // ---- name suggestions

    /// <summary>The closest of <paramref name="candidates"/> to <paramref name="name"/>, or null when nothing is close.</summary>
    private static string? Suggest(string name, IEnumerable<string> candidates)
    {
        string? best = null;
        int bestScore = int.MaxValue;
        int limit = name.Length <= 3 ? 1 : name.Length <= 6 ? 2 : 3;
        foreach (var c in candidates)
        {
            if (c.Length == 0 || c == name)
            {
                continue;
            }

            int d = string.Equals(c, name, StringComparison.OrdinalIgnoreCase) ? 0 : Distance(name, c, limit);
            if (d <= limit && d < bestScore)
            {
                best = c;
                bestScore = d;
            }
        }

        return best;
    }

    private static int Distance(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit)
        {
            return int.MaxValue;
        }

        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }

    private IEnumerable<string> VisibleValueNames()
    {
        for (var s = _scope; s is not null; s = s.Parent)
        {
            foreach (var nm in s.Names)
            {
                var sym = s.LookupLocal(nm);
                if (sym is VariableSymbol or FunctionGroup or EnumeratorSymbol)
                {
                    yield return nm;
                }
            }
        }
    }

    private static string Num(int v) => v.ToString(CultureInfo.InvariantCulture);
}
