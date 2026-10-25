using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace OneShader.Build.Tests;

/// <summary>
/// Packs the two packages into a temporary feed and builds the sample projects with the real
/// <c>dotnet build</c>, restoring into an empty package cache. Slow (a minute or two), run as part of the full suite.
/// </summary>
public sealed class PackageFixture : IDisposable
{
    public PackageFixture()
    {
        Root = Path.Combine(Harness.RealPath(Path.GetTempPath()), "osh-pkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Feed = Path.Combine(Root, "feed");
        var (code, output) = Run("bash", [Path.Combine(Harness.RepoRoot, "tools", "pack-local.sh"), Feed], Harness.RepoRoot, Path.Combine(Root, "pack-artifacts"));
        Assert.True(code == 0, output);
    }

    public string Root { get; }

    public string Feed { get; }

    public void Dispose() => Directory.Delete(Root, recursive: true);

    /// <summary>Runs a process with its own artifacts folder and package cache; returns the exit code and combined output.</summary>
    public static (int Code, string Output) Run(string file, IEnumerable<string> args, string workingDirectory, string artifacts, string? packages = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["ONESHADER_ARTIFACTS"] = artifacts;
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        if (packages is not null)
        {
            psi.Environment["NUGET_PACKAGES"] = packages;
        }

        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        string stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout + stderr.GetAwaiter().GetResult());
    }
}

public sealed class PackageTests : IClassFixture<PackageFixture>
{
    private readonly PackageFixture _fx;

    public PackageTests(PackageFixture fx)
    {
        _fx = fx;
    }

    private static string Dotnet => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

    private (int Code, string Output) Dotnet_(string sample, string artifacts, params string[] args) =>
        PackageFixture.Run(
            Dotnet,
            args.Select(a => a == "{project}" ? Path.Combine(Harness.RepoRoot, "samples", sample, sample + ".csproj") : a),
            Path.Combine(Harness.RepoRoot, "samples", sample),
            artifacts,
            Path.Combine(_fx.Root, "packages"));

    private (int Code, string Output) Build(string sample, string artifacts, params string[] extra)
    {
        var restore = Dotnet_(sample, artifacts, "restore", "{project}", "--source", _fx.Feed, "-v", "quiet");
        Assert.True(restore.Code == 0, restore.Output);
        return Dotnet_(sample, artifacts, ["build", "{project}", "--no-restore", "-nologo", .. extra]);
    }

