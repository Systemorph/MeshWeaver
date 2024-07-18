using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.DiaSymReader;
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
    
    //private string GetSource(Assembly assembly, string member)
        //{
        //    // Assuming assembly file and PDB are in the same directory with the same base name
        //    var assemblyLocation = assembly.Location;
        //    var pdbPath = Path.ChangeExtension(assemblyLocation, ".pdb");

        //    if (!File.Exists(pdbPath))
        //        return null;

        //    using var assemblyStream = File.OpenRead(assemblyLocation);
        //    using var pdbStream = File.OpenRead(pdbPath);
        //    using var peReader = new PEReader(assemblyStream);
        //    var metadataReader = peReader.GetMetadataReader();

        //    // Initialize DiaSymReader
        //    // Correctly initialize an ISymUnmanagedReader using SymUnmanagedReaderFactory
        //    object symReaderObject;
        //    var guid = default(Guid);
        //    var metadataProvider = new MyMetadataProvider();

        //    var symbolReader = SymUnmanagedReaderFactory.CreateReader<ISymUnmanagedReader3>(
        //        pdbStream,
        //        metadataProvider,
        //        SymUnmanagedReaderCreationOptions.Default);

        //    if(symbolReader == null)
        //        return null;

        //    // Find the method definition handle for the specified member
        //    var methodHandle = metadataReader.MethodDefinitions.FirstOrDefault(handle =>
        //    {
        //        var methodDefinition = metadataReader.GetMethodDefinition(handle);
        //        var methodName = metadataReader.GetString(methodDefinition.Name);
        //        return methodName == member;
        //    });

        //    if (methodHandle.IsNil)
        //        return null;

        //    // Use DiaSymReader to find the document (source file) for the method
        //    var methodToken = MetadataTokens.GetToken(methodHandle);
        //    ISymUnmanagedMethod method;
        //    symbolReader.GetMethod(methodToken, out method);

        //    if (method == null)
        //        return null;

        //    // Get sequence points, which include file references
        //    method.GetSequencePointCount(out int count);
        //    // Assuming 'method' is an ISymUnmanagedMethod obtained as shown in your snippet
        //    var offsets = new int[count];
        //    var lines = new int[count];
        //    var columns = new int[count];
        //    var endLines = new int[count];
        //    var endColumns = new int[count];
        //    var documents = new ISymUnmanagedDocument[count];

        //    // Retrieve sequence points
        //    method.GetSequencePoints(count, out count, offsets, documents, lines, columns, endLines, endColumns);

        //    // Now, you have arrays filled with sequence point data.
        //    // For example, to access the document for the first sequence point:
        //    if (count > 0 && documents[0] != null)
        //    {
        //        // Get the URL (file path) of the document
        //        int urlLength;
        //        documents[0].GetUrl(0, out urlLength, null); // Get the length of the URL
        //        char[] url = new char[urlLength];
        //        documents[0].GetUrl(urlLength, out urlLength, url);
        //        var documentPath = new string(url, 0, urlLength - 1);

        //        // Assuming the document path points to a source file on disk
        //        if (File.Exists(documentPath))
        //        {
        //            // Read and return the source code
        //            return File.ReadAllText(documentPath);
        //        }
        //    }
        //    return null;
        //}
    }

// Example implementation of ISymReaderMetadataProvider
// You need to provide implementations for the methods based on your application's needs
public class MyMetadataProvider : ISymReaderMetadataProvider
{
    public unsafe bool TryGetStandaloneSignature(int standaloneSignatureToken, out byte* signature, out int length)
    {
        // Implement based on your application's needs
        signature = default;
        length = 0;
        return false;
    }

    public bool TryGetTypeDefinitionInfo(int typeDefinitionToken, out string namespaceName, out string typeName, out TypeAttributes attributes)
    {
        // Implement based on your application's needs
        namespaceName = null;
        typeName = null;
        attributes = default;
        return false;
    }

    public bool TryGetTypeReferenceInfo(int typeReferenceToken, out string namespaceName, out string typeName)
    {
        // Implement based on your application's needs
        namespaceName = null;
        typeName = null;
        return false;
    }
}
