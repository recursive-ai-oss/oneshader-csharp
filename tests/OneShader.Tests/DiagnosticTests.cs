using OneShader.Diagnostics;
using Xunit;

namespace OneShader.Tests;

public class DiagnosticTests
{
    [Fact]
    public void FormatsAsMsBuildError()
    {
        var bag = new DiagnosticBag();
        bag.Error("OSH0102", "unknown type 'floot'; did you mean 'float'?", new SourceSpan("shaders/a.hlsl", 12, 5, 140, 5));
        Assert.True(bag.HasErrors);
        Assert.Equal("shaders/a.hlsl(12,5): error OSH0102: unknown type 'floot'; did you mean 'float'?", bag.Items[0].ToString());
    }

    [Fact]
    public void WarningWithoutLocationAndMultilineMessage()
    {
        var bag = new DiagnosticBag();
        bag.Warning("OSH0001", "first\nsecond", SourceSpan.None);
        Assert.False(bag.HasErrors);
        Assert.Equal("OneShader: warning OSH0001: first second", bag.Items[0].ToString());
    }
}
