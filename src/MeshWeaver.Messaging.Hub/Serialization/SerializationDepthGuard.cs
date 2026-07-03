using System.Text.Json;

namespace MeshWeaver.Messaging.Serialization;

/// <summary>
/// Carries JSON serialization depth across the NESTED serializer sessions the mesh's converter
/// chain spawns, so <see cref="JsonSerializerOptions.MaxDepth"/> semantics hold for the whole
/// object graph.
///
/// <para><b>Why this exists.</b> <see cref="ObjectPolymorphicConverter"/> (to inject the $type
/// discriminator) and <see cref="ImmutableDictionaryOfStringObjectConverter"/> (to serialize values
/// by runtime type) serialize sub-values through a fresh session
/// (<c>JsonSerializer.Serialize(value → string)</c> / <c>SerializeToNode</c>). Every fresh session
/// creates a fresh <see cref="Utf8JsonWriter"/> whose <see cref="Utf8JsonWriter.CurrentDepth"/>
/// restarts at 0, so no single writer ever approaches MaxDepth while the C# call stack grows by one
/// converter frame per object-graph edge. For a SELF-REFERENCING graph that recursion is unbounded:
/// the native stack exhausts before any depth guard trips, and the resulting StackOverflow kills
/// the whole process (SIGABRT / exit 134) — uncatchable by any try/catch. This guard restores the
/// invariant the per-session writers lost: the ACCUMULATED depth (all outer sessions + the current
/// writer) is checked before each nested session, and exceeding the effective MaxDepth throws a
/// diagnosable <see cref="JsonException"/> naming the type, exactly what MaxDepth is for.</para>
///
/// <para><b>Lifetime / state.</b> One instance is shared by the standard converters of ONE hub's
/// <see cref="JsonSerializerOptions"/> (created in <c>GetStandardConverters</c>) — instance state
/// that dies with the hub, never static. The counter is a <see cref="ThreadLocal{T}"/> because
/// serialization depth is a property of the current thread's call stack: the nested sessions run
/// synchronously on the caller's thread, and concurrent serializations on other threads must not
/// observe each other's depth.</para>
/// </summary>
public sealed class SerializationDepthGuard
{
    /// <summary>
    /// The effective depth limit when <see cref="JsonSerializerOptions.MaxDepth"/> is 0
    /// (System.Text.Json's documented default).
    /// </summary>
    public const int DefaultMaxDepth = 64;

    private readonly ThreadLocal<int> accumulatedDepth = new();

    /// <summary>
    /// Accounts for the depth already consumed by the current writer (plus the wrapper object the
    /// caller is about to emit) before a nested serializer session runs, throwing when the
    /// accumulated depth exceeds the options' effective MaxDepth. Dispose the returned scope when
    /// the nested session completes.
    /// </summary>
    /// <param name="writer">The writer of the CURRENT session, whose <see cref="Utf8JsonWriter.CurrentDepth"/>
    /// contributes the depth consumed since this session started.</param>
    /// <param name="options">The serializer options in effect; source of MaxDepth.</param>
    /// <param name="valueType">The type about to be serialized in the nested session — named in the
    /// error so a cycle is diagnosable.</param>
    /// <returns>A scope that restores the previous accumulated depth on dispose.</returns>
    /// <exception cref="JsonException">The accumulated depth exceeds the effective MaxDepth —
    /// the object graph is self-referencing (a cycle) or nested too deeply.</exception>
    public Scope Enter(Utf8JsonWriter writer, JsonSerializerOptions options, Type valueType)
    {
        var effectiveMaxDepth = options.MaxDepth > 0 ? options.MaxDepth : DefaultMaxDepth;
        var previous = accumulatedDepth.Value;
        var depth = previous + writer.CurrentDepth + 1;
        if (depth > effectiveMaxDepth)
            throw new JsonException(
                $"Serialization depth {depth} exceeds MaxDepth {effectiveMaxDepth} while serializing "
                + $"'{valueType}' — the object graph is self-referencing (a cycle) or nested too deeply. "
                + "The mesh wire format does not support cyclic object graphs; break the cycle, e.g. by "
                + "referencing a path/id instead of embedding the object.");
        accumulatedDepth.Value = depth;
        return new Scope(this, previous);
    }

    /// <summary>
    /// Restores the accumulated depth recorded at <see cref="Enter"/> when disposed.
    /// </summary>
    public readonly struct Scope(SerializationDepthGuard guard, int previous) : IDisposable
    {
        /// <summary>Restores the accumulated depth of the owning guard.</summary>
        public void Dispose() => guard.accumulatedDepth.Value = previous;
    }
}
