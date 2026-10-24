using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
        Root = Path.Combine(Path.GetTempPath(), "osh-pkg-" + Guid.NewGuid().ToString("N"));
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

    private (int Code, string Output) Build(string sample, string artifacts)
    {
        var restore = Dotnet_(sample, artifacts, "restore", "{project}", "--source", _fx.Feed, "-v", "quiet");
        Assert.True(restore.Code == 0, restore.Output);
        return Dotnet_(sample, artifacts, "build", "{project}", "--no-restore", "-nologo");
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
        Assert.Equal(3, generated.Count);
        foreach (string? name in generated)
        {
            Assert.Equal(File.ReadAllBytes(Find(a, name!)), File.ReadAllBytes(Find(b, name!)));
        }

        // The assemblies themselves differ only in the PDB path the compiler records, which follows
        // the artifacts folder, so they are not compared. The task's own output is checked in TaskTests.
    }

    [Fact]
    public void ShaderErrorsAreBuildErrorsOnTheHlslLine()
    {
        string a = Path.Combine(_fx.Root, "art-sprite");
        var result = Build("Sprite", a);
        Assert.NotEqual(0, result.Code);
        string shader = Path.Combine(Harness.RepoRoot, "samples", "Sprite", "Shaders", "Sprite.hlsl");

        // The exact form MSBuild and IDEs parse: path(line,col): error CODE: message.
        Assert.Matches(new Regex("^" + Regex.Escape(shader) + @"\(1,1\): error OSH0001: .+", RegexOptions.Multiline), result.Output);
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
