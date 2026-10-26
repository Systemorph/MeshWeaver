namespace MeshWeaver.Mesh;

/// <summary>
/// 🚨 <b>The IDENTITY of a content degradation — an exception object attached to the diagnostic,
/// deliberately never thrown.</b>
///
/// <para><b>What it marks.</b> A <c>MeshNode</c>'s <c>Content</c> arrived as JSON, the
/// polymorphic reader did not produce a typed value — its <c>$type</c> resolved to nothing, or it
/// resolved to a type the stored JSON does not bind to (<see cref="Cause"/> says which) — and the
/// mesh-wide <c>IMeshContentTypeRegistry</c> could not recover it either. The value crosses
/// the read boundary STILL untyped, so every downstream <c>Content is X</c> / <c>as X</c> silently
/// answers absent: a view renders empty, a reactive wait never completes. Nothing throws — a READ
/// must stay bad-data tolerant, and that is exactly why this class of defect is invisible.</para>
///
/// <para>🚨 <b>Why an exception for something that is not thrown.</b> Because carrying one is what
/// makes the diagnostic DURABLE, and durability here is a property of the CALL, not of the wording
/// or the level. A CI shard's authoritative sink — <c>_meshweaver-test-trace.log</c>, written by
/// <c>XUnitFileLogger.Log</c> → <c>TestTraceLog.AppendFault</c> — takes a record <b>if and only if</b>
/// <c>exception is not null &amp;&amp; logLevel &gt;= Warning</c>. The degradation warnings passed no
/// exception, so they could reach that file by construction never; and the job log carries only the
/// output of tests that FAILED, while a degradation is overwhelmingly logged under a test that
/// PASSES. The consequence was measured in MeshWeaver#3625: the shard gate
/// <c>check-untyped-content.sh</c>, which greps that very directory, had <b>no way to fire</b> — a
/// permanently green check, indistinguishable from a passing one.</para>
///
/// <para>This is the third recorded instance of the same trap (#890's <c>PROCESS CANNOT EMIT</c>,
/// #3413's <c>never rendered</c>, and this one) — see
/// <c>Doc/Architecture/ReadingCiSignals</c> → "for a fault the trace log is the sink".</para>
///
/// <para>🚨 <b>The gate keys on this TYPE NAME, not on the message.</b> A prose phrase is a
/// description of the event; the type is the event's identity, bound by the compiler at every
/// construction site. That gives the coupling two independent bindings instead of one: renaming the
/// type is a compile-wide change, and <c>UntypedContentDegradationGate</c> pins the script's key to
/// <c>nameof(MeshNodeContentDegradedException)</c> so a rename that forgets the script reds.</para>
///
/// <para>Constructing an exception without throwing it costs one allocation and captures no stack,
/// so <see cref="System.Exception.ToString"/> yields exactly <c>type: message</c> — which is the
/// whole record a reader needs here, since the fault is in the DATA and not in a call path.</para>
/// </summary>
public sealed class MeshNodeContentDegradedException : System.Exception
{
    /// <summary>The read seam that observed the degradation (e.g. <c>MeshNodeStreamCache.GetStream</c>).</summary>
    public string Seam { get; }

    /// <summary>The node whose content stayed untyped.</summary>
    public string? NodePath { get; }

    /// <summary>The node's NodeType, which is the key the content-type registry recovery is attempted under.</summary>
    public string? NodeType { get; }

    private readonly string? rawJson;

    /// <summary>
    /// The raw JSON a caller passed to the constructor. 🚨 Never part of <see cref="Message"/>: the
    /// message reaches Warning-level logs, which ship to the central log store outside row-level
    /// security, so the stored content of a node must not travel with it. Kept as a forwarder only
    /// so a compiled caller that reads it does not break; nothing in the platform reads it, and the
    /// platform's own sites pass <c>null</c>.
    /// </summary>
    [System.Obsolete("Raw content is no longer logged or carried by the platform — read Shape, which describes "
        + "the content's $type, keys, kind and length without its values.")]
    public string? RawJson => rawJson;

    /// <summary>
    /// The SHAPE of the content that could not be materialised — its <c>$type</c> (or that it has
    /// none), top-level property names, JSON kind and byte length, as
    /// <see cref="ContentShapes.Describe"/> renders it. Never a value. <c>null</c> when the site
    /// had no content to describe.
    /// </summary>
    public string? Shape { get; init; }

    /// <summary>
    /// WHY the content stayed untyped, as the site that observed it classified it — e.g. a
    /// <c>$type</c> nothing in the process resolves, or a registered type whose stored JSON does not
    /// bind (Systemorph/MeshWeaver#6674). <c>null</c> when the site did not classify it; the message
    /// is then cause-neutral rather than guessing.
    ///
    /// <para>🚨 An <c>init</c> property, deliberately not a constructor parameter: a new parameter
    /// replaces the constructor every already-compiled module bundle calls (a binary break), and a
    /// fifth <c>string</c> overload would make an existing <c>null</c> inner-exception argument
    /// ambiguous. The message is composed when it is READ, so a cause set by the initializer is the
    /// one <see cref="System.Exception.ToString"/> — what every sink appends — carries.</para>
    /// </summary>
    public string? Cause { get; init; }

    /// <summary>Creates the degradation marker.</summary>
    /// <param name="seam">The read seam that observed it.</param>
    /// <param name="nodePath">The node path.</param>
    /// <param name="nodeType">The node's NodeType.</param>
    /// <param name="rawJson">Deprecated — never logged; pass <c>null</c> and set <see cref="Shape"/>.
    /// The parameter stays because removing it would replace the constructor every already-compiled
    /// caller binds to.</param>
    /// <param name="inner">The deserialization exception, when there was one.</param>
    public MeshNodeContentDegradedException(
        string seam, string? nodePath, string? nodeType, string? rawJson, System.Exception? inner = null)
        : base(null, inner)
    {
        Seam = seam;
        NodePath = nodePath;
        NodeType = nodeType;
        this.rawJson = rawJson;
    }

    /// <summary>
    /// The record a sink appends. Names exactly ONE cause — <see cref="Cause"/> when the observing
    /// site classified it, otherwise a cause-neutral statement. Before #6674 this always said the
    /// discriminator resolved to no registered CLR type, so a bind failure the formatted line
    /// correctly named arrived with a second, contradicting cause attached.
    /// <para>🚨 It carries the content's <see cref="Shape"/>, never its values (see
    /// <see cref="ContentShapes"/>): this text is what every sink appends, and Warning-level sinks
    /// leave row-level security behind.</para>
    /// </summary>
    public override string Message =>
        $"{Seam}: content for '{NodePath ?? "<unknown>"}' (nodeType '{NodeType ?? "<none>"}') "
        + "stayed an untyped JsonElement — "
        + (Cause ?? "neither the reading hub's TypeRegistry nor the mesh-wide content-type registry "
                    + "produced a typed value (the cause was not classified at this site)")
        + ". Downstream 'Content is X' / 'as X' consumers read it as ABSENT: views render "
        + $"empty and reactive waits never complete. Shape: {Shape ?? "<not described>"}";
}