    private static string Find(string dir, string pattern) =>
        Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).First();

    [Fact]
    public void PrebuiltSampleBuildsRunsAndIsDeterministic()
    {
        string a = Path.Combine(_fx.Root, "art-a");
        string b = Path.Combine(_fx.Root, "art-b");
        var first = Build("Prebuilt", a);
        Assert.True(first.Code == 0, first.Output);
        Assert.Matches(new Regex(@"\b0 Warning\(s\)"), first.Output);

        // The generated program checks itself against the compiled numbers.
        var run = PackageFixture.Run(Dotnet, [Find(a, "Prebuilt.dll")], Path.GetDirectoryName(Find(a, "Prebuilt.dll"))!, a);
        Assert.True(run.Code == 0, run.Output);
        Assert.DoesNotContain("FAIL", run.Output, StringComparison.Ordinal);

        // A no-op rebuild leaves the compiled program alone.
        string dll = Find(a, "Prebuilt.dll");
        var stamp = File.GetLastWriteTimeUtc(dll);
        var again = Dotnet_("Prebuilt", a, "build", "{project}", "--no-restore", "-nologo");
        Assert.True(again.Code == 0, again.Output);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(dll));

        // A second build elsewhere produces the same generated C# and the same assembly.
        var second = Build("Prebuilt", b);
        Assert.True(second.Code == 0, second.Output);
        var generated = Directory.EnumerateFiles(a, "OneShader.*.g.cs", SearchOption.AllDirectories).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(GoldenModels.Names.Length, generated.Count);
        foreach (string? name in generated)
        {
            Assert.Equal(File.ReadAllBytes(Find(a, name!)), File.ReadAllBytes(Find(b, name!)));
        }

        // The assemblies themselves differ only in the PDB path the compiler records, which follows
        // the artifacts folder, so they are not compared. The task's own output is checked in TaskTests.
    }

    [Fact]
    public void SpriteSampleBuildsFromTheFeedAndItsGeneratedCodeMatchesReflection()
    {
        string a = Path.Combine(_fx.Root, "art-sprite-a");
        string b = Path.Combine(_fx.Root, "art-sprite-b");
        var first = Build("Sprite", a, "-p:EmitCompilerGeneratedFiles=true");
        Assert.True(first.Code == 0, first.Output);
        Assert.Matches(new Regex(@"\b0 Warning\(s\)"), first.Output);

        // The shader compiled in-process is the reference for everything the build produced.
        string shader = Path.Combine(Harness.RepoRoot, "samples", "Sprite", "Shaders", "Sprite.hlsl");
        var compiled = ShaderCompiler.Compile(File.ReadAllText(shader), shader, null, TestContext.Current.CancellationToken);
        Assert.True(compiled.Success, string.Join("\n", compiled.Diagnostics));
        var reflection = compiled.Reflection!;
        var buffer = Assert.Single(reflection.Resources, r => r.Kind == ResourceKind.ConstantBuffer);
        var vertex = Assert.Single(reflection.EntryPoints, e => e.Name == "VSMain");

        // The program prints numbers that come from the generated code.
        string dll = Find(a, "Sprite.dll");
        var run = PackageFixture.Run(Dotnet, [dll], Path.GetDirectoryName(dll)!, a);
        Assert.True(run.Code == 0, run.Output);
        var lines = run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        Assert.Contains($"constant buffer: {buffer.Layout!.Size} bytes, vertex stride: {vertex.VertexStride}", lines);
        Assert.Contains($"vertex shader: {compiled.Find("VSMain", CompileTarget.SpirV)!.Data.Length} bytes of SPIR-V", lines);
        foreach (var r in reflection.Resources)
        {
            var slot = Assert.Single(r.Slots, x => x.Target == CompileTarget.SpirV);
            Assert.Contains($"{r.Name}: set {slot.Set}, binding {slot.Binding}, {r.Kind}, used by {r.Stages}", lines);
        }

        // The generated C# carries the reflected layout.
        string generated = File.ReadAllText(Find(a, "OneShader.*.g.cs"));
        Assert.Contains($"Size = {buffer.Layout.Size})", generated, StringComparison.Ordinal);
        foreach (var member in buffer.Layout.Members)
        {
            Assert.Contains($"FieldOffset({member.Offset})", generated, StringComparison.Ordinal);
        }

        Assert.Contains($"Stride = {vertex.VertexStride}u", generated, StringComparison.Ordinal);
        foreach (var r in reflection.Resources)
        {
            var slot = r.Slots.Single(x => x.Target == CompileTarget.SpirV);
            Assert.Contains($"Set = {slot.Set}u, Binding = {slot.Binding}u", generated, StringComparison.Ordinal);
        }

        // A no-op rebuild compiles nothing: the task output and the assembly keep their timestamps.
        string blob = Find(a, "Sprite.hlsl.oneshader");
        var blobStamp = File.GetLastWriteTimeUtc(blob);
        var dllStamp = File.GetLastWriteTimeUtc(dll);
        var again = Dotnet_("Sprite", a, "build", "{project}", "--no-restore", "-nologo", "-p:EmitCompilerGeneratedFiles=true");
        Assert.True(again.Code == 0, again.Output);
        Assert.Equal(blobStamp, File.GetLastWriteTimeUtc(blob));
        Assert.Equal(dllStamp, File.GetLastWriteTimeUtc(dll));

        // A second build elsewhere gives byte-identical generated C# and compiled blobs.
        var second = Build("Sprite", b, "-p:EmitCompilerGeneratedFiles=true");
        Assert.True(second.Code == 0, second.Output);
        Assert.Equal(File.ReadAllBytes(Find(a, "OneShader.*.g.cs")), File.ReadAllBytes(Find(b, "OneShader.*.g.cs")));
        Assert.Equal(File.ReadAllBytes(blob), File.ReadAllBytes(Find(b, "Sprite.hlsl.oneshader")));
    }

    [Fact]
    public void SpriteSampleWithAllTargetsEmbedsMslAndWgslIdenticallyToTheCompiler()
    {
        string[] flags = ["-p:EmitCompilerGeneratedFiles=true", "-p:OneShaderTargets=SpirV%3BMsl%3BWgsl"];
        string a = Path.Combine(_fx.Root, "art-targets-a");
        string b = Path.Combine(_fx.Root, "art-targets-b");
        var first = Build("Sprite", a, flags);
        Assert.True(first.Code == 0, first.Output);
        Assert.Matches(new Regex(@"\b0 Warning\(s\)"), first.Output);

        string shader = Path.Combine(Harness.RepoRoot, "samples", "Sprite", "Shaders", "Sprite.hlsl");
        var compiled = ShaderCompiler.Compile(
            File.ReadAllText(shader),
            shader,
            new CompileOptions { Targets = [CompileTarget.SpirV, CompileTarget.Msl, CompileTarget.Wgsl] },
            TestContext.Current.CancellationToken);
        Assert.True(compiled.Success, string.Join("\n", compiled.Diagnostics));

        // The generated C# holds the compiler's bytes for each target, spelled out as a byte-array literal.
        string generated = File.ReadAllText(Find(a, "OneShader.*.g.cs"));
        foreach (string entry in new[] { "VSMain", "PSMain" })
        {
            string body = generated.Substring(generated.IndexOf("public static class " + entry, StringComparison.Ordinal));
            body = body.Substring(0, body.IndexOf("\n    }\n", StringComparison.Ordinal));
            foreach (var (member, target) in new[] { ("SpirV", CompileTarget.SpirV), ("Msl", CompileTarget.Msl), ("Wgsl", CompileTarget.Wgsl) })
            {
                var match = Regex.Match(body, @"ReadOnlySpan<byte> " + member + @" => new byte\[\]\s*\{(?<bytes>[^}]*)\}");
                Assert.True(match.Success, entry + "." + member + " is missing");
                byte[] embedded = Regex.Matches(match.Groups["bytes"].Value, "0x([0-9a-f]{2})").Select(m => Convert.ToByte(m.Groups[1].Value, 16)).ToArray();
                Assert.Equal(compiled.Find(entry, target)!.ToArray(), embedded);
            }
        }

        // Metal indices in the generated bindings are the ones reflection recorded.
        foreach (var r in compiled.Reflection!.Resources)
        {
            uint index = r.Slots.Single(x => x.Target == CompileTarget.Msl).Binding;
            var cls = Regex.Match(generated, @"public static class " + r.Name + @"\s*\{(?<body>[^}]*)\}");
            Assert.True(cls.Success, r.Name);
            Assert.Contains($"MslIndex = {index}u", cls.Groups["body"].Value, StringComparison.Ordinal);
        }

        Assert.Contains($"MslVertexBufferIndex = {Msl.MslBackend.VertexBufferIndex}u", generated, StringComparison.Ordinal);

        // A no-op rebuild compiles nothing; a second build elsewhere gives byte-identical C# and task output.
        string blob = Find(a, "Sprite.hlsl.oneshader");
        var blobStamp = File.GetLastWriteTimeUtc(blob);
        string dll = Find(a, "Sprite.dll");
        var dllStamp = File.GetLastWriteTimeUtc(dll);
        var again = Dotnet_("Sprite", a, ["build", "{project}", "--no-restore", "-nologo", .. flags]);
        Assert.True(again.Code == 0, again.Output);
        Assert.Equal(blobStamp, File.GetLastWriteTimeUtc(blob));
        Assert.Equal(dllStamp, File.GetLastWriteTimeUtc(dll));
        var second = Build("Sprite", b, flags);
        Assert.True(second.Code == 0, second.Output);
        Assert.Equal(File.ReadAllBytes(Find(a, "OneShader.*.g.cs")), File.ReadAllBytes(Find(b, "OneShader.*.g.cs")));
        Assert.Equal(File.ReadAllBytes(blob), File.ReadAllBytes(Find(b, "Sprite.hlsl.oneshader")));
    }

    [Fact]
    public async Task HotReloadSampleBuildsAndReportsCompatibleIncompatibleAndBrokenEdits()
    {
        string art = Path.Combine(_fx.Root, "art-hot");
        var build = Build("HotReload", art);
        Assert.True(build.Code == 0, build.Output);
        Assert.Matches(new Regex(@"\b0 Warning\(s\)"), build.Output);

        string dir = Path.Combine(_fx.Root, "hot-shader");
        Directory.CreateDirectory(dir);
        string shader = Path.Combine(dir, "Sprite.hlsl");
        string original = File.ReadAllText(Path.Combine(Harness.RepoRoot, "samples", "HotReload", "Shaders", "Sprite.hlsl"));
        File.WriteAllText(shader, original);

        string dll = Find(art, "HotReload.dll");
        var psi = new ProcessStartInfo(Dotnet) { WorkingDirectory = dir, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add(shader);
        using var process = Process.Start(psi)!;
        var lines = new System.Collections.Concurrent.BlockingCollection<string>();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lines.Add(e.Data); } };
        process.BeginOutputReadLine();
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            // Each step edits the file, then sends a line, which makes the program recompile without waiting for a file event.
            string Next(string marker)
            {
                var deadline = DateTime.UtcNow.AddSeconds(60);
                var seen = new List<string>();
                while (lines.TryTake(out string? line, Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds), TestContext.Current.CancellationToken))
                {
                    seen.Add(line);
                    if (line.Contains(marker, StringComparison.Ordinal))
                    {
                        return string.Join("\n", seen);
                    }
                }

                throw new Xunit.Sdk.XunitException("no line containing '" + marker + "'; saw:\n" + string.Join("\n", seen));
            }

            Assert.Contains("compatible: swapping in VSMain", Next("] compatible: swapping in"), StringComparison.Ordinal);

            File.WriteAllText(shader, original.Replace("float4 Tint;\n    float Time;", "float Time;\n    float4 Tint;", StringComparison.Ordinal));
            process.StandardInput.WriteLine();
            string moved = Next("Rebuild");
            Assert.Contains("incompatible with the compiled-in C# bindings:", moved, StringComparison.Ordinal);
            Assert.Contains("  Frame.Tint moved from offset 64 to 80", moved, StringComparison.Ordinal);
            Assert.Contains("  Frame.Time moved from offset 80 to 64", moved, StringComparison.Ordinal);

            File.WriteAllText(shader, original.Replace("return o;", "return o", StringComparison.Ordinal));
            process.StandardInput.WriteLine();
            string broken = Next("compile failed");
            broken += "\n" + Next("error OSH0200");
            Assert.Contains("compile failed; keeping the previous shader", broken, StringComparison.Ordinal);
            Assert.Contains("Sprite.hlsl(20,13): error OSH0200", broken, StringComparison.Ordinal);

            File.WriteAllText(shader, original.Replace("* Tint * Time;", "* Tint * (Time + 1.0);", StringComparison.Ordinal));
            process.StandardInput.WriteLine();
            Assert.Contains("compatible: swapping in VSMain", Next("] compatible: swapping in"), StringComparison.Ordinal);

            process.StandardInput.WriteLine("q");
            Assert.True(process.WaitForExit(30000), "the sample did not quit");
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        Assert.Equal("", await stderr);
    }

    [Fact]
    public void ShaderErrorsAreBuildErrorsOnTheHlslLine()
    {
        // A copy of the sample project with a broken shader: `missing` is at line 3, column 12.
        string project = Path.Combine(_fx.Root, "broken");
        Directory.CreateDirectory(Path.Combine(project, "Shaders"));
        File.WriteAllText(
            Path.Combine(project, "Broken.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
            + "<ItemGroup><PackageReference Include=\"RecursiveAI.OneShader.Build\" Version=\"0.1.0-dev.*\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(project, "Program.cs"), "System.Console.WriteLine();\n");
        string shader = Path.Combine(project, "Shaders", "Broken.hlsl");
        File.WriteAllText(shader, "[shader(\"vertex\")]\nfloat4 VSMain() : SV_Position {\n    return missing;\n}\n");
        string a = Path.Combine(_fx.Root, "art-broken");
        string packages = Path.Combine(_fx.Root, "packages");
        var restore = PackageFixture.Run(Dotnet, ["restore", "Broken.csproj", "--source", _fx.Feed, "-v", "quiet"], project, a, packages);
        Assert.True(restore.Code == 0, restore.Output);
        var result = PackageFixture.Run(Dotnet, ["build", "Broken.csproj", "--no-restore", "-nologo"], project, a, packages);
        Assert.NotEqual(0, result.Code);

        // The exact form MSBuild and IDEs parse: path(line,col): error CODE: message.
        Assert.Matches(new Regex("^" + Regex.Escape(shader) + @"\(3,12\): error OSH0300: .+", RegexOptions.Multiline), result.Output);
    }

    [Fact]
    public void ProjectReferencesWorkWithoutAPackage()
    {
        string a = Path.Combine(_fx.Root, "art-local");
        string project = Path.Combine(Harness.RepoRoot, "samples", "LocalSource", "LocalSource.csproj");
        string dir = Path.GetDirectoryName(project)!;

        // Uses the ambient package cache: the generator project needs Roslyn, which is not in the local feed.
        var build = PackageFixture.Run(Dotnet, ["build", project, "-nologo"], dir, a);
        Assert.True(build.Code == 0, build.Output);
        string dll = Find(a, "LocalSource.dll");
        var run = PackageFixture.Run(Dotnet, [dll], Path.GetDirectoryName(dll)!, a);
        Assert.True(run.Code == 0, run.Output);
    }
}
