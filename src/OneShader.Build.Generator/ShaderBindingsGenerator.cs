using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace OneShader.Build.Generator;

/// <summary>Diagnostic codes the generator reports (the build task uses OSH0900 to OSH0949).</summary>
internal static class Codes
{
    public const string BadFile = "OSH0950";
    public const string NameClash = "OSH0951";
    public const string Unsupported = "OSH0952";
}

/// <summary>
/// Emits C# structs and binding code for compiled shaders. It reads the <c>.oneshader</c> files
/// the build task writes (reflection plus compiled blobs) and never loads the compiler.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ShaderBindingsGenerator : IIncrementalGenerator
{
    private const string Extension = ".oneshader";

    private static readonly DiagnosticDescriptor BadFileRule = Rule(Codes.BadFile, "Unreadable shader output");
    private static readonly DiagnosticDescriptor NameClashRule = Rule(Codes.NameClash, "Generated name clash");
    private static readonly DiagnosticDescriptor UnsupportedRule = Rule(Codes.Unsupported, "Unsupported shader member");

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var files = context.AdditionalTextsProvider
            .Where(static t => t.Path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            .Select(static (t, ct) => new Input(t.Path, t.GetText(ct)?.ToString() ?? string.Empty))
            .Collect();
        context.RegisterSourceOutput(files, static (spc, inputs) => Emit(spc, inputs));
    }

    private static DiagnosticDescriptor Rule(string id, string title) =>
        new DiagnosticDescriptor(id, title, "{0}", "OneShader", DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>Generates source for a set of shader output files. Shared with tests through the generator driver.</summary>
    private static void Emit(SourceProductionContext spc, ImmutableArray<Input> inputs)
    {
        var parsed = new List<KeyValuePair<Input, ShaderFile>>();
        foreach (var input in inputs)
        {
            var file = ShaderFile.Parse(input.Text, out string error);
            if (file is null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(BadFileRule, Location.Create(input.Path, default, default), "The shader output file is unusable (" + error + "). Rebuild the project; if it persists, delete the obj/ folder."));
                continue;
            }

            parsed.Add(new KeyValuePair<Input, ShaderFile>(input, file));
        }

        // Inputs arrive in project order; sort so output does not depend on item order.
        parsed.Sort(static (a, b) => string.CompareOrdinal(a.Value.Source, b.Value.Source));

        // The output of one variant is generated with its manifest, not on its own.
        var variantFiles = new Dictionary<string, ShaderFile>(StringComparer.Ordinal);
        foreach (var pair in parsed)
        {
            if (pair.Value.VariantKey.Length > 0)
            {
                variantFiles[pair.Value.Source + "\n" + pair.Value.VariantKey] = pair.Value;
            }
        }

        var taken = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in parsed)
        {
            var file = pair.Value;
            if (file.VariantKey.Length > 0)
            {
                continue;
            }

            bool isSet = file.VariantMode.Length > 0;
            var members = new List<ShaderFile>();
            if (isSet)
            {
                var refs = new List<VariantRef>(file.VariantRefs);
                refs.Sort(static (x, y) => x.Index.CompareTo(y.Index));
                file.VariantRefs = refs;
                foreach (var r in refs)
                {
                    if (!variantFiles.TryGetValue(file.Source + "\n" + r.Key, out var member))
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(BadFileRule, Location.Create(pair.Key.Path, default, default), "The output for a variant of '" + file.Source + "' is missing. Rebuild the project; if it persists, delete the obj/ folder."));
                        members.Clear();
                        break;
                    }

                    members.Add(member);
                }

                if (members.Count == 0 || members.Count != refs.Count)
                {
                    continue;
                }
            }

            string cls = CSharpWriter.ClassNameFor(file);
            string key = file.Namespace + "." + cls;
            if (taken.TryGetValue(key, out var other))
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    NameClashRule,
                    Location.Create(file.Source, default, default),
                    "'" + file.Source + "' and '" + other + "' both generate '" + key + "'. Rename one file, or set the ClassName or Namespace metadata on its OneShader item."));
                continue;
            }

            taken[key] = file.Source;
            string? text;
            List<GenProblem> problems;
            if (isSet)
            {
                text = CSharpWriter.GenerateVariants(file, members, out problems);
            }
            else
            {
                text = CSharpWriter.Generate(file, out problems);
            }

            foreach (var p in problems)
            {
                var rule = p.Code == Codes.NameClash ? NameClashRule : UnsupportedRule;
                spc.ReportDiagnostic(Diagnostic.Create(rule, Location.Create(file.Source, default, default), p.Message));
            }

            if (text is not null)
            {
                spc.AddSource(HintName(key), SourceText.From(text, new UTF8Encoding(false)));
            }
        }
    }

    private static string HintName(string key)
    {
        var sb = new StringBuilder("OneShader.");
        foreach (char c in key)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '_' ? c : '_');
        }

        return sb.Append(".g.cs").ToString();
    }

    private readonly struct Input : IEquatable<Input>
    {
        public Input(string path, string text)
        {
            Path = path;
            Text = text;
        }

        public string Path { get; }

        public string Text { get; }

        public bool Equals(Input other) => string.Equals(Path, other.Path, StringComparison.Ordinal) && string.Equals(Text, other.Text, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is Input i && Equals(i);

        public override int GetHashCode() => (StringComparer.Ordinal.GetHashCode(Path) * 31) + StringComparer.Ordinal.GetHashCode(Text);
    }
}
