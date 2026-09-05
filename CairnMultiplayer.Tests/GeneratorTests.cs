using CairnMultiplayerMod.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

public sealed class GeneratorTests
{
    private static GeneratorDriverRunResult Generate(string code)
    {
        var source = "namespace CairnMultiplayerMod.Framework { public abstract class MultiplayerFeature {} } " + code;
        var compilation = CSharpCompilation.Create("Sample", new[] { CSharpSyntaxTree.ParseText(source) },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return CSharpGeneratorDriver.Create(new FeatureRegistryGenerator()).RunGenerators(compilation).GetRunResult();
    }

    [Theory]
    [InlineData("Il2Cpp")]
    [InlineData("Il2CppTheGameBakers.Cairn.Global")]
    [InlineData("UnityEngine")]
    public void ForbiddenNativeNamespacesAreDiagnosed(string ns)
    {
        var result = Generate($"namespace {ns} {{ public class Native {{}} }} namespace CairnMultiplayerMod.Features.Test {{ class Test {{ {ns}.Native value; }} }}");
        Assert.Contains(result.Diagnostics, d => d.Id == "CMP003");
    }

    [Theory]
    [InlineData("class Bad<T> : CairnMultiplayerMod.Framework.MultiplayerFeature {}")]
    [InlineData("class Bad : CairnMultiplayerMod.Framework.MultiplayerFeature { protected Bad() {} }")]
    public void UnconstructibleFeaturesAreRejectedWithoutEmittingInvalidRegistration(string source)
    {
        var result = Generate(source);
        Assert.Contains(result.Diagnostics, d => d.Id == "CMP001");
        Assert.DoesNotContain("new global::Bad", result.GeneratedTrees.Single().ToString());
    }

    [Fact]
    public void PartialFeatureIsRegisteredExactlyOnce()
    {
        var result = Generate("partial class Good : CairnMultiplayerMod.Framework.MultiplayerFeature {} partial class Good : CairnMultiplayerMod.Framework.MultiplayerFeature {}");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(1, result.GeneratedTrees.Single().ToString().Split("new global::Good()").Length - 1);
    }
}
