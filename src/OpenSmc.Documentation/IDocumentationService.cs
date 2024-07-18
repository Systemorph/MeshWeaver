using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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


    public IReadOnlyDictionary<string, string> GetSources(Assembly assembly)
    {
        // Assuming assembly file and PDB are in the same directory with the same base name
        var assemblyLocation = assembly.Location;
        var pdbPath = Path.ChangeExtension(assemblyLocation, ".pdb");

        if (!File.Exists(pdbPath))
            return null;

        using var assemblyStream = File.OpenRead(assemblyLocation);
        using var pdbStream = File.OpenRead(pdbPath);
        using var peReader = new PEReader(assemblyStream);
        var metadataReader = peReader.GetMetadataReader();

        // Initialize DiaSymReader
        // Correctly initialize an ISymUnmanagedReader using SymUnmanagedReaderFactory
        object symReaderObject;
        var guid = default(Guid);

        var pdbMeta = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
        var reader = pdbMeta.GetMetadataReader(MetadataReaderOptions.Default);

        return reader.Documents.ToDictionary(document => reader.GetString(reader.GetDocument(document).Name),
            document => reader.GetEmbeddedSource(document));

    }
    
}
