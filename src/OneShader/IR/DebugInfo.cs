using System;
using System.Collections.Generic;
using System.Text;
using OneShader.Diagnostics;
using OneShader.Types;

namespace OneShader.IR;

/// <summary>A parameter as the user declared it, kept so debug output can name it.</summary>
/// <param name="Name">The parameter name.</param>
/// <param name="Type">The declared type.</param>
/// <param name="Span">Where it is declared.</param>
public sealed record SourceParameter(string Name, HlslType Type, SourceSpan Span);

/// <summary>
/// A function as written in the HLSL source. Optimization may inline it into another IR function, so debug output
/// finds it again through the source positions of the instructions it produced, not through the IR function.
/// </summary>
public sealed class SourceFunction
{
    /// <summary>Creates the description of one source function.</summary>
    public SourceFunction(string name, SourceSpan nameSpan, SourceSpan extent, HlslType returnType, IReadOnlyList<SourceParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
        NameSpan = nameSpan;
        Extent = extent;
        ReturnType = returnType;
        Parameters = parameters;
    }

    /// <summary>The name as written.</summary>
    public string Name { get; }

    /// <summary>Position of the name; the function's line and column in debug output.</summary>
    public SourceSpan NameSpan { get; }

    /// <summary>The whole declaration, from its first token to the closing brace.</summary>
    public SourceSpan Extent { get; }

    /// <summary>The declared return type.</summary>
    public HlslType ReturnType { get; }

    /// <summary>The declared parameters.</summary>
    public IReadOnlyList<SourceParameter> Parameters { get; }

    /// <summary>Brace-delimited blocks and <c>for</c> statements inside the body, in source order.</summary>
    public List<SourceSpan> Blocks { get; } = [];
}

/// <summary>A local variable or parameter the user declared, attached to the <see cref="Opcode.Alloca"/> that holds it.</summary>
/// <param name="Name">The variable name.</param>
/// <param name="Type">The declared type.</param>
/// <param name="Span">Where it is declared.</param>
/// <param name="ArgumentNumber">1-based parameter position, or 0 for an ordinary local.</param>
public sealed record SourceVariable(string Name, HlslType Type, SourceSpan Span, int ArgumentNumber);

/// <summary>One level of inlining: the call that was replaced by the callee's body.</summary>
/// <param name="Call">The position of the call.</param>
/// <param name="Outer">The call site this one was itself inlined into, or null.</param>
public sealed record InlineSite(SourceSpan Call, InlineSite? Outer);

/// <summary>The text of a source file, kept so debug output can embed it.</summary>
/// <param name="Path">The path as it appears in instruction positions.</param>
/// <param name="Text">The file's text.</param>
public sealed record SourceText(string Path, string Text);

/// <summary>Turns file paths into the spelling debug output records: relative to a root, with forward slashes, never absolute.</summary>
public static class DebugPaths
{
    /// <summary>
    /// Returns <paramref name="path"/> relative to <paramref name="root"/> with <c>/</c> separators when it lies under the root.
    /// A relative path keeps its spelling (separators normalized). An absolute path outside the root is reduced to its file name,
    /// so no machine path leaks into a binary that is meant to be reproducible.
    /// </summary>
    /// <param name="path">The path as the compiler saw it.</param>
    /// <param name="root">The directory paths are made relative to; null or empty leaves relative paths alone.</param>
    public static string Normalize(string path, string? root)
    {
        if (path.Length == 0)
        {
            return path;
        }

        string p = path.Replace('\\', '/');
        if (!string.IsNullOrEmpty(root))
        {
            string r = root.Replace('\\', '/');
            if (!r.EndsWith('/'))
            {
                r += "/";
            }

            if (IsRooted(p) && IsRooted(r))
            {
                // Collapse "." and ".." so "a/../b" compares by what it names.
                p = Collapse(p);
                r = Collapse(r);
                if (!r.EndsWith('/'))
                {
                    r += "/";
                }

                if (p.StartsWith(r, StringComparison.Ordinal))
                {
                    return p[r.Length..];
                }
            }
        }

        if (IsRooted(p))
        {
            int slash = p.LastIndexOf('/');
            return slash >= 0 ? p[(slash + 1)..] : p;
        }

        return p.StartsWith("./", StringComparison.Ordinal) ? p[2..] : p;
    }

    private static bool IsRooted(string p) =>
        p.StartsWith('/') || p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '/';

    private static string Collapse(string p)
    {
        var parts = new List<string>();
        foreach (string seg in p.Split('/'))
        {
            if (seg is "." or "")
            {
                continue;
            }

            if (seg == ".." && parts.Count > 0 && parts[^1] != "..")
            {
                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(seg);
        }

        var sb = new StringBuilder();
        if (p.StartsWith('/'))
        {
            sb.Append('/');
        }

        sb.Append(string.Join('/', parts));
        return sb.ToString();
    }
}
