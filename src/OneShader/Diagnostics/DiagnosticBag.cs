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

    /// <summary>Adds a diagnostic.</summary>
    public void Add(Diagnostic diagnostic)
    {
        System.ArgumentNullException.ThrowIfNull(diagnostic);
        _items.Add(diagnostic);
        if (diagnostic.Severity == DiagnosticSeverity.Error)
        {
            HasErrors = true;
        }
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
