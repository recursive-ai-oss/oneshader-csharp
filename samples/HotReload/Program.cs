using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using HotReloadSample.Shaders;
using OneShader;
using OneShader.Reflection;

// Watches a shader file, recompiles it whenever it (or a file it includes) changes, and says whether the new
// version can replace the one compiled into this program.
//
//   dotnet run --project samples/HotReload [-- path/to/Sprite.hlsl]
//
// Edit the shader while it runs:
//   * change only function bodies   -> "compatible": a real program would hand the new SPIR-V to its renderer;
//   * move or retype a Frame member -> "incompatible", with each difference: the generated C# (Sprite.Frame,
//     Sprite.VSMain.Vertex, Sprite.Bindings) no longer matches, so the program must be rebuilt;
//   * make a mistake                -> the compiler's diagnostics are printed and the last good shader stays in use.
// Press q and Enter (or Ctrl+C) to quit.
string path = args.Length > 0 ? args[0] : DefaultShaderPath();
string full = Path.GetFullPath(path);

// What the program was built with: the reflection embedded in the generated Sprite class.
ShaderReflection builtWith = Sprite.Reflection;

var changed = new AutoResetEvent(true);
var quit = new ManualResetEvent(false);
var watchedFiles = new HashSet<string>(StringComparer.Ordinal) { full };
var watchers = new Dictionary<string, FileSystemWatcher>(StringComparer.Ordinal);
var gate = new object();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    quit.Set();
};
var input = new Thread(() =>
{
    // 'q' quits; any other line asks for a recompile, which is handy when a watcher misses an event.
    while (Console.In.ReadLine() is { } line)
    {
        if (line.Trim() == "q")
        {
            quit.Set();
            return;
        }

        changed.Set();
    }
})
{
    IsBackground = true,
};
input.Start();

int generation = 0;
Console.WriteLine($"watching {full}");
while (true)
{
    int signalled = WaitHandle.WaitAny([changed, quit]);
    if (signalled == 1)
    {
        break;
    }

    // Editors save in several steps; let them finish, then compile once.
    Thread.Sleep(150);
    changed.Reset();
    Recompile(++generation);
}

foreach (var w in watchers.Values)
{
    w.Dispose();
}

return 0;

void Recompile(int number)
{
    // CompileFile reads the file and any includes from disk, and returns problems as diagnostics.
    CompileResult result = ShaderCompiler.CompileFile(full);
    Watch(result.Files);

    if (!result.Success)
    {
        Console.WriteLine($"[{number}] compile failed; keeping the previous shader:");
        foreach (var d in result.Diagnostics)
        {
            Console.WriteLine($"  {d}");
        }

        return;
    }

    foreach (var d in result.Diagnostics)
    {
        Console.WriteLine($"  {d}");
    }

    CompatibilityReport report = InterfaceCompatibility.Compare(builtWith, result.Reflection!);
    if (!report.IsCompatible)
    {
        Console.WriteLine($"[{number}] {report}");
        return;
    }

    var sizes = new List<string>();
    foreach (var blob in result.Blobs)
    {
        sizes.Add($"{blob.EntryPoint} {blob.Data.Length} bytes");
    }

    Console.WriteLine($"[{number}] compatible: swapping in {string.Join(", ", sizes)}");
}

// Starts watching the directory of every file the compile read, once.
void Watch(IReadOnlyList<string> files)
{
    lock (gate)
    {
        foreach (string f in files)
        {
            watchedFiles.Add(f);
            string dir = Path.GetDirectoryName(f) ?? ".";
            if (watchers.ContainsKey(dir))
            {
                continue;
            }

            var w = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            void OnEvent(object sender, FileSystemEventArgs e)
            {
                lock (gate)
                {
                    if (watchedFiles.Contains(e.FullPath))
                    {
                        changed.Set();
                    }
                }
            }

            w.Changed += OnEvent;
            w.Created += OnEvent;
            w.Renamed += (s, e) => OnEvent(s, e);
            w.EnableRaisingEvents = true;
            watchers[dir] = w;
        }
    }
}

static string DefaultShaderPath([CallerFilePath] string here = "") =>
    Path.Combine(Path.GetDirectoryName(here) ?? ".", "Shaders", "Sprite.hlsl");
