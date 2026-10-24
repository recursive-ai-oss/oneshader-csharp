using System;
using System.Collections.Generic;
using System.IO;
using OneShader.Lexing;

namespace OneShader.Preprocessing;

/// <summary>
/// Finds the text for an <c>#include</c>. The build task passes a file-system resolver with the project's
/// include paths; the runtime API can pass an in-memory one. Implementations should return null for a name
/// they cannot find rather than throw.
/// </summary>
public interface IIncludeResolver
{
    /// <summary>Resolves one include.</summary>
    /// <param name="name">The name between the quotes or angle brackets, as written.</param>
    /// <param name="isAngled">True for <c>#include &lt;name&gt;</c>, false for <c>#include "name"</c>.</param>
    /// <param name="includer">The file containing the directive.</param>
    /// <param name="searched">The resolver appends a description of every place it looked (directories), for the "not found" message.</param>
    /// <returns>The file, or null when it was not found.</returns>
    SourceFile? Resolve(string name, bool isAngled, SourceFile includer, IList<string> searched);
}

/// <summary>
/// Shared lookup order: for <c>"name"</c> the including file's directory first, then the include
/// directories in order; for <c>&lt;name&gt;</c> the include directories, then (as a convenience for
/// shaders written for other compilers) the including file's directory. Backslashes in names are
/// treated as <c>/</c>, so shaders written on Windows work everywhere.
/// </summary>
public abstract class SearchPathIncludeResolver : IIncludeResolver
{
    private readonly string[] _directories;

    /// <summary>Creates a resolver searching <paramref name="includeDirectories"/> in order.</summary>
    protected SearchPathIncludeResolver(IEnumerable<string>? includeDirectories)
    {
        _directories = includeDirectories is null ? [] : [.. includeDirectories];
    }

    /// <inheritdoc/>
    public SourceFile? Resolve(string name, bool isAngled, SourceFile includer, IList<string> searched)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(includer);
        ArgumentNullException.ThrowIfNull(searched);
        string rel = name.Replace('\\', '/');
        if (rel.Length == 0)
        {
            return null;
        }

        string includerDir = DirectoryOf(includer.Path);
        if (!isAngled)
        {
            SourceFile? f = TryDirectory(includerDir, rel, searched);
            if (f is not null)
            {
                return f;
            }
        }

        foreach (string dir in _directories)
        {
            SourceFile? f = TryDirectory(dir, rel, searched);
            if (f is not null)
            {
                return f;
            }
        }

        return isAngled ? TryDirectory(includerDir, rel, searched) : null;
    }

    /// <summary>Reads a file; returns false when it does not exist or cannot be read. <paramref name="identity"/> must be equal for the same physical file.</summary>
    protected abstract bool TryRead(string path, out string text, out string identity);

    private SourceFile? TryDirectory(string dir, string rel, IList<string> searched)
    {
        string shown = dir.Length == 0 ? "." : dir;
        if (!searched.Contains(shown))
        {
            searched.Add(shown);
        }

        string path = dir.Length == 0 ? rel : dir.EndsWith('/') ? dir + rel : dir + "/" + rel;
        return TryRead(path, out string text, out string identity) ? new SourceFile(path, text, identity) : null;
    }

    private static string DirectoryOf(string path)
    {
        int i = path.LastIndexOfAny(['/', '\\']);
        return i < 0 ? string.Empty : path[..i];
    }
}

/// <summary>Resolves includes from the file system.</summary>
public sealed class FileSystemIncludeResolver : SearchPathIncludeResolver
{
    /// <summary>Creates a resolver. With no directories only the including file's own directory is searched.</summary>
    public FileSystemIncludeResolver(IEnumerable<string>? includeDirectories = null)
        : base(includeDirectories)
    {
    }

    /// <inheritdoc/>
    protected override bool TryRead(string path, out string text, out string identity)
    {
        text = string.Empty;
        identity = path;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            identity = Path.GetFullPath(path);
            text = File.ReadAllText(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>Resolves includes from a dictionary of path to text, for tests, tools and hot reload.</summary>
public sealed class InMemoryIncludeResolver : SearchPathIncludeResolver
{
    private readonly IReadOnlyDictionary<string, string> _files;

    /// <summary>Creates a resolver. Keys use <c>/</c> separators; <c>.</c> and <c>..</c> segments are normalized when looking up.</summary>
    public InMemoryIncludeResolver(IReadOnlyDictionary<string, string> files, IEnumerable<string>? includeDirectories = null)
        : base(includeDirectories)
    {
        ArgumentNullException.ThrowIfNull(files);
        _files = files;
    }

    /// <inheritdoc/>
    protected override bool TryRead(string path, out string text, out string identity)
    {
        identity = Normalize(path);
        if (_files.TryGetValue(path, out string? t) || _files.TryGetValue(identity, out t))
        {
            text = t;
            return true;
        }

        text = string.Empty;
        return false;
    }

    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (string seg in path.Split('/'))
        {
            if (seg.Length == 0 || seg == ".")
            {
                continue;
            }

            if (seg == ".." && parts.Count > 0 && parts[^1] != "..")
            {
                parts.RemoveAt(parts.Count - 1);
            }
            else
            {
                parts.Add(seg);
            }
        }

        return string.Join('/', parts);
    }
}
