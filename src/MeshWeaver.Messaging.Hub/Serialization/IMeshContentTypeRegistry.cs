using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

// 🚨 Namespace deliberately NOT MeshWeaver.Messaging.Serialization (where the file now lives).
// The type used to sit in MeshWeaver.Mesh.Contract, but MeshWeaver.Mesh.Contract REFERENCES
// MeshWeaver.Messaging.Hub — so the wire-level degrade seam (ObjectPolymorphicConverter) could not
// see it, which is exactly the hole this registry exists to plug. Moving the FILE down (it has zero
// mesh dependencies — Type, JsonElement, JsonSerializerOptions) while KEEPING the namespace means
// every existing `using MeshWeaver.Mesh.Services;` still resolves. That matters more than tidiness
// here: NodeType source stored in the mesh is compiled at RUNTIME and is invisible to `dotnet build`,
// so renaming a public namespace is a breaking change no build or test in this repo can catch.
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
/// lifetime, it lets the degrade seams recover the concrete type on the ALREADY-degraded path — no
/// hot-path cost, no dependency on per-hub option ordering. Registered in DI exactly like
/// <c>IAssemblyStore</c> so it is reachable from every hub's <see cref="System.IServiceProvider"/>,
/// including the cache hub.</para>
///
/// <para><b>The three seams that consult it</b>, outermost first:
/// <list type="number">
/// <item><c>ObjectPolymorphicConverter.ReadObject</c> — the WIRE, and the broadest: EVERY hub that
/// receives such a node passes through it. It was the last one wired, which is why prod kept logging
/// <c>"Received '$type':'PluginContent' which is NOT registered in this (receiving) hub"</c> for every
/// plugin content type at once while the two seams below were already healing.</item>
/// <item><c>MeshNodeStreamCache</c> (GetStream + GetQuery) — the cache hub's read boundary.</item>
/// <item><c>MeshNodeStreamHandle.EnsureTypedContent</c> — the per-handle read boundary.</item>
/// </list>
/// None of them ADOPT the type into a hub's <c>ITypeRegistry</c>: they deserialise to the concrete
/// type explicitly, so a per-compile collectible identity never becomes a hub's long-lived answer for
/// that discriminator (see <c>PolymorphicTypeInfoResolver.WarnCollectibleSerialization</c>).</para>
///
/// <para>🚨 <b>A discriminator is a package-local name, so the map answers only where that name is
/// mesh-wide unique.</b> Two packages may each define a <c>Currency</c>; when both have registered,
/// the name resolves to NEITHER (see <see cref="TryResolveByDiscriminator"/>) and the content stays
/// a <see cref="JsonElement"/> for any hub that has not registered the type itself. The exact route
/// — <see cref="TryResolveByNodeType"/>, keyed on the node's own NodeType path — is never ambiguous
/// and is the durable answer for a caller that has the node in hand.</para>
/// </summary>
public interface IMeshContentTypeRegistry
{
    /// <summary>
    /// Records <paramref name="contentType"/> under its short name and full name (both are valid
    /// <c>$type</c> discriminators), and — when supplied — under its <paramref name="nodeTypePath"/>.
    /// Idempotent, and last-writer-wins for a REBUILD of the same declaration (a recompiled NodeType
    /// mints a new CLR identity under the same assembly name).
    ///
    /// <para>🚨 A discriminator claimed by two DIFFERENT declarations becomes
    /// <b>ambiguous</b> and stops resolving by name — see
    /// <see cref="TryResolveByDiscriminator"/>.</para>
    /// </summary>
    void Register(Type contentType, string? nodeTypePath = null);

    /// <summary>
    /// Resolves a <c>$type</c> discriminator (short or full name) to its CLR type — and REFUSES
    /// (returns <c>false</c>) a discriminator two different declarations have claimed.
    ///
    /// <para>🚨 <b>Why refusing beats answering.</b> A dynamically-compiled content type's
    /// discriminator is its bare CLR name, and that name is unique only inside its own package:
    /// one customer node repo ships <c>Currency</c> four times (<c>Reinsurance/Currency</c>,
    /// <c>ClaimsDeepfield/Currency</c>, <c>UWDeepfield/Currency</c>, <c>Ifrs17/Currency</c>) and
    /// eleven further names twice or more. With last-writer-wins, "the" <c>Currency</c> was
    /// whichever package's NodeType had compiled most recently — so one package's content
    /// deserialised into ANOTHER package's CLR record, dropping every member that record does not
    /// declare and materialising defaults it does. Which package won changed run to run, because
    /// registration happens at each type's compile, so the SAME input produced different results
    /// (Systemorph/MeshWeaver#1299).</para>
    ///
    /// <para>An unresolvable name leaves the content a <see cref="JsonElement"/> — honest,
    /// deterministic, and recoverable by <c>ContentAs&lt;T&gt;</c> at a consumer that knows which
    /// type it wants. A wrong answer is silent and lossy. <see cref="TryResolveByNodeType"/> stays
    /// exact and is unaffected: a caller that knows the node's NodeType always gets the right
    /// type.</para>
    /// </summary>
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
    /// unresolvable case. An AMBIGUOUS discriminator (two declarations, see
    /// <see cref="TryResolveByDiscriminator"/>) is one of the unresolvable cases.
    /// </summary>
    object? TryRecover(JsonElement content, JsonSerializerOptions options);
}

