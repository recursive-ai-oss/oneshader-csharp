using System;
using System.Collections.Generic;
using OneShader.Diagnostics;
using OneShader.Lexing;

namespace OneShader.Preprocessing;

/// <summary>
/// The macros an expansion must not expand again (Prosser's "hide set"). Immutable; null stands for the
/// empty set. Sets stay tiny (the nesting depth of one expansion), so arrays beat hash sets.
/// </summary>
internal sealed class HideSet
{
    private readonly Macro[] _items;

    private HideSet(Macro[] items) => _items = items;

    public bool Contains(Macro m)
    {
        foreach (Macro x in _items)
        {
            if (ReferenceEquals(x, m))
            {
                return true;
            }
        }

        return false;
    }

    public static HideSet? Add(HideSet? set, Macro m)
    {
        if (set is null)
        {
            return new HideSet([m]);
        }

        if (set.Contains(m))
        {
            return set;
        }

        var items = new Macro[set._items.Length + 1];
        set._items.CopyTo(items, 0);
        items[^1] = m;
        return new HideSet(items);
    }

    public static HideSet? Union(HideSet? a, HideSet? b)
    {
        if (a is null || ReferenceEquals(a, b))
        {
            return b;
        }

        if (b is null)
        {
            return a;
        }

        HideSet? result = a;
        foreach (Macro m in b._items)
        {
            result = Add(result, m);
        }

        return result;
    }

    public static HideSet? Intersect(HideSet? a, HideSet? b)
    {
        if (a is null || b is null)
        {
            return null;
        }

        if (ReferenceEquals(a, b))
        {
            return a;
        }

        List<Macro>? common = null;
        foreach (Macro m in a._items)
        {
            if (b.Contains(m))
            {
                (common ??= []).Add(m);
            }
        }

        return common is null ? null : new HideSet(common.ToArray());
    }
}

internal enum BuiltinKind : byte
{
    None,
    File,
    Line,
    Counter,
    IncludeLevel,
    HasInclude,
}

internal enum BodyKind : byte
{
    /// <summary>An ordinary token copied to the output.</summary>
    Token,

    /// <summary>A parameter replaced by its fully macro-expanded argument.</summary>
    Param,

    /// <summary>A parameter next to <c>##</c>, replaced by its argument as written.</summary>
    ParamRaw,

    /// <summary><c>#param</c>: the argument as a string literal.</summary>
    Stringify,

    /// <summary>The <c>##</c> operator.</summary>
    Paste,
}

internal readonly record struct BodyItem(BodyKind Kind, Token Tok, int Param);

internal sealed class Macro
{
    public Macro(string name, SourceSpan definedAt)
    {
        Name = name;
        DefinedAt = definedAt;
    }

    public string Name { get; }

    public SourceSpan DefinedAt { get; }

    public bool IsFunction { get; init; }

    public string[] Params { get; init; } = [];

    public bool IsVariadic { get; init; }

    public BodyItem[] Body { get; init; } = [];

    public BuiltinKind Builtin { get; init; }

    /// <summary>True when redefining with <paramref name="other"/> is harmless (same parameters, same tokens and spacing).</summary>
    public bool SameAs(Macro other)
    {
        if (IsFunction != other.IsFunction || IsVariadic != other.IsVariadic || Params.Length != other.Params.Length || Body.Length != other.Body.Length)
        {
            return false;
        }

        for (int i = 0; i < Params.Length; i++)
        {
            if (!string.Equals(Params[i], other.Params[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        for (int i = 0; i < Body.Length; i++)
        {
            BodyItem a = Body[i], b = other.Body[i];
            if (a.Kind != b.Kind || a.Param != b.Param || !string.Equals(a.Tok.Text, b.Tok.Text, StringComparison.Ordinal)
                || (i > 0 && a.Tok.HasLeadingSpace != b.Tok.HasLeadingSpace))
            {
                return false;
            }
        }

        return true;
    }
}
