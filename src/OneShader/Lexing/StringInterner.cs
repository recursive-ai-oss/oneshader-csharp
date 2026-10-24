using System;
using System.Collections.Generic;

namespace OneShader.Lexing;

/// <summary>Shares one string instance per distinct token text so lexing a name twice allocates once.</summary>
internal sealed class StringInterner
{
    private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);
    private Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _lookup;

    public StringInterner()
    {
        _lookup = _map.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public string Intern(ReadOnlySpan<char> text)
    {
        if (text.Length == 0)
        {
            return string.Empty;
        }

        if (_lookup.TryGetValue(text, out string? existing))
        {
            return existing;
        }

        string s = text.ToString();
        _map.Add(s, s);
        return s;
    }

    public string Intern(string text) => Intern(text.AsSpan());
}
