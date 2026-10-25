using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using OneShader.Diagnostics;
using OneShader.Syntax;
using OneShader.Types;

namespace OneShader.Semantics;

// Templates (HLSL 2021): every instantiation re-reads the template's tokens, so each gets its own syntax tree
// and its own analysis results; the template parameters become names in a scope around the declaration.
internal sealed partial class Checker
{
    private const int MaxInstantiationDepth = 48;

    private int _instDepth;
    private readonly Dictionary<StructType, StructTemplateSymbol> _templateOf = [];

    // The arguments each struct template instance was made with, so a function parameter written Box<T> can deduce T.
    private readonly Dictionary<StructType, Binding?[]> _instanceArgs = [];

    private sealed class Binding
    {
        public HlslType? Type { get; init; }

        public ConstValue? Value { get; init; }

        public ScalarType? ValueType { get; init; }
    }

    private sealed class TemplateMatch
    {
        public required FunctionTemplate Template { get; init; }

        public required FunctionDeclaration Fresh { get; init; }

        public required Scope BindScope { get; init; }

        public required string Key { get; init; }

        public FunctionSymbol? Cached { get; init; }

        public List<ParamInfo> Params { get; } = [];

        public HlslType ReturnType { get; set; } = HlslType.Error;

        public StructType? Owner { get; init; }

        public bool IsStatic { get; init; }
    }

    private Declaration? Reparse(TemplateSource src) => Parser.ReparseTemplate(src, new DiagnosticBag(), _file, _ct);

    private T Quietly<T>(Func<T> f, out bool errors)
    {
        var saved = _diag;
        var tmp = new DiagnosticBag();
        _diag = tmp;
        try
        {
            var r = f();
            errors = tmp.HasErrors;
            return r;
        }
        finally
        {
            _diag = saved;
        }
    }

    private static int IndexOfParam(IReadOnlyList<TemplateParameter> ps, string name)
    {
        for (int i = 0; i < ps.Count; i++)
        {
            if (ps[i].Name.Text == name)
            {
                return i;
            }
        }

        return -1;
    }

    private static Scope MakeBindScope(Scope outer, IReadOnlyList<TemplateParameter> ps, Binding?[] b)
    {
        var s = new Scope(outer);
        for (int k = 0; k < ps.Count; k++)
        {
            var bind = b[k];
            if (bind is null)
            {
                continue;
            }

            if (bind.Type is not null)
            {
                s.Add(new TypeSymbol(ps[k].Name.Text, ps[k].Name.Span, bind.Type));
            }
            else
            {
                s.Add(new VariableSymbol(ps[k].Name.Text, ps[k].Name.Span, VarKind.Global, bind.ValueType!, Modifiers.Const | Modifiers.Static) { Const = bind.Value });
            }
        }

        return s;
    }

    private static string KeyOf(string name, IReadOnlyList<TemplateParameter> ps, Binding?[] b)
    {
        var sb = new StringBuilder(name).Append('<');
        for (int k = 0; k < ps.Count; k++)
        {
            if (k > 0)
            {
                sb.Append(',');
            }

            var bind = b[k];
            if (bind?.Type is not null)
            {
                sb.Append(bind.Type);
            }
            else if (bind?.Value is { } v)
            {
                sb.Append(v.IsFloat ? v.Float.ToString("R", CultureInfo.InvariantCulture) : v.Int.ToString(CultureInfo.InvariantCulture));
            }
        }

        return sb.Append('>').ToString();
    }

    /// <summary>The value of a template argument written at a use site, or null (already reported or explained by <paramref name="why"/>).</summary>
    private Binding? EvalTemplateArg(TemplateParameter p, TemplateArgument a, out string why)
    {
        why = "";
        if (p.IsTypeParameter)
        {
            if (a.Type is null)
            {
                why = "template argument for '" + p.Name.Text + "' must be a type";
                return null;
            }

            var t = ResolveType(a.Type);
            if (t.IsError)
            {
                why = "template argument for '" + p.Name.Text + "' is not a valid type";
                return null;
            }

            return new Binding { Type = t };
        }

        // A constant name parses like a type name.
        Expression? e = a.Value ?? (a.Type is NamedTypeSyntax { Name.Parts.Count: 1, Arguments.Count: 0 } nt ? new NameExpression(nt.Span, nt.Name) : null);
        if (e is null)
        {
            why = "template argument for '" + p.Name.Text + "' must be a constant value, not a type";
            return null;
        }

        CheckExpr(e);
        var c = e.Info?.Const;
        if (c is null)
        {
            if (e.Type is { IsError: false })
            {
                Err(SemanticCodes.NotConstant, "The template argument for '" + p.Name.Text + "' must be a compile-time constant.", e.Span);
            }

            why = "template argument for '" + p.Name.Text + "' is not a constant";
            return null;
        }

        var vt = p.ValueType is null ? ScalarType.Int : ResolveType(p.ValueType) as ScalarType ?? ScalarType.Int;
        return new Binding { Value = c.Value.ConvertTo(vt), ValueType = vt };
    }

