using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CairnMultiplayerMod.Generators;

/// <summary>
/// Emits FeatureRegistry, the list of every class deriving from MultiplayerFeature, so that
/// adding a feature is adding a file — no shared list to remember, and no runtime reflection
/// (which we avoid under IL2CPP).
/// </summary>
[Generator]
public sealed class FeatureRegistryGenerator : IIncrementalGenerator
{
    private const string BaseTypeName = "CairnMultiplayerMod.Framework.MultiplayerFeature";

    private static readonly DiagnosticDescriptor NeedsParameterlessConstructor = new(
        id: "CMP001",
        title: "A feature must be constructible by the registry",
        messageFormat: "'{0}' must be non-generic and expose a parameterless constructor accessible from the registry",
        category: "CairnMP",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MustNotBeNested = new(
        id: "CMP002",
        title: "A feature must be a top-level class",
        messageFormat: "'{0}' derives from MultiplayerFeature but is nested inside another type, so it cannot be registered automatically",
        category: "CairnMP",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

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

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var candidates = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                transform: static (syntaxContext, _) => Describe(syntaxContext))
            .Where(static feature => feature is not null);

        context.RegisterSourceOutput(candidates.Collect(), Emit);

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

    private static FeatureCandidate Describe(GeneratorSyntaxContext context)
    {
        if (context.SemanticModel.GetDeclaredSymbol(context.Node) is not INamedTypeSymbol symbol)
            return null;
        if (symbol.IsAbstract || symbol.IsStatic)
            return null;
        if (!DerivesFromFeature(symbol))
            return null;

        return new FeatureCandidate(
            symbol.ToDisplayString(),
            symbol.Name,
            !symbol.IsGenericType && HasParameterlessConstructor(symbol),
            symbol.ContainingType is not null,
            symbol.Locations.FirstOrDefault());
    }

    private static bool DerivesFromFeature(INamedTypeSymbol symbol)
    {
        for (var current = symbol.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == BaseTypeName)
                return true;
        }
        return false;
    }

    private static bool HasParameterlessConstructor(INamedTypeSymbol symbol)
        => symbol.InstanceConstructors.Any(constructor =>
            constructor.Parameters.Length == 0 &&
            (constructor.DeclaredAccessibility == Accessibility.Public ||
             constructor.DeclaredAccessibility == Accessibility.Internal ||
             constructor.DeclaredAccessibility == Accessibility.ProtectedOrInternal));

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

    private static void Emit(SourceProductionContext context, ImmutableArray<FeatureCandidate> candidates)
    {
        var usable = new List<FeatureCandidate>();
        foreach (var candidatesForType in candidates.GroupBy(candidate => candidate.FullName))
        {
            var candidate = candidatesForType.First();
            if (candidate.IsNested)
            {
                context.ReportDiagnostic(Diagnostic.Create(MustNotBeNested, candidate.Location, candidate.Name));
                continue;
            }
            if (!candidate.HasParameterlessConstructor)
            {
                context.ReportDiagnostic(Diagnostic.Create(NeedsParameterlessConstructor, candidate.Location, candidate.Name));
                continue;
            }
            usable.Add(candidate);
        }

        // Ordered so the generated file only changes when the set of features does.
        usable.Sort(static (left, right) => string.CompareOrdinal(left.FullName, right.FullName));

        var source = new StringBuilder();
        source.AppendLine("// <auto-generated/>");
        source.AppendLine("// Emitted by FeatureRegistryGenerator — every class deriving from MultiplayerFeature.");
        source.AppendLine("// Do not edit: add a feature by adding its file, it appears here on the next build.");
        source.AppendLine("using System.Collections.Generic;");
        source.AppendLine();
        source.AppendLine("namespace CairnMultiplayerMod.Framework;");
        source.AppendLine();
        source.AppendLine("internal static class FeatureRegistry");
        source.AppendLine("{");
        source.AppendLine($"    /// <summary>The {usable.Count} feature(s) this build ships.</summary>");
        source.AppendLine("    internal static IEnumerable<MultiplayerFeature> CreateAll() => new MultiplayerFeature[]");
        source.AppendLine("    {");
        foreach (var candidate in usable)
            source.AppendLine($"        new global::{candidate.FullName}(),");
        source.AppendLine("    };");
        source.AppendLine("}");

        context.AddSource("FeatureRegistry.g.cs", source.ToString());
    }

    private sealed class FeatureCandidate
    {
        internal FeatureCandidate(string fullName, string name, bool hasParameterlessConstructor,
            bool isNested, Location location)
        {
            FullName = fullName;
            Name = name;
            HasParameterlessConstructor = hasParameterlessConstructor;
            IsNested = isNested;
            Location = location;
        }

        internal string FullName { get; }
        internal string Name { get; }
        internal bool HasParameterlessConstructor { get; }
        internal bool IsNested { get; }
        internal Location Location { get; }

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
