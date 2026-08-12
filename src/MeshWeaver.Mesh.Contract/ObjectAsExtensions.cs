using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

/// <summary>
/// 🚨 THE way to read an <c>object</c>-typed payload as <c>T</c> anywhere on the
/// mesh. A plain <c>value is T</c> / <c>value as T</c> cast is the TRAP-DOOR: it is correct exactly
/// when the value happens to already be your CLR type, and silently yields <c>null</c> in the three
/// cases that actually occur in a running mesh —
///
/// <list type="number">
/// <item><b>Untyped JSON.</b> The polymorphic converter DEGRADES an unresolvable <c>$type</c> to a
///   raw <see cref="JsonElement"/> rather than throwing, so any hub whose TypeRegistry does not know
///   the discriminator hands you JSON, not an instance. <c>as T</c> → null.</item>
/// <item><b>The as-written DOM.</b> Application code builds payloads as <see cref="JsonObject"/>,
///   and a change notification forwards that shape verbatim until the materialization pipeline
///   re-types it. <c>as T</c> → null.</item>
/// <item><b>A same-named type from another assembly.</b> Every recompile of a dynamic NodeType mints
///   a new collectible assembly, so "the same" record has a different CLR identity per build — and
///   another hub's registry can resolve the discriminator to ITS copy at a query boundary.
///   <c>as T</c> → null.</item>
/// </list>
///
/// <para>Each of those has cost production an outage, and each looked identical from the outside:
/// the value reads as absent, the view renders empty, and a reactive wait never completes — no
/// exception, no log, nothing to grep. That is why this is not a style preference. <b>If a value is
/// declared <c>object</c> (or <c>object?</c>) and you want it as <c>T</c>, call
/// <see cref="As{T}"/> and pass <c>hub.JsonSerializerOptions</c>.</b></para>
///
/// <para>🚨 <b>FIRST, though: deserialize as close as possible to where the type IS registered —
/// which usually means the RIGHT HUB should be handling this at all.</b> A <c>$type</c> resolves
/// against the TypeRegistry behind the options you pass, so a payload read on a hub that never
/// registered the type is untyped by construction, and <see cref="As{T}"/> is then recovering from
/// a routing mistake rather than a data problem. The durable fix for a repeated degradation is to
/// move the work to the owning hub (the per-node hub for its own content, the hub that declares the
/// type via <c>WithType</c>) — or to register the type where the read happens. Reach for this
/// accessor at genuine boundaries — a cross-hub query result, a control payload, storage JSON — not
/// as a way to keep reading foreign content on the wrong hub.</para>
///
/// <example>
/// <code>
/// // ❌ the trap-door — null the moment the value arrives untyped or from another build
/// if (delivery.Message.Payload is MyPayload p) { … }
/// var settings = control.Data as MySettings;
///
/// // ✅ bad-data tolerant, and it says why when it cannot convert
/// var p = delivery.Message.Payload.As&lt;MyPayload&gt;(hub.JsonSerializerOptions);
/// var settings = control.Data.As&lt;MySettings&gt;(hub.JsonSerializerOptions, logger);
/// </code>
/// </example>
///
/// <para>For a <see cref="MeshNode"/>'s content use <c>node.ContentAs&lt;T&gt;(options)</c> — it is
/// this same conversion with the node's path in the diagnostics.</para>
/// </summary>
public static class ObjectAsExtensions
{
    /// <summary>
    /// <paramref name="value"/> as <typeparamref name="T"/>: already-typed → as-is; a degraded
    /// <see cref="JsonElement"/> or <see cref="JsonNode"/> → deserialized; typed with a SAME-NAMED
    /// but different CLR type → recovered by a JSON round-trip; typed with a DIFFERENTLY-named type
    /// → <c>null</c>, so probe-dispatch call sites keep working; anything unconvertible → <c>null</c>,
    /// logged loud and never silently swallowed.
    /// </summary>
    /// <typeparam name="T">The target type.</typeparam>
    /// <param name="value">The object-typed payload.</param>
    /// <param name="options">Always <c>hub.JsonSerializerOptions</c>, and preferably the OWNING
    /// hub's: the registry behind those options is what resolves a <c>$type</c>, so reading with a
    /// hub that never registered the type degrades the value right back to JSON.</param>
    /// <param name="logger">Optional; without it an unconvertible value is still returned as null,
    /// just without the diagnosis.</param>
    /// <param name="what">What is being read, for the log line (a node path, a control name…).</param>
    public static T? As<T>(
        this object? value,
        JsonSerializerOptions options,
        ILogger? logger = null,
        string? what = null)
        where T : class
    {
        switch (value)
        {
            case null:
                return null;
            case T typed:
                return typed;
            case JsonElement je:
                try
                {
                    return je.Deserialize<T>(options);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
                {
                    // Don't rethrow (a throw on read faults the caller), don't swallow: log loud
                    // with the raw JSON so the corruption is visible.
                    //
                    // The catch set matches the foreign-type branch below on purpose. A missing
                    // converter throws NotSupportedException and an unsupported target shape throws
                    // InvalidOperationException — neither derives from JsonException, so catching
                    // JsonException alone would let a read fault its caller, which is the one thing
                    // this accessor exists to prevent.
                    logger?.LogError(ex, "As<{TargetType}> could not recover {What}: {RawJson}",
                        typeof(T).Name, what ?? "value", Excerpt(je.GetRawText()));
                    return null;
                }
            case JsonNode jn:
                try
                {
                    return jn.Deserialize<T>(options);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
                {
                    logger?.LogError(ex, "As<{TargetType}> could not recover {What}: {RawJson}",
                        typeof(T).Name, what ?? "value", Excerpt(jn.ToJsonString()));
                    return null;
                }
            default:
                // A typed object, but not OUR T. Recover ONLY when the runtime type has the SAME
                // short name: the same class compiled into a different dynamic node assembly, or a
                // same-short-named type another hub's registry resolved at a boundary.
                //
                // A DIFFERENTLY-named type must stay null — call sites probe-dispatch on exactly
                // that (`x.As<Index>() ?? treat x as the item itself`), and shape-recovering across
                // unrelated types once broke every API-token login.
                if (value.GetType().Name == typeof(T).Name)
                {
                    try
                    {
                        // Serialize AS the concrete runtime type: the object-declared path routes
                        // through ObjectPolymorphicConverter.GetTypeName → an UNGUARDED GetOrAddType
                        // that would adopt the foreign type into the caller's registry as a read
                        // side effect. The concrete-type path goes through
                        // PolymorphicTypeInfoResolver, whose collectible-assembly guard formats the
                        // discriminator WITHOUT registering.
                        var element = JsonSerializer.SerializeToElement(value, value.GetType(), options);
                        var recovered = element.Deserialize<T>(options);
                        if (recovered is not null)
                        {
                            // Debug, not Warning: this fires per value per read on a healthy,
                            // recoverable cross-assembly path — Warning would storm at snapshot size.
                            logger?.LogDebug(
                                "As<{TargetType}> for {What}: recovered value typed as foreign "
                                + "{ActualType} ({ActualAssembly}) via JSON round-trip",
                                typeof(T).Name, what ?? "value", value.GetType().Name,
                                value.GetType().Assembly.GetName().Name);
                            return recovered;
                        }
                    }
                    catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
                    {
                        // fall through to the loud log below
                    }
                }

                // Assembly-qualified on purpose: dynamic node assemblies compile without namespaces,
                // so the bare type name is identical on BOTH sides of a cross-assembly mismatch —
                // only the assembly identity makes this diagnosable.
                logger?.LogError(
                    "As<{TargetType}> for {What}: value is {ActualType} ({ActualAssembly}), not convertible",
                    typeof(T).Name, what ?? "value", value.GetType().FullName,
                    value.GetType().Assembly.GetName().Name);
                return null;
        }
    }

    /// <summary>
    /// The head of a payload, for a diagnostic log line. Bounded because the value being logged is
    /// by definition the one we could NOT parse — a degraded content blob or a whole snapshot can be
    /// megabytes, and these lines ship to the log store. The first 2 KB is what a human reads to
    /// recognise the shape; the tail never adds diagnosis, it only adds cost.
    /// </summary>
    private static string Excerpt(string raw) =>
        raw.Length <= RawJsonLogLimit
            ? raw
            : $"{raw[..RawJsonLogLimit]}… [{raw.Length - RawJsonLogLimit} more chars]";

    private const int RawJsonLogLimit = 2048;
}
