using System.Text.Json;
using System.Text.Json.Nodes;

// Same reasoning as IMeshContentTypeRegistry.cs, which this rule was extracted from: the FILE lives
// in MeshWeaver.Messaging.Hub (the lowest assembly every caller can see) while the NAMESPACE stays
// MeshWeaver.Mesh.Services, so no `using` anywhere has to change.
namespace MeshWeaver.Mesh.Services;

/// <summary>
/// 🚨 <b>THE one rule for "is this stored content allowed to be read AS that type?"</b> — the guard
/// that stops a seam from RESHAPING content into a type the content itself disagrees with.
///
/// <para><b>Why a rule is needed at all.</b> <see cref="JsonSerializer"/> deserialising into a
/// concrete type SUCCEEDS on almost anything: <see cref="System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip"/>
/// discards every member the target does not declare and materialises defaults for the ones it
/// does. So handing it a mismatched type does not throw — it returns a plausible, wrong object. That
/// turned one bad registry entry into Systemorph/MeshWeaver#1379 (an instance served its NodeType's
/// definition as its own content, silently, on every read) and it is what
/// <c>NodeUpdatePipeline</c> did to a node whose stored content type differed from the proposal's
/// (#4597). An unresolvable read is honest and recoverable; a wrongly-typed one is neither.</para>
///
/// <para><b>Absent is not contradicting.</b> Content written without a <c>$type</c> (a plain JSON
/// object) has nothing to disagree with, so it is admitted — a caller that reached this rule has no
/// other evidence, and refusing would break every legitimate discriminator-less recovery.</para>
///
/// <para><b>The comparison is on the SHORT name, deliberately.</b> That is the framework's existing
/// recovery rule (<c>ObjectAsExtensions.As&lt;T&gt;</c>: "recover ONLY when the runtime type has the
/// SAME short name … a DIFFERENTLY-named type must stay null"). It keeps two packages that each ship
/// a <c>Currency</c> working — both carry <c>"$type":"Currency"</c> and each resolves to its OWN
/// package's CLR type — and it matches a rebuild into a new collectible assembly and a declaration
/// that moved namespace. Only a genuinely different record name is refused.</para>
/// </summary>
public static class ContentDiscriminator
{
    /// <summary>
    /// Whether <paramref name="content"/> — in ANY of the three shapes <c>MeshNode.Content</c>
    /// actually takes — does not CONTRADICT being read as <paramref name="candidate"/>.
    ///
    /// <para>A live CLR instance is its own authority: it admits <paramref name="candidate"/> when
    /// it IS one, or when it carries the same short name from another assembly (the collectible
    /// re-compile / cross-package case <c>ObjectAsExtensions.As</c> recovers). A
    /// <see cref="JsonElement"/> or <see cref="JsonNode"/> is judged by its own <c>$type</c>. Null
    /// content carries no claim and admits anything.</para>
    /// </summary>
    /// <param name="content">The stored content, typed or as JSON.</param>
    /// <param name="candidate">The type a caller proposes to read it as.</param>
    /// <returns><c>true</c> when nothing in the content contradicts <paramref name="candidate"/>.</returns>
    public static bool Admits(object? content, Type candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return content switch
        {
            null => true,
            JsonElement je => Admits(je, candidate),
            JsonNode jn => ShortNameMatches(DiscriminatorOf(jn), candidate),
            _ => candidate.IsInstanceOfType(content)
                 || string.Equals(content.GetType().Name, candidate.Name, StringComparison.Ordinal),
        };
    }

    /// <summary>
    /// Whether <paramref name="content"/>'s own <c>$type</c> does not CONTRADICT
    /// <paramref name="candidate"/>. Absent, non-string or empty ⇒ admitted (see the class doc).
    /// </summary>
    /// <param name="content">The stored content as JSON.</param>
    /// <param name="candidate">The type a caller proposes to read it as.</param>
    /// <returns><c>true</c> when the discriminator is absent or names the same record.</returns>
    public static bool Admits(JsonElement content, Type candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (content.ValueKind != JsonValueKind.Object
            || !content.TryGetProperty("$type", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.String)
            return true;
        return ShortNameMatches(typeProp.GetString(), candidate);
    }

    /// <summary>
    /// The record name a <c>$type</c> discriminator names — the segment after the last <c>.</c>, so
    /// an assembly-qualified or namespaced discriminator compares as the bare record does. Private:
    /// this is the rule's own vocabulary, and a public surface nothing calls is one more thing that
    /// can never be deleted (in-mesh callers are invisible to the compiler).
    /// </summary>
    private static string? ShortNameOf(string? discriminator)
    {
        if (discriminator is not { Length: > 0 })
            return null;
        var lastDot = discriminator.LastIndexOf('.');
        return lastDot >= 0 ? discriminator[(lastDot + 1)..] : discriminator;
    }

    /// <summary>The <c>$type</c> a DOM-shaped payload carries, or null when it carries none.</summary>
    private static string? DiscriminatorOf(JsonNode content)
        => content is JsonObject o
           && o.TryGetPropertyValue("$type", out var t)
           && t?.GetValueKind() == JsonValueKind.String
            ? t.GetValue<string>()
            : null;

    private static bool ShortNameMatches(string? discriminator, Type candidate)
        => ShortNameOf(discriminator) is not { } shortName
           || string.Equals(shortName, candidate.Name, StringComparison.Ordinal);
}
