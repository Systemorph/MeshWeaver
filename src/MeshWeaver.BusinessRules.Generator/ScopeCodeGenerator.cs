using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
#pragma warning disable RS1035 // Do not use APIs banned for analyzers

namespace MeshWeaver.BusinessRules.Generator;

//[Generator]
public class ScopeCodeGenerator : ISourceGenerator
{
    public void Execute(GeneratorExecutionContext context)
    {
        //Debugger.Launch();
        // Log the start of the generator execution
        context.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor(
            "SCOPE000", "Generator Execution Started", "ScopeCodeGenerator execution started.",
            "SourceGenerator", DiagnosticSeverity.Info, true), Location.None));

        // Get the compilation
        var compilation = context.Compilation;

        // Find the IScope<,> interface
        var iScopeInterface = compilation.GetTypeByMetadataName("MeshWeaver.BusinessRules.IScope`2");
        if (iScopeInterface == null)
        {
            context.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor(
                "SCOPE001", "IScope Interface Not Found", "The IScope<TIdentity, TState> interface could not be found.",
                "SourceGenerator", DiagnosticSeverity.Warning, true), Location.None));
            return; // Interface not found
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
                var interfaceType =  typeSymbol.AllInterfaces
                    .FirstOrDefault(i => i.OriginalDefinition.Equals(iScopeInterface, SymbolEqualityComparer.Default));

                if(interfaceType == null)
                    continue;

                context.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor(
                    "SCOPE002", "Type Found", $"Found type implementing IScope: {typeSymbol.ToDisplayString()}",
                    "SourceGenerator", DiagnosticSeverity.Info, true), Location.None));
                var (className, source) = GenerateProperties(typeSymbol, interfaceType);
                generated.Add(className);
                context.AddSource($"{className}.g.cs", source);
            }
        }

        // Log the end of the generator execution
        context.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor(
            "SCOPE003", "Generator Execution Completed", "ScopeCodeGenerator execution completed.",
            "SourceGenerator", DiagnosticSeverity.Info, true), Location.None));
    }


    private (string className, string code) GenerateProperties(INamedTypeSymbol typeSymbol, INamedTypeSymbol interfaceType)
    {
        var builder = new StringBuilder();
        var namespaceName = typeSymbol.ContainingNamespace.ToDisplayString();
        var className = $"{typeSymbol.Name}Proxy";

        var identityType = interfaceType.TypeArguments[0];
        var stateType = interfaceType.TypeArguments[1];
        builder.AppendLine($"namespace {namespaceName}");
        builder.AppendLine("{");
        builder.AppendLine($"    public partial class {className} : ScopeBase<{typeSymbol}, {identityType}, {stateType}>");
        builder.AppendLine("    {");

        // Get all interface properties
        var properties = 
            typeSymbol.GetMembers().Concat(
                    typeSymbol
                        .AllInterfaces
                        .Where(i => !i.Equals(interfaceType, SymbolEqualityComparer.Default))
                        .SelectMany(i => i.GetMembers())
                )
            .OfType<IPropertySymbol>()
            .Distinct(SymbolEqualityComparer.Default);

        foreach (var property in properties.Cast<IPropertySymbol>())
        {
            var propertyName = property.Name;
            var propertyType = property.Type.ToDisplayString();
            var fieldName = $"_{char.ToLower(propertyName[0])}{propertyName.Substring(1)}";

            builder.AppendLine($"        private System.Lazy<{propertyType}> {fieldName};");

            // Default interface implementation
            builder.AppendLine($"        {propertyType} {typeSymbol}.{propertyName} => {propertyName};");

            // Property implementation with caching
            builder.AppendLine($"        public {propertyType} {propertyName}");
            builder.AppendLine("        {");
            builder.AppendLine("            get");
            builder.AppendLine("            {");
            builder.AppendLine($"                if ({fieldName} == null)");
            builder.AppendLine("                {");
            builder.AppendLine($"                    {fieldName} = new System.Lazy<{propertyType}>(() => (({typeSymbol})this).{propertyName});");
            builder.AppendLine("                }");
            builder.AppendLine($"                return {fieldName}.Value;");
            builder.AppendLine("            }");
            builder.AppendLine("        }");
        }

        builder.AppendLine("    }");
        builder.AppendLine("}");

        return (className, builder.ToString());
    }

    public void Initialize(GeneratorInitializationContext context)
    {
        // Initialization logic if needed
    }
}
