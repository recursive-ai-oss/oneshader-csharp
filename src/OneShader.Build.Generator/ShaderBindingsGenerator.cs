using Microsoft.CodeAnalysis;

namespace OneShader.Build.Generator;

/// <summary>Emits C# structs and binding code for compiled shaders. Placeholder until the compiler exists.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class ShaderBindingsGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
    }
}