    // ------------------------------------------------------------ deduction

    private static bool BindType(Binding?[] b, bool[] explicitly, int k, HlslType a)
    {
        if (explicitly[k])
        {
            return true;
        }

        if (b[k]?.Type is { } existing)
        {
            return existing.Equals(a);
        }

        b[k] = new Binding { Type = a };
        return true;
    }

    private bool BindValue(Binding?[] b, bool[] explicitly, IReadOnlyList<TemplateParameter> ps, int k, long v)
    {
        if (explicitly[k])
        {
            return true;
        }

        var vt = ps[k].ValueType is null ? ScalarType.Int : ResolveType(ps[k].ValueType!) as ScalarType ?? ScalarType.Int;
        var cv = ConstValue.FromInt(ScalarType.Int, v).ConvertTo(vt);
        if (b[k]?.Value is { } existing)
        {
            return existing.Int == cv.Int;
        }

        b[k] = new Binding { Value = cv, ValueType = vt };
        return true;
    }

    /// <summary>Matches a parameter's declared type (and array sizes) against an argument type, binding template parameters on the way.</summary>
    private bool Deduce(IReadOnlyList<TemplateParameter> ps, Binding?[] b, bool[] explicitly, TypeSyntax pt, IReadOnlyList<Expression?> dims, HlslType arg)
    {
        var a = arg;
        foreach (var dim in dims)
        {
            if (a is not ArrayType at)
            {
                return false;
            }

            if (dim is NameExpression { Name.Parts.Count: 1 } ne)
            {
                int k = IndexOfParam(ps, ne.Name.Last.Text);
                if (k >= 0 && !ps[k].IsTypeParameter && !BindValue(b, explicitly, ps, k, at.Length))
                {
                    return false;
                }
            }

            a = at.Element;
        }

        return MatchType(ps, b, explicitly, pt, a);
    }

