using System.Collections.Concurrent;
using System.Reflection;
using OpenSmc.Messaging;

namespace OpenSmc.Documentation;

public interface IDocumentationService
{
    DocumentationContext Context { get; }
    Stream GetStream(string fullPath);
    Stream GetStream(string dataSource, string documentName);
    IReadOnlyDictionary<string, string> GetSources(Assembly assembly);
}

public class DocumentationService(IMessageHub hub) : IDocumentationService
{
    public DocumentationContext Context { get; } = hub.CreateDocumentationContext();

    public Stream GetStream(string fullPath)
        => Context.Sources.Values
            .Select(s => s.GetStream(fullPath))
            .FirstOrDefault(s => s != null);

    public Stream GetStream(string dataSource, string documentName)
    {
        if (Context.Sources.TryGetValue(dataSource, out var source))
            return source.GetStream(documentName);
        return null;
    }

    private readonly ConcurrentDictionary<Assembly, IReadOnlyDictionary<string, string>> sources = new();
    public IReadOnlyDictionary<string, string> GetSources(Assembly assembly)
    {
        return sources.GetOrAdd(assembly, _ => assembly.GetSourcesByType());
    }
    
}
