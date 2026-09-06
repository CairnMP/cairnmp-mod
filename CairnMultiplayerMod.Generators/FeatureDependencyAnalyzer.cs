using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CairnMultiplayerMod.Generators;

/// <summary>Registers CMP003 checks for feature dependencies; shares the registry's generator pipeline.</summary>
internal static class FeatureDependencyAnalyzer
{
    private static readonly DiagnosticDescriptor ForbiddenFeatureDependency = new(
        id: "CMP003",
        title: "A feature bypasses the safe game API",
        messageFormat: "'{0}' directly references '{1}'. Features must use FeatureBuilder and FeatureBuilder.Game instead.",
        category: "CairnMP",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly string[] ForbiddenNamespacePrefixes =
    {
        "CairnMultiplayer.Api",
        "CairnMultiplayerMod.Bootstrap",
        "CairnMultiplayerMod.Internal",
        "MelonLoader",
        "UnityEngine",
        "UnityEngine.UI",
        "UnityEngine.UIElements",
        "UnityEngine.EventSystems",
        "Il2Cpp",
        "TMPro",
        "Steamworks",
        "HarmonyLib",
    };

    internal static void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var forbiddenFeatureReferences = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is BaseTypeDeclarationSyntax,
                transform: static (syntaxContext, _) => DescribeForbiddenReference(syntaxContext))
            .Where(static dependency => dependency is not null);

        context.RegisterSourceOutput(forbiddenFeatureReferences,
            static (output, dependency) => output.ReportDiagnostic(Diagnostic.Create(
                ForbiddenFeatureDependency,
                dependency.Location,
                dependency.OwnerName,
                dependency.TypeName)));
    }

    private static ForbiddenDependency DescribeForbiddenReference(GeneratorSyntaxContext context)
    {
        if (context.SemanticModel.GetDeclaredSymbol(context.Node) is not INamedTypeSymbol owner)
            return null;

        var ownerNamespace = owner.ContainingNamespace?.ToDisplayString();
        if (ownerNamespace != "CairnMultiplayerMod.Features"
            && !ownerNamespace.StartsWith("CairnMultiplayerMod.Features.", System.StringComparison.Ordinal))
            return null;

        foreach (var name in context.Node.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var symbol = context.SemanticModel.GetSymbolInfo(name).Symbol;
            if (symbol is IAliasSymbol alias) symbol = alias.Target;

            var referencedType = symbol as INamedTypeSymbol ?? symbol?.ContainingType;
            var referencedNamespace = referencedType?.ContainingNamespace?.ToDisplayString();
            if (string.IsNullOrEmpty(referencedNamespace)) continue;

            foreach (var prefix in ForbiddenNamespacePrefixes)
            {
                if ((prefix == "Il2Cpp" && referencedNamespace.StartsWith(prefix, System.StringComparison.Ordinal))
                    || referencedNamespace == prefix
                    || referencedNamespace.StartsWith(prefix + ".", System.StringComparison.Ordinal))
                {
                    return new ForbiddenDependency(
                        owner.ToDisplayString(), referencedType.ToDisplayString(), name.GetLocation());
                }
            }
        }

        return null;
    }

    private sealed class ForbiddenDependency
    {
        internal ForbiddenDependency(string ownerName, string typeName, Location location)
        {
            OwnerName = ownerName;
            TypeName = typeName;
            Location = location;
        }

        internal string OwnerName { get; }
        internal string TypeName { get; }
        internal Location Location { get; }
    }
}