    private bool MatchType(IReadOnlyList<TemplateParameter> ps, Binding?[] b, bool[] explicitly, TypeSyntax pt, HlslType a)
    {
        if (pt is not NamedTypeSyntax n)
        {
            return true;
        }

        string nm = n.Name.Last.Text;
        if (n.Name.Parts.Count == 1 && n.Arguments.Count == 0)
        {
            int k = IndexOfParam(ps, nm);
            return k < 0 || !ps[k].IsTypeParameter || BindType(b, explicitly, k, a);
        }

        switch (nm)
        {
            case "vector" when n.Arguments.Count == 2 && a is VectorType v:
                return MatchArg(ps, b, explicitly, n.Arguments[0], v.Element) && MatchArg(ps, b, explicitly, n.Arguments[1], v.Count);
            case "matrix" when n.Arguments.Count == 3 && a is MatrixType m:
                return MatchArg(ps, b, explicitly, n.Arguments[0], m.Element) && MatchArg(ps, b, explicitly, n.Arguments[1], m.Rows) && MatchArg(ps, b, explicitly, n.Arguments[2], m.Columns);
            case "vector" or "matrix":
                return false;
        }

        if (a is ResourceType rt && n.Arguments.Count >= 1 && rt.Element is not null && BuiltinTypes.TryGetResource(nm, out _))
        {
            return MatchArg(ps, b, explicitly, n.Arguments[0], rt.Element);
        }

        if (n.Name.Parts.Count == 1 && _scope.Lookup(nm) is StructTemplateSymbol)
        {
            // Box<T> against an instance of Box: take the instance's arguments.
            if (a is not StructType inst || !_templateOf.TryGetValue(inst, out var owner) || owner.Name != nm || !_instanceArgs.TryGetValue(inst, out var actual))
            {
                return false;
            }

            for (int i = 0; i < n.Arguments.Count && i < actual.Length; i++)
            {
                object? value = actual[i]?.Type is { } t ? t : actual[i]?.Value is { } v ? v.Int : null;
                if (value is not null && !MatchArg(ps, b, explicitly, n.Arguments[i], value))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private bool MatchArg(IReadOnlyList<TemplateParameter> ps, Binding?[] b, bool[] explicitly, TemplateArgument ta, object actual)
    {
        if (actual is HlslType nested && ta.Type is NamedTypeSyntax { Arguments.Count: > 0 } nestedSyntax)
        {
            return MatchType(ps, b, explicitly, nestedSyntax, nested);
        }

        string? nm = ta.Type is NamedTypeSyntax { Name.Parts.Count: 1, Arguments.Count: 0 } t1 ? t1.Name.Last.Text
            : ta.Value is NameExpression { Name.Parts.Count: 1 } ne ? ne.Name.Last.Text
            : null;
        int k = nm is null ? -1 : IndexOfParam(ps, nm);
        if (k < 0)
        {
            return true;
        }

        if (actual is HlslType type)
        {
            return !ps[k].IsTypeParameter || BindType(b, explicitly, k, type);
        }

        return ps[k].IsTypeParameter || BindValue(b, explicitly, ps, k, Convert.ToInt64(actual, CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------ function templates

    private TemplateMatch? BindTemplate(FunctionTemplate t, string name, IReadOnlyList<TemplateArgument>? targs, IReadOnlyList<Expression> args, HlslType[] at, SourceSpan span, StructType? thisType, out string why)
    {
        _ = args;
        _ = thisType;
        why = "";
        if (Reparse(t.Decl.Source!) is not FunctionDeclaration fresh)
        {
            why = "the template '" + name + "' could not be read again";
            return null;
        }

        var ps = fresh.TemplateParameters;
        var b = new Binding?[ps.Count];
        var explicitly = new bool[ps.Count];
        if (targs is not null)
        {
            if (targs.Count > ps.Count)
            {
                why = "'" + name + "' takes " + Num(ps.Count) + " template argument" + (ps.Count == 1 ? "" : "s") + " but " + Num(targs.Count) + " were given";
                return null;
            }

            for (int k = 0; k < targs.Count; k++)
            {
                b[k] = EvalTemplateArg(ps[k], targs[k], out why);
                if (b[k] is null)
                {
                    return null;
                }

                explicitly[k] = true;
            }
        }

        for (int i = 0; i < fresh.Parameters.Count && i < at.Length; i++)
        {
            var p = fresh.Parameters[i];
            if (!Deduce(ps, b, explicitly, p.Type, p.ArrayDimensions, at[i]))
            {
                why = "argument " + Num(i + 1) + " of type '" + at[i] + "' does not match the parameter" + (p.Name is null ? "" : " '" + p.Name.Text + "'");
                return null;
            }
        }

        var saved = _scope;
        try
        {
            for (int k = 0; k < ps.Count; k++)
            {
                if (b[k] is not null)
                {
                    continue;
                }

                if (ps[k].Default is not { } def)
                {
                    why = "the template argument '" + ps[k].Name.Text + "' cannot be deduced from the call; give it explicitly, for example " + name + "<" + (ps[k].IsTypeParameter ? "float" : "4") + ">(...)";
                    return null;
                }

                _scope = MakeBindScope(t.Scope, ps, b);
                b[k] = EvalTemplateArg(ps[k], def, out why);
                if (b[k] is null)
                {
                    return null;
                }
            }
        }
        finally
        {
            _scope = saved;
        }

        string key = KeyOf(name, ps, b);
        if (t.Instances.TryGetValue(key, out var cached))
        {
            var hit = new TemplateMatch { Template = t, Fresh = fresh, BindScope = t.Scope, Key = key, Cached = cached, ReturnType = cached.ReturnType, Owner = t.Owner, IsStatic = cached.IsStatic };
            for (int i = cached.HasThis ? 1 : 0; i < cached.Params.Count; i++)
            {
                hit.Params.Add(cached.Params[i]);
            }

            return hit;
        }

        var bind = MakeBindScope(t.Scope, ps, b);
        var m = new TemplateMatch
        {
            Template = t,
            Fresh = fresh,
            BindScope = bind,
            Key = key,
            Owner = t.Owner,
            IsStatic = (fresh.Modifiers & Modifiers.Static) != 0,
        };
        bool bad = false;
        var prevScope = _scope;
        _scope = bind;
        try
        {
            foreach (var p in fresh.Parameters)
            {
                var pt = Quietly(() => ApplyDims(ResolveType(p.Type), p.ArrayDimensions, false, p.Span), out bool err);
                if (err || pt.IsError)
                {
                    bad = true;
                    why = "substituting the template arguments gives an invalid type for parameter" + (p.Name is null ? "" : " '" + p.Name.Text + "'");
                    break;
                }

                m.Params.Add(new ParamInfo { Name = p.Name?.Text ?? "", Type = pt, Mods = p.Modifiers, Default = p.DefaultValue, Decl = p });
            }

            if (!bad)
            {
                m.ReturnType = Quietly(() => ResolveType(fresh.ReturnType), out bool err);
                if (err)
                {
                    bad = true;
                    why = "substituting the template arguments gives an invalid return type";
                }
            }
        }
        finally
        {
            _scope = prevScope;
        }

        _ = span;
        return bad ? null : m;
    }

    private FunctionSymbol? InstantiateFunction(TemplateMatch m, SourceSpan callSpan)
    {
        if (m.Cached is not null)
        {
            return m.Cached;
        }

        var t = m.Template;
        if (_instDepth >= MaxInstantiationDepth)
        {
            Err(SemanticCodes.Limit, "Template instantiation is nested more than " + Num(MaxInstantiationDepth) + " levels deep while instantiating '" + m.Key + "'. A template that instantiates itself with ever-changing arguments never ends; add a base case with a different overload.", callSpan);
            return null;
        }

        var outer = _diag;
        var local = new DiagnosticBag();
        _diag = local;
        _instDepth++;
        FunctionSymbol sym;
        try
        {
            sym = BuildFunctionSymbol(m.Fresh, m.BindScope, t.Owner, m.Key);
            t.Instances[m.Key] = sym;
            _allFns.Add(sym);
            if (sym.HasBody)
            {
                Functions.Add(sym);
                CheckFunctionBody(sym, m.BindScope);
            }
        }
        finally
        {
            _diag = outer;
            _instDepth--;
        }

        foreach (var d in local.Items)
        {
            outer.Add(d);
        }

        if (local.HasErrors)
        {
            outer.Info(SemanticCodes.Instantiation, "In instantiation of '" + m.Key + "', requested here.", callSpan);
        }

        return sym;
    }

    // ------------------------------------------------------------ struct templates

    private HlslType InstantiateStruct(StructTemplateSymbol t, IReadOnlyList<TemplateArgument> args, SourceSpan span)
    {
        var ps0 = t.Decl.TemplateParameters;
        if (args.Count > ps0.Count)
        {
            Err(SemanticCodes.BadType, "The struct template '" + t.Name + "' takes " + Num(ps0.Count) + " template argument" + (ps0.Count == 1 ? "" : "s") + " but " + Num(args.Count) + " were given.", span);
            return HlslType.Error;
        }

        if (_instDepth >= MaxInstantiationDepth)
        {
            Err(SemanticCodes.Limit, "Template instantiation is nested more than " + Num(MaxInstantiationDepth) + " levels deep while instantiating '" + t.Name + "'; a struct template that contains itself never ends.", span);
            return HlslType.Error;
        }

        if (Reparse(t.Decl.Source!) is not StructDeclaration fresh)
        {
            Err(SemanticCodes.Unsupported, "The struct template '" + t.Name + "' could not be read again.", span);
            return HlslType.Error;
        }

        var ps = fresh.TemplateParameters;
        var b = new Binding?[ps.Count];
        for (int k = 0; k < args.Count; k++)
        {
            b[k] = EvalTemplateArg(ps[k], args[k], out string why);
            if (b[k] is null)
            {
                if (why.Length > 0)
                {
                    Err(SemanticCodes.BadType, "In '" + t.Name + "<...>': " + why + ".", args[k].Span);
                }

                return HlslType.Error;
            }
        }

        var saved = _scope;
        try
        {
            for (int k = args.Count; k < ps.Count; k++)
            {
                if (ps[k].Default is not { } def)
                {
                    Err(SemanticCodes.BadType, "The struct template '" + t.Name + "' needs " + Num(ps.Count) + " template argument" + (ps.Count == 1 ? "" : "s") + ", for example " + t.Name + "<" + (ps[0].IsTypeParameter ? "float" : "4") + ">, but " + Num(args.Count) + " were given.", span);
                    return HlslType.Error;
                }

                _scope = MakeBindScope(t.Scope, ps, b);
                b[k] = EvalTemplateArg(ps[k], def, out _);
                if (b[k] is null)
                {
                    return HlslType.Error;
                }
            }
        }
        finally
        {
            _scope = saved;
        }

        string key = KeyOf(t.Name, ps, b);
        if (t.Instances.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var bind = MakeBindScope(t.Scope, ps, b);
        var outer = _diag;
        var local = new DiagnosticBag();
        _diag = local;
        _instDepth++;
        var savedScope = _scope;
        HlslType result;
        try
        {
            _scope = bind;
            result = DeclareStructCore(fresh, bind, key, false, t);
            if (result is StructType made)
            {
                _instanceArgs[made] = b;
            }
        }
        finally
        {
            _scope = savedScope;
            _diag = outer;
            _instDepth--;
        }

        foreach (var d in local.Items)
        {
            outer.Add(d);
        }

        if (local.HasErrors)
        {
            outer.Info(SemanticCodes.Instantiation, "In instantiation of '" + key + "', requested here.", span);
        }

        return result;
    }
}
