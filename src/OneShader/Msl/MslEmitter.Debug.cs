using System;
using System.Globalization;
using OneShader.Diagnostics;
using OneShader.IR;

namespace OneShader.Msl;

// Source-level debug information: #line directives that point generated statements back at the HLSL file and line they came from,
// so Metal's compiler (-gline-tables-only, -frecord-sources) and Xcode's shader debugger show the user's source.
// Directives are only written where the position changes, always at the start of a line, and never inside an expression.
internal sealed partial class MslEmitter
{
    private SourceSpan _pendingMark;
    private (string Path, int Line) _lastMark;

    private bool DebugLines => _o.DebugInfo != DebugInfoLevel.None;

    /// <summary>Asks for a directive before the next line written, if the position differs from the last one written.</summary>
    private void Mark(SourceSpan span)
    {
        if (DebugLines && span.IsKnown)
        {
            _pendingMark = span;
        }
    }

    private void FlushMark()
    {
        var s = _pendingMark;
        _pendingMark = default;
        if (_lastMark == (s.Path, s.Line))
        {
            return;
        }

        _lastMark = (s.Path, s.Line);
        _out.Append(Directive(s));
    }

    /// <summary>The directive text for a position, newline included; empty when debug lines are off or the position is unknown.</summary>
    private string Directive(SourceSpan s)
    {
        if (!DebugLines || !s.IsKnown)
        {
            return string.Empty;
        }

        string file = DebugPaths.Normalize(s.Path, _o.DebugSourceRoot).Replace("\\", "/", StringComparison.Ordinal).Replace("\"", "%22", StringComparison.Ordinal).Replace('\n', '_').Replace('\r', '_');
        return "#line " + s.Line.ToString(CultureInfo.InvariantCulture) + " \"" + file + "\"\n";
    }

    /// <summary>Text written outside the normal order (a captured block) cannot know what directive came before it or comes after it.</summary>
    private void ForgetMark()
    {
        _lastMark = default;
        _pendingMark = default;
    }
}
