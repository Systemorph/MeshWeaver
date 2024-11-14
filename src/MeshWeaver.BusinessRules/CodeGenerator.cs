using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

//[Generator]
public class CodeGenerator : ISourceGenerator
{
    public void Execute(GeneratorExecutionContext context)
    {
        // Get the compilation
        var compilation = context.Compilation;

        // Find the IScope<,> interface
        var iScopeInterface = compilation.GetTypeByMetadataName("MeshWeaver.BusinessRules.IScope`2");
        if (iScopeInterface == null)
            return; // Interface not found

        // Find all types implementing IScope<TIdentity, TState>
        var typesToAugment = new List<INamedTypeSymbol>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(tree);
            var typeDeclarations = tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>();

            foreach (var typeDecl in typeDeclarations)
            {
                var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
                if (typeSymbol == null)
                    continue;

                if (ImplementsIScope(typeSymbol, iScopeInterface))
                    typesToAugment.Add(typeSymbol);
            }
        }

        // Generate code for each type
        foreach (var typeSymbol in typesToAugment)
        {
            var source = GenerateProperties(typeSymbol);
            context.AddSource($"{typeSymbol.Name}_GeneratedProperties.cs", source);
        }
    }

    private bool ImplementsIScope(INamedTypeSymbol typeSymbol, INamedTypeSymbol iScopeInterface)
    {
        return typeSymbol.AllInterfaces.Any(i =>
            i.OriginalDefinition.Equals(iScopeInterface, SymbolEqualityComparer.Default));
    }

    private string GenerateProperties(INamedTypeSymbol typeSymbol)
    {
        var builder = new StringBuilder();
        var namespaceName = typeSymbol.ContainingNamespace.ToDisplayString();
        var className = typeSymbol.Name;

        builder.AppendLine($"namespace {namespaceName}");
        builder.AppendLine("{");
        builder.AppendLine($"    public partial class {className}");
        builder.AppendLine("    {");

        // Get all interface properties
        var properties = typeSymbol.AllInterfaces
            .SelectMany(i => i.GetMembers())
            .OfType<IPropertySymbol>()
            .Distinct(SymbolEqualityComparer.Default);

        foreach (var property in properties.Cast<IPropertySymbol>())
        {
            var propertyName = property.Name;
            var propertyType = property.Type.ToDisplayString();
            var fieldName = $"_{char.ToLower(propertyName[0])}{propertyName.Substring(1)}";
            var interfaceType = $"MeshWeaver.BusinessRules.IScope<{typeSymbol.TypeArguments[0]}, {typeSymbol.TypeArguments[1]}>";

            builder.AppendLine($"        private System.Lazy<{propertyType}> {fieldName};");

            // Default interface implementation
            builder.AppendLine($"        {propertyType} {interfaceType}.{propertyName} => {propertyName};");

            // Property implementation with caching
            builder.AppendLine($"        public {propertyType} {propertyName}");
            builder.AppendLine("        {");
            builder.AppendLine("            get");
            builder.AppendLine("            {");
            builder.AppendLine($"                if ({fieldName} == null)");
            builder.AppendLine("                {");
            builder.AppendLine($"                    {fieldName} = new System.Lazy<{propertyType}>(() => (({interfaceType})this).{propertyName});");
            builder.AppendLine("                }");
            builder.AppendLine($"                return {fieldName}.Value;");
            builder.AppendLine("            }");
            builder.AppendLine("        }");
        }

        builder.AppendLine("    }");
        builder.AppendLine("}");

        return builder.ToString();
    }

    public void Initialize(GeneratorInitializationContext context)
    {
        // Initialization logic if needed
    }
}