/// <summary>
/// Default <see cref="IMeshContentTypeRegistry"/>: two lock-free concurrent maps
/// (discriminator → claim, NodeType path → type). No mutable static state; one instance per mesh,
/// held as a DI singleton for the process lifetime.
/// </summary>
/// <param name="logger">Names an ambiguous discriminator once, with both declarations — the only
/// place the collision is visible, and the one thing an operator needs to fix it (rename one of the
/// two content records).</param>
public sealed class MeshContentTypeRegistry(ILogger<MeshContentTypeRegistry>? logger = null)
    : IMeshContentTypeRegistry
{
    /// <summary>
    /// One discriminator's claim: the CLR type, WHICH declaration claimed it, and whether a second,
    /// different declaration has since claimed the same name (making the name unusable).
    /// </summary>
    /// <param name="ContentType">The type the (first) declaration registered.</param>
    /// <param name="Declaration">The declaring identity — see <see cref="DeclarationOf"/>.</param>
    /// <param name="Ambiguous">Set once and never cleared: a later rebuild of either claimant must
    /// not "heal" a name that two packages genuinely share.</param>
    private sealed record DiscriminatorClaim(Type ContentType, string Declaration, bool Ambiguous);

    private readonly ConcurrentDictionary<string, DiscriminatorClaim> _byDiscriminator = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Type> _byNodeType = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// WHO declared a content type — the NodeType path when the caller knows it, otherwise the
    /// declaring assembly's simple name. For a runtime-compiled NodeType that assembly name is
    /// derived from the NodeType's own path (<c>NodeTypeRelease.GetSanitizedPath</c>), so it is
    /// stable across REBUILDS of one type and differs between two types that merely share a short
    /// name. That is exactly the distinction the ambiguity rule needs, and it needs no plumbing at
    /// the single <c>WithContentType</c> call site.
    /// </summary>
    private static string DeclarationOf(Type contentType, string? nodeTypePath) =>
        !string.IsNullOrEmpty(nodeTypePath)
            ? "nodeType:" + nodeTypePath
            : "assembly:" + (contentType.Assembly.GetName().Name ?? contentType.Assembly.FullName ?? "?");

    /// <inheritdoc />
    public void Register(Type contentType, string? nodeTypePath = null)
    {
        if (contentType is null)
            return;
        var declaration = DeclarationOf(contentType, nodeTypePath);
        ClaimDiscriminator(contentType.Name, contentType, declaration);
        if (contentType.FullName is { } fullName && !string.Equals(fullName, contentType.Name, StringComparison.Ordinal))
            ClaimDiscriminator(fullName, contentType, declaration);
        if (!string.IsNullOrEmpty(nodeTypePath))
            _byNodeType[nodeTypePath] = contentType;
    }

    private void ClaimDiscriminator(string discriminator, Type contentType, string declaration)
    {
        var claim = _byDiscriminator.AddOrUpdate(
            discriminator,
            _ => new DiscriminatorClaim(contentType, declaration, Ambiguous: false),
            (_, previous) => previous.Ambiguous
                // Already contested — keep it contested. A rebuild of one claimant does not make
                // the name unique again.
                ? previous
                // The SAME CLR type registered again — two NodeTypes sharing one compiled record
                // (a package's shared Source folder). One type, one answer: never ambiguous.
                : ReferenceEquals(previous.ContentType, contentType)
                    ? previous
                    : string.Equals(previous.Declaration, declaration, StringComparison.Ordinal)
                        // Same declaration, new identity: a REBUILD. The newest one is the answer.
                        ? new DiscriminatorClaim(contentType, declaration, Ambiguous: false)
                        : previous with { Ambiguous = true });
        if (claim.Ambiguous && !ReferenceEquals(claim.ContentType, contentType))
            WarnAmbiguous(discriminator, claim.Declaration, declaration);
    }

    // Once per (discriminator, second declaration): the collision is a permanent property of the
    // installed content, so repeating it on every recompile would be noise — but staying silent
    // would make "renders empty" unexplainable, since the refusal to resolve is deliberate.
    private readonly ConcurrentDictionary<string, byte> _warned = new(StringComparer.Ordinal);

    private void WarnAmbiguous(string discriminator, string first, string second)
    {
        if (logger is null || !_warned.TryAdd(discriminator + "|" + second, 0))
            return;
        logger.LogWarning(
            "Content type discriminator '{Discriminator}' is claimed by two different declarations "
            + "({First} and {Second}) — it can no longer be resolved by name, and content carrying it "
            + "stays an untyped JsonElement on any hub that has not registered the type itself. "
            + "Answering with one of the two would deserialise one package's content into the other's "
            + "record (members dropped, foreign defaults materialised) and the winner would depend on "
            + "compile order. Rename one of the two content records to make the name unique.",
            discriminator, first, second);
    }

    /// <inheritdoc />
    public bool TryResolveByDiscriminator(string discriminator, out Type contentType)
    {
        if (!string.IsNullOrEmpty(discriminator)
            && _byDiscriminator.TryGetValue(discriminator, out var claim)
            && !claim.Ambiguous)
        {
            contentType = claim.ContentType;
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
