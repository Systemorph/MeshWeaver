using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MeshWeaver.BusinessRules.Generator;

/// <summary>
/// Source generator that creates proxy classes for IScope interface implementations.
/// </summary>
[Generator]
public class ScopeCodeGenerator : IIncrementalGenerator
{
    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterSourceOutput(context.CompilationProvider, (context, compilation) =>
        {
            Execute(context, compilation);
        });
    }

    /// <summary>
    /// Executes the source generation process for the given compilation.
    /// </summary>
    /// <param name="context">The source production context.</param>
    /// <param name="compilation">The compilation to process.</param>
    private void Execute(SourceProductionContext context, Compilation compilation)
    {
        context.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor(
            "SCOPE000", "Generator Execution Started", "ScopeCodeGenerator execution started.",
            "SourceGenerator", DiagnosticSeverity.Info, true), Location.None));

        var iScopeInterface = compilation.GetTypeByMetadataName("MeshWeaver.BusinessRules.IScope`2");
        if (iScopeInterface == null)
        {
            context.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor(
                "SCOPE001", "IScope Interface Not Found", "The IScope<TIdentity, TState> interface could not be found.",
                "SourceGenerator", DiagnosticSeverity.Warning, true), Location.None));
            return;
        }

        var generated = new HashSet<string>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(tree);
            var typeDeclarations = tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>();

            foreach (var typeDecl in typeDeclarations)
            {
                var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
                if (typeSymbol == null || generated.Contains(typeSymbol.Name))
                    continue;

                var interfaceType = typeSymbol.AllInterfaces
                    .FirstOrDefault(i => i.OriginalDefinition.Equals(iScopeInterface, SymbolEqualityComparer.Default));

                if (interfaceType == null)
                    continue;

                if (typeSymbol.TypeKind != TypeKind.Interface)
                    continue;

                generated.Add(typeSymbol.Name);
                context.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor(
                    "SCOPE002", "Type Found", $"Found type implementing IScope: {typeSymbol.ToDisplayString()}",
                    "SourceGenerator", DiagnosticSeverity.Info, true), Location.None));

                var (className, source) = GenerateProperties(typeSymbol, interfaceType);
                generated.Add(className);
                context.AddSource($"scopes/{className}.g.cs", source);
            }
        }

        context.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor(
            "SCOPE003", "Generator Execution Completed", "ScopeCodeGenerator execution completed.",
            "SourceGenerator", DiagnosticSeverity.Info, true), Location.None));
    }

    /// <summary>
    /// Generates proxy class properties for the specified scope type and interface.
    /// </summary>
    /// <param name="typeSymbol">The type symbol representing the scope interface.</param>
    /// <param name="interfaceType">The constructed IScope interface type.</param>
    /// <returns>A tuple containing the generated class name and source code.</returns>
    private (string className, string code) GenerateProperties(INamedTypeSymbol typeSymbol, INamedTypeSymbol interfaceType)
    {
        var builder = new StringBuilder();
        var namespaceName = typeSymbol.ContainingNamespace.ToDisplayString();
        var className = $"{typeSymbol.Name}Proxy";

        var identityType = interfaceType.TypeArguments[0];
        var stateType = interfaceType.TypeArguments[1]; builder.AppendLine($"using {typeof(Lazy<>).Namespace};");
        builder.AppendLine($"namespace {namespaceName};");
        builder.AppendLine();
        builder.AppendLine("/// <inheritdoc/>");
        builder.AppendLine($"public partial class {className} : MeshWeaver.BusinessRules.ScopeBase<{typeSymbol}, {identityType}, {stateType}>, {typeSymbol}");
        builder.AppendLine("{");

        var properties = typeSymbol.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => p.DeclaredAccessibility == Accessibility.Public && !p.IsStatic)
            .ToList();

        List<string> constructorInitializations = new();
        foreach (var property in properties)
        {
            var propertyName = property.Name;
            var propertyType = property.Type.ToDisplayString();
            var fieldName = $"__{typeSymbol.Name}_{propertyName}";
            var getterName = $"__G_{fieldName}";

            builder.AppendLine($"    private static readonly System.Reflection.MethodInfo {getterName} = typeof({typeSymbol}).GetProperty(nameof({typeSymbol}.{propertyName})).GetMethod;");
            builder.AppendLine($"    private readonly Lazy<{propertyType}> {fieldName};"); constructorInitializations.Add($"       {fieldName} = new(() => Evaluate<{propertyType}>({getterName}));");

            builder.AppendLine($"    /// <inheritdoc/>");
            builder.AppendLine($"    {propertyType} {typeSymbol}.{propertyName} => {fieldName}.Value;");
        }
        builder.AppendLine();
        builder.AppendLine("    /// <inheritdoc/>");
        builder.AppendLine($"    public {className}({identityType} identity, MeshWeaver.BusinessRules.ScopeRegistry<{stateType}> state) : base(identity, state)");
        builder.AppendLine("    {");
        foreach (var initialization in constructorInitializations)
            builder.AppendLine(initialization);
        builder.AppendLine("    }");

        builder.AppendLine("}");

        return (className, builder.ToString());
    }
}
