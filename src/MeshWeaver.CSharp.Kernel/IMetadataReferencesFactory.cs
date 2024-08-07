using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Caching.Memory;
using MeshWeaver.CSharp.Roslyn;

namespace MeshWeaver.CSharp.Kernel;

public interface IMetadataReferencesFactory
{
    MetadataReferenceWrap GetOrAdd(string path, MetadataReferenceProperties properties = default);
}

internal class MetadataReferencesFactory : IMetadataReferencesFactory
{
    private readonly IMemoryCache cache = new MemoryCache(new MemoryCacheOptions());
    private const string KeyPrefix = nameof(MetadataReferencesFactory);

    public MetadataReferenceWrap GetOrAdd(string path, MetadataReferenceProperties properties = default)
    {
        var key = KeyPrefix + path;

        return cache.GetOrCreate(key, entry =>
        {
            var documentationProvider = XmlDocumentationProvider.CreateFromFile(Path.ChangeExtension(path, ".xml"));
            var portableExecutableReference = MetadataReference.CreateFromFile(path, default, documentationProvider);
            var metadataReferenceWrap = new MetadataReferenceWrap(portableExecutableReference);
            entry.Value = metadataReferenceWrap;
            entry.SlidingExpiration = TimeSpan.FromHours(1);
            return metadataReferenceWrap;
        });
    }
}
