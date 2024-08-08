using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace MeshWeaver.Documentation;

public record PdbDocumentationSource
: DocumentationSource<PdbDocumentationSource>
{
    public const string Pdb = nameof(Pdb);
    public override string Type => Pdb;


    public Func<string, Stream> Sources { get;  }

    public IReadOnlyDictionary<string,string> FilesByType { get;  }

    public string GetFileNameForType(string typeName)
        => FilesByType.GetValueOrDefault(typeName);

    public override Stream GetStream(string name)
    => Sources(name);


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

    public PdbDocumentationSource(string assemblyName) : base(assemblyName)
    {
        var assembly = Assembly.Load(assemblyName);
        var assemblyPath = assembly.Location;

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
                            Namespace: metadataReader.GetString(typeDefinition.Namespace),
                            DocName: GetDocumentName(pdbReader, document));
                    }))
            .Where(item => !Exclusions.Any(x => x.Invoke(item)))
            .DistinctBy(x => x.TypeName)
            .ToArray();

        FilesByType = tuplesByTypeName.ToDictionary(x => $"{x.Namespace}.{x.TypeName}", x => x.DocName);

        var sources = tuplesByTypeName
            .DistinctBy(x => x.Doc.Name)
            .ToDictionary(tuple => tuple.DocName,
                tuple => (Reader: pdbReader, tuple.Handle)
            );

        Sources = name => sources.TryGetValue(name, out var tuple)
            ? GetEmbeddedSource(tuple.Reader, tuple.Handle)
            : null;
    }

    private static Stream GetEmbeddedSource(MetadataReader reader, DocumentHandle document)
    {
        var bytes = (from handle in reader.GetCustomDebugInformation(document)
            let cdi = reader.GetCustomDebugInformation(handle)
            where reader.GetGuid(cdi.Kind) == EmbeddedSource
            select reader.GetBlobBytes(cdi.Value)).SingleOrDefault();

        if (bytes == null)
        {
            return null;
        }

        var uncompressedSize = BitConverter.ToInt32(bytes, 0);
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

            decompressed.Position = 0;
            return decompressed;
        }

        stream.Position = 0;
        return stream;
    }
    private static Stream DecodeStream(MemoryStream stream, Encoding encoding)
    {
        if (stream == null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        encoding = encoding ?? DefaultEncoding;
        stream.Position = 0;

        var decodedStream = new MemoryStream();
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        using var writer = new StreamWriter(decodedStream, encoding);
        writer.Write(reader.ReadToEnd());
        writer.Flush();
        decodedStream.Position = 0;

        return decodedStream;
    }
    private ImmutableList<Func<(DocumentHandle Handle, Document Doc, string TypeName, string Namespace, string DocName), bool>> 
        Exclusions { get; init; }
            =
            [
                x => x.TypeName.StartsWith("<"),
                x => x.TypeName.StartsWith("AutoGenerated")
            ];

    public PdbDocumentationSource WithExclusion(
        Func<(DocumentHandle Handle, Document Doc, string TypeName, string Namespace, string DocName), bool> filter)
        => this with { Exclusions = Exclusions.Add(filter) };


    private static string GetDocumentName(MetadataReader pdbReader, Document document)
    {
        var nameInPdb = pdbReader.GetString(document.Name);
        return Path.GetFileName(nameInPdb);
    }
    private static readonly Encoding DefaultEncoding =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private static readonly Guid EmbeddedSource = new Guid("0E8A571B-6926-466E-B4AD-8AB04611F5FE");


    public override string GetPath(string fullType)
    {
        var name = FilesByType.GetValueOrDefault(fullType);
        return name == null ? null : $"{Pdb}/{Id}/{name}";
    }

    public override string GetDocumentName(string documentId)
    => FilesByType.GetValueOrDefault(documentId, documentId);
}


