using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace MeshWeaver.GitSync;

/// <summary>
/// Allows the mesh serializer's own <c>$type</c> marker on sealed, statically typed
/// nested records during a strict repository import. Sealed records have no STJ
/// polymorphism metadata, so without this property the import mistakes a valid
/// discriminator for an unknown authored field. Every other member remains subject
/// to <see cref="JsonSerializerOptions.UnmappedMemberHandling"/>.
/// </summary>
internal sealed class StrictImportTypeInfoResolver(IJsonTypeInfoResolver inner) : IJsonTypeInfoResolver
{
    /// <inheritdoc />
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        var info = inner.GetTypeInfo(type, options);
        if (info is null || !type.IsSealed || info.Kind != JsonTypeInfoKind.Object
            || info.PolymorphismOptions is not null)
            return info;

        var discriminator = info.CreateJsonPropertyInfo(typeof(string), "$type");
        discriminator.Set = (_, _) => { };
        discriminator.ShouldSerialize = (_, _) => false;
        info.Properties.Add(discriminator);
        return info;
    }
}
