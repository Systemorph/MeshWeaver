using System.Collections.Concurrent;
using System.Text.Json;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Process-wide (mesh singleton) map from a dynamically-compiled NodeType's content
/// <c>$type</c> discriminator (its CLR short/full name) — and, where known, its NodeType
/// path — to the concrete CLR <see cref="Type"/>.
///
/// <para><b>Why this exists.</b> The <c>$type</c> → CLR-<see cref="Type"/> mapping for a
/// dynamically-compiled NodeType was registered ONLY as a per-hub side effect of
/// <c>MeshDataSource.WithContentType</c> (which runs at the per-node hub's cold
/// activation, into that hub's <c>JsonSerializerOptions</c>). System.Text.Json freezes each
/// hub's options on first use, and <c>PolymorphicTypeInfoResolver</c> deliberately refuses to
/// auto-adopt collectible-assembly (dynamic-node) types — so any hub whose options were frozen
/// without the registration (notably the domain-agnostic mesh-node cache hub) deserialises that
/// content to a bare <see cref="JsonElement"/>. After a GitSync re-import into a RUNNING process
/// the content re-materialises as <see cref="JsonElement"/> and every <c>Content is T</c> /
/// <c>as T</c> silently fails (pages render empty) until a manual recycle re-activates the hub.</para>
///
/// <para>This registry is the ONE mesh-wide source of truth for that mapping, independent of any
/// hub's frozen options: populated once (at <c>WithContentType</c>) and retained for the process
/// lifetime, it lets the degrade seams (<c>MeshNodeStreamCache</c>, <c>EnsureTypedContent</c>)
/// recover the concrete type on the ALREADY-degraded path — no hot-path cost, no dependency on
/// per-hub option ordering. Registered in DI exactly like <c>IAssemblyStore</c> so it is reachable
/// from every hub's <see cref="System.IServiceProvider"/>, including the cache hub.</para>
/// </summary>
public interface IMeshContentTypeRegistry
{
    /// <summary>
    /// Records <paramref name="contentType"/> under its short name and full name (both are valid
    /// <c>$type</c> discriminators), and — when supplied — under its <paramref name="nodeTypePath"/>.
    /// Idempotent; last-writer-wins on a short-name collision (the same accepted risk the rest of the
    /// framework's short-name <c>$type</c> resolution already carries).
    /// </summary>
    void Register(Type contentType, string? nodeTypePath = null);

    /// <summary>Resolves a <c>$type</c> discriminator (short or full name) to its CLR type.</summary>
    bool TryResolveByDiscriminator(string discriminator, out Type contentType);

    /// <summary>Resolves a NodeType path to its content CLR type, when one was registered.</summary>
    bool TryResolveByNodeType(string nodeTypePath, out Type contentType);

    /// <summary>
    /// Best-effort recovery of a degraded <see cref="JsonElement"/> whose <c>$type</c> names a
    /// registered content type: reads the discriminator, resolves the concrete CLR type and
    /// deserialises <paramref name="content"/> to it using <paramref name="options"/> (the concrete
    /// target is passed explicitly, so the caller hub's registry need not know the type). Returns
    /// <c>null</c> when the element carries no resolvable <c>$type</c> or deserialisation fails —
    /// leaving the caller to keep the existing untyped-JsonElement warning for the genuinely
    /// unresolvable case.
    /// </summary>
    object? TryRecover(JsonElement content, JsonSerializerOptions options);
}

/// <summary>
/// Default <see cref="IMeshContentTypeRegistry"/>: two lock-free concurrent maps
/// (discriminator → type, NodeType path → type). No mutable static state; one instance per mesh,
/// held as a DI singleton for the process lifetime.
/// </summary>
public sealed class MeshContentTypeRegistry : IMeshContentTypeRegistry
{
    private readonly ConcurrentDictionary<string, Type> _byDiscriminator = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Type> _byNodeType = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public void Register(Type contentType, string? nodeTypePath = null)
    {
        if (contentType is null)
            return;
        _byDiscriminator[contentType.Name] = contentType;
        if (contentType.FullName is { } fullName)
            _byDiscriminator[fullName] = contentType;
        if (!string.IsNullOrEmpty(nodeTypePath))
            _byNodeType[nodeTypePath] = contentType;
    }

    /// <inheritdoc />
    public bool TryResolveByDiscriminator(string discriminator, out Type contentType)
    {
        if (!string.IsNullOrEmpty(discriminator) && _byDiscriminator.TryGetValue(discriminator, out var t))
        {
            contentType = t;
            return true;
        }
        contentType = null!;
        return false;
    }

    /// <inheritdoc />
    public bool TryResolveByNodeType(string nodeTypePath, out Type contentType)
    {
        if (!string.IsNullOrEmpty(nodeTypePath) && _byNodeType.TryGetValue(nodeTypePath, out var t))
        {
            contentType = t;
            return true;
        }
        contentType = null!;
        return false;
    }

    /// <inheritdoc />
    public object? TryRecover(JsonElement content, JsonSerializerOptions options)
    {
        if (content.ValueKind != JsonValueKind.Object
            || !content.TryGetProperty("$type", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.String)
            return null;

        var discriminator = typeProp.GetString();
        if (discriminator is null || !TryResolveByDiscriminator(discriminator, out var contentType))
            return null;

        try
        {
            // Deserialise to the CONCRETE type explicitly: STJ maps the JSON to its properties and
            // ignores the stale $type member, so recovery works regardless of whether `options`'
            // (frozen) registry knows the type — exactly the ContentAs<T> JsonElement contract.
            return content.Deserialize(contentType, options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }
}
