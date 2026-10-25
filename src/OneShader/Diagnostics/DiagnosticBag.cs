using System.Collections.Generic;

namespace OneShader.Diagnostics;

/// <summary>
/// Collects the diagnostics of a compilation in the order they were reported. Each compile
/// phase receives the same bag. Not thread-safe: use one bag per compilation.
/// </summary>
public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = [];

    /// <summary>The diagnostics reported so far, in report order.</summary>
    public IReadOnlyList<Diagnostic> Items => _items;

    /// <summary>True when at least one error was reported.</summary>
    public bool HasErrors { get; private set; }

    /// <summary>The code of the note that ends a report cut off at <see cref="MaxErrors"/> errors.</summary>
    public const string TooManyErrors = "OSH0004";

    /// <summary>
    /// After this many errors the bag keeps nothing more: a broken input (a stray brace, a runaway macro) would otherwise
    /// bury the first, real error under thousands of follow-on ones and cost memory and time to produce them.
    /// </summary>
    public const int MaxErrors = 100;

    private int _errors;

    /// <summary>True once the report is cut off at <see cref="MaxErrors"/>: later diagnostics are dropped, so callers can skip work that only builds a message.</summary>
    public bool IsFull => _errors > MaxErrors;

    /// <summary>Adds a diagnostic. An error identical to the one just before it is dropped; errors past <see cref="MaxErrors"/> are dropped after one closing note.</summary>
    public void Add(Diagnostic diagnostic)
    {
        System.ArgumentNullException.ThrowIfNull(diagnostic);
        if (_errors > MaxErrors)
        {
            return;
        }

        if (diagnostic.Severity == DiagnosticSeverity.Error)
        {
            if (_items.Count > 0 && _items[^1] == diagnostic)
            {
                return;
            }

            HasErrors = true;
            if (++_errors > MaxErrors)
            {
                _items.Add(new Diagnostic(DiagnosticSeverity.Error, TooManyErrors, "More than " + MaxErrors.ToString(System.Globalization.CultureInfo.InvariantCulture) + " errors; the rest are not listed. Fix the errors above first, later ones are often caused by them.", diagnostic.Span));
                return;
            }
        }

        _items.Add(diagnostic);
    }

    /// <summary>Reports an error.</summary>
    public void Error(string code, string message, SourceSpan span) =>
        Add(new Diagnostic(DiagnosticSeverity.Error, code, message, span));

    /// <summary>Reports a warning.</summary>
    public void Warning(string code, string message, SourceSpan span) =>
        Add(new Diagnostic(DiagnosticSeverity.Warning, code, message, span));

    /// <summary>Reports an informational message.</summary>
    public void Info(string code, string message, SourceSpan span) =>
        Add(new Diagnostic(DiagnosticSeverity.Info, code, message, span));
}
