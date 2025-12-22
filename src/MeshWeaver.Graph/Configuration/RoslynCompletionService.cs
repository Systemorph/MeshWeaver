using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides Roslyn-based code completion for C# source code.
/// Uses Microsoft.CodeAnalysis.CSharp.Features for IntelliSense support.
/// </summary>
public class RoslynCompletionService : IRoslynCompletionService
{
    private readonly ILogger<RoslynCompletionService>? _logger;
    private readonly AdhocWorkspace _workspace;
    private readonly List<MetadataReference> _references;
    private readonly ConcurrentDictionary<string, DocumentId> _documentCache = new();

    private const string ProjectName = "CompletionProject";
    private const string DocumentPrefix = "CompletionDoc_";
    private const string DynamicNamespace = "MeshWeaver.Graph.Dynamic";

    // Common using statements for view/type compilation
    private const string CommonUsings = @"
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json.Serialization;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
";

    public RoslynCompletionService(ILogger<RoslynCompletionService>? logger = null)
    {
        _logger = logger;
        _references = GetDefaultReferences();

        // Create workspace with MEF composition
        var host = MefHostServices.Create(MefHostServices.DefaultAssemblies);
        _workspace = new AdhocWorkspace(host);

        // Create the project
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            ProjectName,
            ProjectName,
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            metadataReferences: _references);

        _workspace.AddProject(projectInfo);
    }

    private static List<MetadataReference> GetDefaultReferences()
    {
        var references = new List<MetadataReference>();

        // Add runtime assemblies
        var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (trustedAssemblies != null)
        {
            foreach (var path in trustedAssemblies.Split(Path.PathSeparator))
            {
                if (File.Exists(path))
                {
                    try
                    {
                        references.Add(MetadataReference.CreateFromFile(path));
                    }
                    catch
                    {
                        // Skip assemblies that can't be loaded
                    }
                }
            }
        }

        // Add specific assemblies we need for Layout types
        var additionalAssemblies = new[]
        {
            typeof(object).Assembly,
            typeof(System.ComponentModel.DataAnnotations.KeyAttribute).Assembly,
            typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute).Assembly,
            typeof(System.Linq.Enumerable).Assembly,
        };

        foreach (var assembly in additionalAssemblies)
        {
            if (!string.IsNullOrEmpty(assembly.Location) && File.Exists(assembly.Location))
            {
                try
                {
                    var reference = MetadataReference.CreateFromFile(assembly.Location);
                    if (!references.Any(r => r.Display == assembly.Location))
                        references.Add(reference);
                }
                catch
                {
                    // Skip if already added or can't be loaded
                }
            }
        }

        return references;
    }

    public async Task<IReadOnlyList<CompletionItem>> GetCompletionsAsync(
        string sourceCode,
        int position,
        CancellationToken ct = default)
    {
        try
        {
            // Wrap source code in namespace with common usings
            var wrappedCode = WrapSourceCode(sourceCode);
            var adjustedPosition = position + CommonUsings.Length + $"namespace {DynamicNamespace}\n{{\n".Length;

            // Get or create document
            var document = GetOrCreateDocument(wrappedCode);

            // Get completion service
            var completionService = CompletionService.GetService(document);
            if (completionService == null)
            {
                _logger?.LogWarning("Completion service not available");
                return Array.Empty<CompletionItem>();
            }

            // Get completions
            var completions = await completionService.GetCompletionsAsync(document, adjustedPosition, cancellationToken: ct);
            if (completions == null)
                return Array.Empty<CompletionItem>();

            // Convert to our completion items
            var result = new List<CompletionItem>();
            foreach (var item in completions.ItemsList)
            {
                result.Add(new CompletionItem
                {
                    Label = item.DisplayText,
                    InsertText = item.DisplayText,
                    Kind = MapCompletionKind(item.Tags),
                    Detail = item.InlineDescription,
                    SortText = item.SortText,
                    FilterText = item.FilterText
                });
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting completions at position {Position}", position);
            return Array.Empty<CompletionItem>();
        }
    }

    private string WrapSourceCode(string sourceCode)
    {
        return $@"{CommonUsings}
namespace {DynamicNamespace}
{{
{sourceCode}
}}";
    }

    private Document GetOrCreateDocument(string sourceCode)
    {
        var project = _workspace.CurrentSolution.Projects.First();
        var documentId = DocumentId.CreateNewId(project.Id);

        // Create a new document with the source code
        var sourceText = SourceText.From(sourceCode);
        var document = project.AddDocument($"{DocumentPrefix}{Guid.NewGuid():N}.cs", sourceText);

        // Apply the change to the workspace
        _workspace.TryApplyChanges(document.Project.Solution);

        return _workspace.CurrentSolution.GetDocument(document.Id)!;
    }

    private static CompletionItemKind MapCompletionKind(IReadOnlyList<string> tags)
    {
        if (tags.Contains("Method"))
            return CompletionItemKind.Method;
        if (tags.Contains("Property"))
            return CompletionItemKind.Property;
        if (tags.Contains("Field"))
            return CompletionItemKind.Field;
        if (tags.Contains("Class"))
            return CompletionItemKind.Class;
        if (tags.Contains("Struct"))
            return CompletionItemKind.Struct;
        if (tags.Contains("Interface"))
            return CompletionItemKind.Interface;
        if (tags.Contains("Enum"))
            return CompletionItemKind.Enum;
        if (tags.Contains("EnumMember"))
            return CompletionItemKind.EnumMember;
        if (tags.Contains("Keyword"))
            return CompletionItemKind.Keyword;
        if (tags.Contains("Namespace"))
            return CompletionItemKind.Module;
        if (tags.Contains("Local"))
            return CompletionItemKind.Variable;
        if (tags.Contains("Parameter"))
            return CompletionItemKind.Variable;
        if (tags.Contains("Event"))
            return CompletionItemKind.Event;
        if (tags.Contains("Constant"))
            return CompletionItemKind.Constant;
        if (tags.Contains("TypeParameter"))
            return CompletionItemKind.TypeParameter;
        if (tags.Contains("Snippet"))
            return CompletionItemKind.Snippet;

        return CompletionItemKind.Text;
    }
}
