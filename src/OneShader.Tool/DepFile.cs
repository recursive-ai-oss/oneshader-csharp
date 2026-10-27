using System;
using System.Collections.Generic;
using System.Text;

namespace OneShader.Tool;

/// <summary>Make-style dependency files (<c>out: dep dep</c>), as Make, Ninja and CMake read them.</summary>
internal static class DepFile
{
    public static string Format(IReadOnlyList<string> targets, IReadOnlyList<string> dependencies)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < targets.Count; i++)
        {
            sb.Append(i == 0 ? "" : " ");
            Escape(sb, targets[i]);
        }

        sb.Append(':');
        foreach (string d in dependencies)
        {
            sb.Append(' ');
            Escape(sb, d);
        }

        return sb.Append('\n').ToString();
    }

    private static void Escape(StringBuilder sb, string path)
    {
        // Windows paths use forward slashes so Make reads the drive letter and separators; spaces, '#' and '$' are escaped as Make expects.
        string p = OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;
        foreach (char c in p)
        {
            if (c is ' ' or '#')
            {
                sb.Append('\\');
            }
            else if (c == '$')
            {
                sb.Append('$');
            }

            sb.Append(c);
        }
    }
}
