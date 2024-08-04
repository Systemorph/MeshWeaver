using System.Collections.Concurrent;
using System.Reflection;
using OpenSmc.Messaging;

namespace OpenSmc.Documentation;

public interface IDocumentationService
{
    DocumentationContext Context { get; }
    Stream GetStream(string fullPath);
    Stream GetStream(string dataSource, string documentName);
    AssemblySourceLookup GetSources(Assembly assembly) => GetSources(assembly.Location);
    AssemblySourceLookup GetSources(string path);
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

    private readonly ConcurrentDictionary<string, AssemblySourceLookup> sources = new();
    public AssemblySourceLookup GetSources(string path)
    {
        return sources.GetOrAdd(path, PdbMethods.GetSourcesByType);
    }
    
}
