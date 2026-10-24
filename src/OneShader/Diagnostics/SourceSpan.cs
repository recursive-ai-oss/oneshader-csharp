namespace OneShader.Diagnostics;

/// <summary>
/// A range of text in one source file. Line and column are 1-based and refer to the original
/// file on disk, also when the text arrived through <c>#include</c> or <c>#line</c>; for an
/// included file the path is the included file's, not the includer's.
/// </summary>
/// <param name="Path">The file path as the user spelled it or the build supplied it; empty when unknown.</param>
/// <param name="Line">1-based line of the first character.</param>
/// <param name="Column">1-based column of the first character, counted in UTF-16 code units.</param>
/// <param name="Offset">0-based offset of the first character in the file's text.</param>
/// <param name="Length">Number of UTF-16 code units covered; 0 marks a point.</param>
public readonly record struct SourceSpan(string Path, int Line, int Column, int Offset = 0, int Length = 0)
{
    /// <summary>A location that carries no position, for problems that belong to no file.</summary>
    public static SourceSpan None => default;

    /// <summary>True when this span points somewhere (path and a 1-based line are set).</summary>
    public bool IsKnown => !string.IsNullOrEmpty(Path) && Line >= 1;
}
