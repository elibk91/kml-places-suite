using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace KmlSuite.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class KmlSuiteArchitectureAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor ConcreteRuntimeDependencyRule = new(
        "KS1001",
        "Inject proxied runtime collaborators by interface",
        "Parameter '{0}' injects concrete runtime collaborator '{1}'. Inject its interface instead.",
        "Architecture",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor TracedRegistrationRule = new(
        "KS1002",
        "Register proxied runtime services with AddTracedSingleton",
        "Service registration for '{0}' should use AddKmlSuiteTracing() with AddTracedSingleton(...), not plain AddSingleton(...).",
        "Architecture",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DirectConstructionRule = new(
        "KS1003",
        "Do not directly construct proxied runtime services",
        "Type '{0}' is a proxied runtime service and should not be constructed directly here.",
        "Architecture",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NullForgivingRule = new(
        "KS1004",
        "Do not use the null-forgiving operator",
        "Avoid the null-forgiving operator. Make the null state explicit instead.",
        "Safety",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly ImmutableHashSet<string> ProxiedImplementationTypes = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "ArcGeometryExtractorApp",
        "KmlConsoleRunner",
        "KmlGenerator.Core.Services.KmlGenerationService",
        "KmlTilerRunner",
        "LocationAssemblerRunner",
        "MasterListBuilderRunner",
        "PlacesGatherer.Console.Secrets.LocalConfigurationSecretProvider",
        "PlacesGatherer.Console.Secrets.SecretProviderFactory",
        "PlacesGatherer.Console.Services.GooglePlacesClient",
        "PlacesGatherer.Console.Services.PlaceNameNormalizer",
        "PlacesGatherer.Console.Services.PlacesSearchExpander",
        "PlacesGathererRunner");

    private static readonly ImmutableHashSet<string> ProxiedInterfaceTypes = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "IArcGeometryExtractorApp",
        "IKmlConsoleApp",
        "IKmlTilerApp",
        "KmlGenerator.Core.Services.IKmlGenerationService",
        "LocationAssembler.Console.ILocationAssemblerApp",
        "MasterListBuilder.Console.IMasterListBuilderApp",
        "PlacesGatherer.Console.IPlacesGathererApp",
        "PlacesGatherer.Console.Secrets.ISecretProvider",
        "PlacesGatherer.Console.Secrets.ISecretProviderFactory",
        "PlacesGatherer.Console.Services.IGooglePlacesClient",
        "PlacesGatherer.Console.Services.IPlaceNameNormalizer",
        "PlacesGatherer.Console.Services.IPlacesSearchExpander");

    private static readonly ImmutableHashSet<string> RuntimeServiceFiles = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "ArcGeometryExtractor.Console/Program.cs",
        "KmlGenerator.Console/Program.cs",
        "KmlGenerator.Core/Services/KmlGenerationService.cs",
        "KmlTiler.Console/Program.cs",
        "LocationAssembler.Console/Program.cs",
        "MasterListBuilder.Console/Program.cs",
        "PlacesGatherer.Console/Program.cs",
        "PlacesGatherer.Console/Secrets/LocalConfigurationSecretProvider.cs",
        "PlacesGatherer.Console/Secrets/SecretProviderFactory.cs",
        "PlacesGatherer.Console/Services/GooglePlacesClient.cs",
        "PlacesGatherer.Console/Services/PlaceNameNormalizer.cs",
        "PlacesGatherer.Console/Services/PlacesSearchExpander.cs");

    private static readonly ImmutableHashSet<string> CompositionRootFiles = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "ArcGeometryExtractor.Console/Program.cs",
        "KmlGenerator.Api/Program.cs",
        "KmlGenerator.Console/Program.cs",
        "KmlTiler.Console/Program.cs",
        "LocationAssembler.Console/Program.cs",
        "MasterListBuilder.Console/Program.cs",
        "PlacesGatherer.Console/Program.cs");

    private static readonly ImmutableDictionary<string, ImmutableHashSet<string>> ApprovedDirectConstructionFiles =
        ImmutableDictionary<string, ImmutableHashSet<string>>.Empty.Add(
            "PlacesGatherer.Console/Secrets/SecretProviderFactory.cs",
            ImmutableHashSet.Create(StringComparer.Ordinal, "PlacesGatherer.Console.Secrets.LocalConfigurationSecretProvider"));

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [
            ConcreteRuntimeDependencyRule,
            TracedRegistrationRule,
            DirectConstructionRule,
            NullForgivingRule
        ];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(AnalyzeAddSingletonInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSymbolAction(AnalyzeConstructor, SymbolKind.Method);
        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeNullForgivingOperator, SyntaxKind.SuppressNullableWarningExpression);
    }

    private static void AnalyzeConstructor(SymbolAnalysisContext context)
    {
        if (context.Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } constructor)
        {
            return;
        }

        var filePath = GetRelativePath(constructor.Locations);
        if (filePath is null || !RuntimeServiceFiles.Contains(filePath))
        {
            return;
        }

        foreach (var parameter in constructor.Parameters)
        {
            var parameterType = NormalizeTypeName(parameter.Type);
            if (!ProxiedImplementationTypes.Contains(parameterType))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                ConcreteRuntimeDependencyRule,
                parameter.Locations.FirstOrDefault(),
                parameter.Name,
                parameterType));
        }
    }

    private static void AnalyzeAddSingletonInvocation(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not InvocationExpressionSyntax invocation)
        {
            return;
        }

        var filePath = GetRelativePath(invocation.SyntaxTree.FilePath);
        if (filePath is null || !CompositionRootFiles.Contains(filePath))
        {
            return;
        }

        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "AddSingleton" } memberAccess)
        {
            return;
        }

        if (memberAccess.Name is not GenericNameSyntax genericName)
        {
            return;
        }

        foreach (var typeArgument in genericName.TypeArgumentList.Arguments)
        {
            var typeInfo = context.SemanticModel.GetTypeInfo(typeArgument, context.CancellationToken).Type;
            var typeName = NormalizeTypeName(typeInfo);
            if (!ProxiedImplementationTypes.Contains(typeName) && !ProxiedInterfaceTypes.Contains(typeName))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                TracedRegistrationRule,
                typeArgument.GetLocation(),
                typeName));
        }
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not ObjectCreationExpressionSyntax objectCreation)
        {
            return;
        }

        var createdType = NormalizeTypeName(context.SemanticModel.GetTypeInfo(objectCreation, context.CancellationToken).Type);
        if (!ProxiedImplementationTypes.Contains(createdType))
        {
            return;
        }

        var filePath = GetRelativePath(objectCreation.SyntaxTree.FilePath);
        if (filePath is null || IsTestFile(filePath))
        {
            return;
        }

        if (ApprovedDirectConstructionFiles.TryGetValue(filePath, out var allowedTypes) && allowedTypes.Contains(createdType))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            DirectConstructionRule,
            objectCreation.Type.GetLocation(),
            createdType));
    }

    private static void AnalyzeNullForgivingOperator(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not PostfixUnaryExpressionSyntax expression
            || expression.Kind() != SyntaxKind.SuppressNullableWarningExpression
            || IsGeneratedFile(expression.SyntaxTree.FilePath))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            NullForgivingRule,
            expression.OperatorToken.GetLocation()));
    }

    private static string NormalizeTypeName(ITypeSymbol? typeSymbol) =>
        typeSymbol?.ToDisplayString().Replace("global::", string.Empty) ?? string.Empty;

    private static string? GetRelativePath(ImmutableArray<Location> locations)
    {
        foreach (var location in locations)
        {
            var path = GetRelativePath(location.SourceTree?.FilePath);
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string? GetRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var nonNullablePath = path;
        if (nonNullablePath is null)
        {
            return null;
        }

        var normalized = nonNullablePath.Replace('\\', '/');
        const string marker = "/kml-places-suite/";
        var markerIndex = normalized.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            return normalized.Substring(markerIndex + marker.Length);
        }

        return Path.GetFileName(normalized);
    }

    private static bool IsGeneratedFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        return path.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase)
               || path.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTestFile(string filePath) =>
        filePath.StartsWith("KmlGenerator.Tests/", StringComparison.OrdinalIgnoreCase)
        || filePath.StartsWith("PlacesGatherer.Console.Tests/", StringComparison.OrdinalIgnoreCase);
}
