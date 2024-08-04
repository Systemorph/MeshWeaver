using System;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
namespace OpenSmc.Documentation;


public static class PdbMethods
{
    private static readonly Guid EmbeddedSource = new Guid("0E8A571B-6926-466E-B4AD-8AB04611F5FE");

    private static readonly Encoding DefaultEncoding =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public static string GetEmbeddedSource(this MetadataReader reader, DocumentHandle document)
    {
        byte[] bytes = (from handle in reader.GetCustomDebugInformation(document)
                        let cdi = reader.GetCustomDebugInformation(handle)
                        where reader.GetGuid(cdi.Kind) == EmbeddedSource
                        select reader.GetBlobBytes(cdi.Value)).SingleOrDefault();

        if (bytes == null)
        {
            return null;
        }

        int uncompressedSize = BitConverter.ToInt32(bytes, 0);
        var stream = new MemoryStream(bytes, sizeof(int), bytes.Length - sizeof(int));

        if (uncompressedSize != 0)
        {
            var decompressed = new MemoryStream(uncompressedSize);

            using (var deflater = new DeflateStream(stream, CompressionMode.Decompress))
            {
                deflater.CopyTo(decompressed);
            }

            if (decompressed.Length != uncompressedSize)
            {
                throw new InvalidDataException();
            }

            stream = decompressed;
        }

        using (stream)
        {
            return Decode(stream, DefaultEncoding);
        }
    }

    private static string Decode(MemoryStream stream, Encoding encoding)
    {
        if (stream == null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        encoding = encoding ?? DefaultEncoding;
        stream.Position = 0;

        using (var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true))
        {
            var text = reader.ReadToEnd();
            return text;
        }
    }

    public static AssemblySourceLookup GetSourcesByType(this Assembly assembly)
        => GetSourcesByType(assembly.Location);


    public static SequencePointCollection ReadMethodSourceInfo(string assemblyPath, string methodName)
    {
        using var assemblyStream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(assemblyStream);
        if (!peReader.HasMetadata)
            throw new InvalidOperationException("Assembly has no metadata.");

        var metadataReader = peReader.GetMetadataReader();
        var methodHandles = metadataReader.MethodDefinitions;

        foreach (var handle in methodHandles)
        {
            var method = metadataReader.GetMethodDefinition(handle);
            var name = metadataReader.GetString(method.Name);

            if (name.Equals(methodName, StringComparison.Ordinal))
            {
                var pdbPath = Path.ChangeExtension(assemblyPath, "pdb");

                using var pdbStream = File.OpenRead(pdbPath);
                var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
                var pdbReader = provider.GetMetadataReader();

                // Correctly obtain the MethodDebugInformation
                var methodDebugInformation = pdbReader.GetMethodDebugInformation(handle);

                if (!methodDebugInformation.SequencePointsBlob.IsNil)
                {
                    var sequencePoints = methodDebugInformation.GetSequencePoints();
                    return sequencePoints;
                }
            }
        }

        return default;
    }



    public static AssemblySourceLookup GetSourcesByType(string assemblyPath)
    {
        var ret = new Dictionary<string, string>();

        using var assemblyStream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(assemblyStream);
        if (!peReader.HasMetadata)
            throw new InvalidOperationException("Assembly has no metadata.");
        var metadataReader = peReader.GetMetadataReader();
        var pdbPath = Path.ChangeExtension(assemblyPath, "pdb");

        using var pdbStream = File.OpenRead(pdbPath);
        var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
        var pdbReader = provider.GetMetadataReader();

        var tuplesByTypeName = metadataReader.TypeDefinitions.Select(metadataReader.GetTypeDefinition)
            .SelectMany(typeDefinition =>
                typeDefinition
                    .GetMethods()
                    .Select(pdbReader.GetMethodDebugInformation)
                    .Where(method => !method.SequencePointsBlob.IsNil)
                    .SelectMany(method => method.GetSequencePoints().Select(x => x.Document))
            .Distinct()
                    .Select(docHandle =>
                    {
                        var document = pdbReader.GetDocument(docHandle);
                        return (Handle: docHandle, Doc: document,
                            TypeName: metadataReader.GetString(typeDefinition.Name),
                            DocName: pdbReader.GetString(document.Name));
                    }))
            .DistinctBy(x => x.TypeName)
            .ToArray();

        var sources = tuplesByTypeName
            .DistinctBy(x => x.Doc.Name)
            .ToDictionary(tuple => tuple.DocName,
                tuple => pdbReader.GetEmbeddedSource(tuple.Handle)
            );

        return new AssemblySourceLookup(tuplesByTypeName.ToDictionary(x => x.TypeName, x => x.DocName), sources);
    }

}

public class AssemblySourceLookup(
    Dictionary<string, string> filesByType,
    Dictionary<string, string> sources)
{
    public string GetSource(string typeName) => 
        filesByType.TryGetValue(typeName, out var name) ? sources[name] : null;
}


