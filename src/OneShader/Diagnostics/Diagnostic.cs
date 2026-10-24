using System;
using System.Globalization;
using System.Text;

namespace OneShader.Diagnostics;

/// <summary>How serious a diagnostic is. Only errors fail a compilation.</summary>
public enum DiagnosticSeverity
{
    /// <summary>Information that needs no action.</summary>
    Info = 0,

    /// <summary>Suspicious code that still compiles.</summary>
    Warning = 1,

    /// <summary>The shader cannot be compiled.</summary>
    Error = 2,
}

/// <summary>
/// One problem found while compiling. Codes are <c>OSH</c> plus four digits (for example
/// <c>OSH0102</c>), are stable, and are never reused for a different problem.
/// </summary>
/// <param name="Severity">How serious the problem is.</param>
/// <param name="Code">The stable code.</param>
/// <param name="Message">What is wrong and what to do about it.</param>
/// <param name="Span">Where it is; <see cref="SourceSpan.None"/> when it belongs to no file.</param>
public sealed record Diagnostic(DiagnosticSeverity Severity, string Code, string Message, SourceSpan Span)
{
    /// <summary>
    /// Formats the diagnostic the way MSBuild and IDEs recognize it:
    /// <c>path(line,col): error CODE: message</c>. Without a location the origin is
    /// <c>OneShader</c>. Line breaks in the message become spaces, because MSBuild reads one line.
    /// </summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Span.IsKnown)
        {
            sb.Append(Span.Path).Append('(')
              .Append(Span.Line.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(Math.Max(Span.Column, 1).ToString(CultureInfo.InvariantCulture)).Append(')');
        }
        else
        {
            sb.Append("OneShader");
        }

        sb.Append(": ")
          .Append(Severity switch
          {
              DiagnosticSeverity.Error => "error",
              DiagnosticSeverity.Warning => "warning",
              _ => "info",
          })
          .Append(' ').Append(Code).Append(": ");

        foreach (char c in Message)
        {
            sb.Append(c is '\r' or '\n' ? ' ' : c);
        }

        return sb.ToString();
    }
}
