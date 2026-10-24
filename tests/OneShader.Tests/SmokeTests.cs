using OneShader;
using Xunit;

namespace OneShader.Tests;

public class SmokeTests
{
    [Fact]
    public void LibraryLoads() => Assert.Equal("OneShader", OneShaderInfo.Name);
}
