using System;

namespace OneShader.Lexing;

/// <summary>An HLSL source text and the name it is reported under.</summary>
public sealed class SourceFile
{
    private int[]? _lineStarts;

    /// <summary>Creates a source file.</summary>
    /// <param name="path">The path shown in diagnostics and returned by <c>__FILE__</c>.</param>
    /// <param name="text">The file's text.</param>
    /// <param name="identity">
    /// A key that is equal for the same physical file however it was reached (typically the full path);
    /// <c>#pragma once</c> and include guards use it. Defaults to <paramref name="path"/>.
    /// </param>
    public SourceFile(string path, string text, string? identity = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(text);
        Path = path;
        Text = text;
        Identity = identity ?? path;
    }

    /// <summary>The path shown in diagnostics.</summary>
    public string Path { get; }

    /// <summary>The full text.</summary>
    public string Text { get; }

    /// <summary>Key identifying the physical file.</summary>
    public string Identity { get; }

    /// <summary>Offsets at which each line starts. Line breaks are <c>\n</c>, <c>\r\n</c> and a lone <c>\r</c>.</summary>
    internal int[] LineStarts => _lineStarts ??= ComputeLineStarts(Text);

    /// <summary>Converts a 0-based offset to a 1-based line and column (UTF-16 code units).</summary>
    public (int Line, int Column) GetLineColumn(int offset)
    {
        int[] starts = LineStarts;
        int lo = 0, hi = starts.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >>> 1;
            if (starts[mid] <= offset)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return (lo + 1, offset - starts[lo] + 1);
    }

    private static int[] ComputeLineStarts(string text)
    {
        var list = new System.Collections.Generic.List<int>(Math.Max(16, text.Length / 32)) { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n')
            {
                list.Add(i + 1);
            }
            else if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                list.Add(i + 1);
            }
        }

        return list.ToArray();
    }
}
